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
        Join-Path $scriptDir '..\probes\report_length_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_bt_axis_role_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\foreign_devnode_survival_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xbox_combined_trigger_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xbox_gip_trigger_resolver_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usb_composite_schema_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usbip_server_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usbip_bundle_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\usbip_e2e_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_feature_gate_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\switch2_pro_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\switch2_pro_sdl3_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\sony_extra_buttons_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\vr_controller_smoke\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_persona_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_wire_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_sdl_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_raw_path_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xusb_wgi_single_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_steam_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_multi_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\identity_derivation_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\identity_battery_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\xusb_battery_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\driver_catalog_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\valve_firmware_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\ds4_report_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\ds3_sixaxis_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\switch2_composite_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\registry_ownership_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\wgi_mapping_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\vr_driver_conflict_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\kbm_offline_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\kbm_live_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\coexist_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        # The copy that runs inside Windows Sandbox is published from the same
        # build into sandbox\, so this one's freshness is that one's too.
        Join-Path $scriptDir '..\probes\usbip_install_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        # The NativeAOT and single-file copies carry no separate DLL. They
        # are published from the same build as this one.
        Join-Path $scriptDir '..\probes\aot_smoke\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\json_parity_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\pnp_notify_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        # The copy in variant\ is a different build of the SDK on purpose.
        Join-Path $scriptDir '..\probes\build_info_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\switch_pro_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\switch_descriptor_idle_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
        Join-Path $scriptDir '..\probes\switch_pro_sdl3_check\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
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

# Get-HMPresentDevnodes and Get-HMLeftovers: every HIDMaestro controller
# devnode in the PRESENT state, proved by its hardware ID under any
# enumerator, and what a scenario left of them. devnode_owner_check.ps1
# checks both offline.
. (Join-Path $scriptDir 'HMDevnodes.ps1')

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
    # quit and cleanup: long enough to trigger Stop-HMTestProcess KILL
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
        # its own: no KILL budget, no scaled timeout. If the test process
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

# Hard-kill a test process and everything it started, as a crash or Task
# Manager's End process tree does. Windows PowerShell 5.1 runs on .NET
# Framework, whose Process.Kill takes no argument and kills one process,
# so taskkill /T does it. cmd keeps taskkill's own output and an error for
# a process already gone out of PowerShell's error stream.
function Stop-HMProcessTree {
    param([System.Diagnostics.Process]$Proc)
    if ($Proc.HasExited) { return }
    & cmd.exe /c "taskkill /PID $($Proc.Id) /T /F >nul 2>&1"
    $null = $Proc.WaitForExit(5000)
}

# Wait until the test process has created its initial controllers and the
# devnodes hold still. The test app reads stdin only once every initial
# controller exists, and it acknowledges a command after running it, so
# the acknowledgment of a resume marks the batch as created. Resume
# changes nothing while no slot is paused, and no scenario pauses one.
function Wait-Ready {
    param([System.Diagnostics.Process]$Proc)
    Send-Cmd -Proc $Proc -Cmd 'resume'
    Wait-CascadeSettle
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
    # Get-HMLeftovers already returns one array. Wrapping its output in
    # @() would make that array the single element of another, so an
    # empty result would count as one leftover.
    $leftovers = Get-HMLeftovers -Baseline $baseline -After $after

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
# ────────────────────────────────────────────────────────────────────
#  Invoke-Probe
# ────────────────────────────────────────────────────────────────────

# Run a probe with its stdout captured, and on failure print the tail so
# the log says WHICH assertion went red rather than only an exit code.
# S53 failed once in a full run and passed every isolated reproduction;
# with no captured output there was nothing to diagnose it from, which is
# how a one-off becomes a guess. The transcript is kept next to the probe
# so a passing run leaves the evidence too.
function Invoke-Probe {
    param(
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$Message,
        [int[]]$SkipCodes = @(),
        # Passed to the probe as its command line. Two scenarios that run
        # one probe with different arguments also pass a distinct -Tag, so
        # each keeps its own transcript.
        [string]$Arguments = '',
        [string]$Tag = ''
    )
    $probe = Resolve-ProbeBinary $Dir $Exe
    $name = [IO.Path]::GetFileNameWithoutExtension($Exe)
    if ($Tag) { $name += '_' + $Tag }
    $out = Join-Path $env:TEMP ("HIDMaestro_probe_" + $name + ".log")
    $err = "$out.err"
    $start = @{
        FilePath               = $probe
        PassThru               = $true
        NoNewWindow            = $true
        Wait                   = $true
        RedirectStandardOutput = $out
        RedirectStandardError  = $err
    }
    # Start-Process rejects an empty -ArgumentList, so it is passed only
    # when there is something to pass.
    if ($Arguments) { $start.ArgumentList = $Arguments }
    $p = Start-Process @start
    Resolve-ProbeExit -ExitCode $p.ExitCode -Out $out -Exe $Exe -Message $Message -SkipCodes $SkipCodes
}

# What Invoke-Probe does once the probe has exited: a skip code prints
# [SKIP], and any other exit but 0 prints the probe's [FAIL] lines, or its
# tail, and throws.
function Resolve-ProbeExit {
    param([int]$ExitCode, [string]$Out, [string]$Exe, [string]$Message, [int[]]$SkipCodes = @())
    if ($SkipCodes -contains $ExitCode) {
        Write-Host ('    [SKIP] probe reported exit ' + $ExitCode) -ForegroundColor Yellow
        return
    }
    if ($ExitCode -ne 0) {
        if (Test-Path $Out) {
            $lines = @(Get-Content $Out -ErrorAction SilentlyContinue)
            $failed = @($lines | Where-Object { $_ -match '\[FAIL\]' })
            if ($failed.Count -gt 0) {
                Write-Host '    probe failures:' -ForegroundColor Red
                foreach ($l in $failed) { Write-Host ('      ' + $l.Trim()) -ForegroundColor Red }
            } else {
                Write-Host '    probe tail:' -ForegroundColor Red
                foreach ($l in ($lines | Select-Object -Last 15)) { Write-Host ('      ' + $l.Trim()) -ForegroundColor Red }
            }
            Write-Host ('    full transcript: ' + $Out) -ForegroundColor Yellow
        }
        throw ($Exe + ' exited ' + $ExitCode + ' - ' + $Message)
    }
}

# Starts a probe whose work runs on a machine of its own, so it can run
# beside the scenarios ahead of its slot: S73's Windows Sandbox. The slot
# waits for it with Complete-BackgroundProbe and judges it as Invoke-Probe
# would. A probe that cannot start fails at its slot, not here.
function Start-BackgroundProbe {
    param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][string]$Exe)
    try {
        $probe = Resolve-ProbeBinary $Dir $Exe
        $out = Join-Path $env:TEMP ('HIDMaestro_probe_' + [IO.Path]::GetFileNameWithoutExtension($Exe) + '.log')
        $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
        # The exit code reads back only through a handle taken while the
        # process runs.
        $null = $p.Handle
        return @{ Process = $p; Out = $out; Exe = $Exe; Error = $null }
    } catch {
        return @{ Error = $_ }
    }
}

function Complete-BackgroundProbe {
    param([Parameter(Mandatory)][hashtable]$Run, [Parameter(Mandatory)][string]$Message, [int[]]$SkipCodes = @())
    if ($Run.Error) { throw $Run.Error }
    $Run.Process.WaitForExit()
    Write-Host ('    ran beside the scenarios ahead of it, {0:N1} s' -f ($Run.Process.ExitTime - $Run.Process.StartTime).TotalSeconds)
    Resolve-ProbeExit -ExitCode $Run.Process.ExitCode -Out $Run.Out -Exe $Run.Exe -Message $Message -SkipCodes $SkipCodes
}

function Scenario-Single-360-BT-360 {
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Wait-Ready -Proc $proc

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
        Wait-Ready -Proc $proc

        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);   Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S3: Long cycle, 8 swaps. Stress-tests the per-call unique suffix
# scheme (v1.1.30) and the SwDevice teardown path repeatedly. Each swap
# starts on a settled device tree: after the app's acknowledgment the
# present devnodes read the same three times 50 ms apart (scaled), where
# Wait-CascadeSettle reads them 200 ms apart. Polled every 10 ms for 1.5 s
# after each of 40 acknowledgments on the bench (the resume and four
# runs of the eight swaps), the set never changed once the app had
# acknowledged, since the SDK's swap returns after Windows has removed
# the old device and created the new one.
function Scenario-Single-LongCycle {
    $sequence = @(
        $Profiles.XboxBT, $Profiles.Xbox360,
        $Profiles.XboxBT, $Profiles.Xbox360,
        $Profiles.XboxBT, $Profiles.Xbox360,
        $Profiles.XboxBT, $Profiles.Xbox360
    )
    function Wait-Settled {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $prev = $null
        $count = 0
        while ($sw.ElapsedMilliseconds -lt (HMScaleMs 60000)) {
            $cur = ((Get-HMPresentDevnodes) | Sort-Object) -join '|'
            if ($cur -eq $prev) {
                $count++
                if ($count -ge 2) { return }
            } else {
                $count = 0
                $prev = $cur
            }
            Start-Sleep -Milliseconds (HMScaleMs 50)
        }
    }
    $proc = Start-HMTestProcess -ProfileIds @($Profiles.Xbox360)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.Xbox360)) { throw "initial create timeout" }
        Send-Cmd -Proc $proc -Cmd 'resume'
        Wait-Settled
        foreach ($p in $sequence) {
            Send-Cmd -Proc $proc -Cmd ('0 ' + $p)
            Wait-Settled
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
        Wait-Ready -Proc $proc

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
        Wait-Ready -Proc $proc

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
        Wait-Ready -Proc $proc

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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
    Wait-Ready -Proc $proc1
    # Hard-kill: no quit, no clean dispose. Simulates user closing
    # PadForge from Task Manager / a crash.
    Stop-HMProcessTree -Proc $proc1
    $proc1.WaitForExit(5000) | Out-Null
    Wait-CascadeSettle

    # Now run a clean session that should clean up the orphans on
    # startup and leave a clean post-state on quit.
    $proc2 = Start-HMTestProcess -ProfileIds @($Profiles.DualSense)
    try {
        Wait-Ready -Proc $proc2
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
    Wait-Ready -Proc $proc1
    Stop-HMTestProcess -Proc $proc1 | Out-Null
    Wait-CascadeSettle  # wait for the prior proc's teardowns to fully drain

    # Same profiles, fresh process. Should NOT hit empty-shell state on
    # subsequent runs.
    $proc2 = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT, $Profiles.Xbox360)
    try {
        Wait-Ready -Proc $proc2
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
        Wait-Ready -Proc $proc

        # Queue 4 swap commands with no inter-command sleep. Each one
        # processes after the prior LiveSwap returns synchronously; no
        # external pacing.
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360)
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360)
        # Send-Cmd returns once the app has run each swap, so all four
        # are done here.
        Wait-CascadeSettle
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc

        # xbox-360-arcade-stick: same 045E:028E as xbox-360-wired.
        Send-Cmd -Proc $proc -Cmd '0 xbox-360-arcade-stick';     Wait-CascadeSettle
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.Xbox360);    Wait-CascadeSettle
    } finally {
        Stop-HMTestProcess -Proc $proc | Out-Null
    }
}

# S17: Force-kill mid-cascade. The trickiest force-kill timing: kill
# the test process while a Series BT teardown is still in flight.
# Phantoms left by a hard kill at this exact moment have the worst
# chance of surviving the next session's RemoveAllVirtualControllers.
#
# Send-Cmd waits for the app's acknowledgment, which comes once the swap
# has finished, so the swap goes in without waiting for it, and the kill
# lands the moment the present devnodes first change: the BT pad's HID
# child has gone and its teardown is under way. On the bench that change
# came 43 to 129 ms after the command and the app was dead 13 to 17 ms
# later, inside the teardown, where the whole swap takes about 700 ms. A
# tree kill through taskkill took 1.4 to 1.6 s to land and missed the
# swap, so the app goes first, as a crash takes it, and its tree after.
# The app reads stdin only once its controller exists, and an xinputhid
# create returns once XInput reports the pad's slot, so the resume's
# acknowledgment is the bind the old 6 s sleep waited for. After the kill
# the present devnodes last changed 31 ms later on the bench, so three
# reads 50 ms apart that agree replace the 5 s sleep, which stays the
# limit.
function Scenario-ForceKill-MidCascade {
    function Get-PresentKey { ((Get-HMPresentDevnodes) | Sort-Object) -join '|' }
    $proc1 = Start-HMTestProcess -ProfileIds @($Profiles.XboxBT)
    try {
        if (-not (Wait-CreateBound -ProfileId $Profiles.XboxBT)) { throw "initial create timeout" }
        Wait-Ready -Proc $proc1

        # Kick off swap-back-to-360 without waiting for it.
        $before = Get-PresentKey
        Send-Cmd -Proc $proc1 -Cmd ('0 ' + $Profiles.Xbox360) -NoWaitAck
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt (HMScaleMs 5000)) {
            if ((Get-PresentKey) -ne $before) { break }
            Start-Sleep -Milliseconds 5
        }
    } finally {
        # Hard-kill while teardown still in flight.
        if (-not $proc1.HasExited) { try { $proc1.Kill() } catch {} }
        Stop-HMProcessTree -Proc $proc1
        $proc1.WaitForExit(5000) | Out-Null
    }

    # Wait for any in-flight kernel work to complete or stall.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $prev = $null
    $count = 0
    while ($sw.ElapsedMilliseconds -lt (HMScaleMs 5000)) {
        $cur = Get-PresentKey
        if ($cur -eq $prev) {
            $count++
            if ($count -ge 2) { break }
        } else {
            $count = 0
            $prev = $cur
        }
        Start-Sleep -Milliseconds (HMScaleMs 50)
    }

    # Fresh session should reliably clean up whatever was left in mid-
    # cascade state.
    $proc2 = Start-HMTestProcess -ProfileIds @($Profiles.SwitchPro)
    try {
        Wait-Ready -Proc $proc2
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
        Wait-Ready -Proc $proc

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
        Wait-Ready -Proc $proc

        # Queue 4 swaps for 4 different slots with no inter-command sleep.
        Send-Cmd -Proc $proc -Cmd ('0 ' + $Profiles.XboxBT)
        Send-Cmd -Proc $proc -Cmd ('1 ' + $Profiles.DualSense)
        Send-Cmd -Proc $proc -Cmd ('2 ' + $Profiles.SwitchPro)
        Send-Cmd -Proc $proc -Cmd ('3 ' + $Profiles.XboxOneBT)
        # Send-Cmd returns once the app has run each swap, so all four
        # are done here.
        Wait-CascadeSettle
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
        Wait-Ready -Proc $proc
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
    Invoke-Probe -Dir 'sony_bt_report31_check' -Exe 'SonyBtReport31Check.exe' `
                 -Message 'extended input encoder produced unexpected bytes for dualsense-bt-full'
}

# S30: Sony BT extendedOutputReport round-trip. Validates the inverse direction
# of the v1.3.5 vendor-blob codec: HMOutputEncoder.Encode(profile, fields) produces
# wire-format bytes whose declared fields land at the right offsets, the CRC32
# matches an independent computation with prefix [0xA2, 0x31], and decode(encode(x))
# preserves every declared field's value byte-for-byte.
function Scenario-Sony-BT-Output-Decode-Check {
    Invoke-Probe -Dir 'sony_bt_output_decode_check' -Exe 'SonyBtOutputDecodeCheck.exe' `
                 -Message 'output codec did not round-trip cleanly'
}

# S31: full Sony data-driven coverage. Validates the v1.3.5 vendor-blob blocks
# across the full Sony catalog: DS4 BT (Report 0x11) input + output round-trip
# with CRC32 prefix [0xA1, 0x11] / [0xA2, 0x11], DS5 USB (Report 0x02) output,
# and DS4 USB (Report 0x05) output. Closes the v1.3.5 scope on every Sony
# variant the SDK ships - DS5 BT, DS5 BT (full), DS5 Edge BT, DS4 v2 BT, DS5
# USB, DS5 Edge USB, DS4 v1 USB, DS4 v1 USB (full), DS4 v2 USB.
function Scenario-Sony-Data-Driven-Coverage {
    Invoke-Probe -Dir 'sony_data_driven_coverage' -Exe 'SonyDataDrivenCoverage.exe' `
                 -Message 'DS4 BT / USB output codec did not round-trip cleanly'
}

# S32: v1.3.5 features + regression check. Combined probe covering every
# fix from the issue #21 PadForge spot-check rounds:
#   Round 3: USB Sony profiles can't trigger the codec hot path
#             (regression: per-frame VendorBlobCodec.EncodeInput at 250 Hz
#             on USB DS5 caused stick-jerkiness on ds.daidr.me)
#   Round 5: DS5 sensor byte positions (gyro/accel/sensorTimestamp shift +1
#             because Linux dualsense_input_report struct excludes report_id)
#   Round 6: DS4 BT armOn IDs are DS4-canonical (0x02/0xA3, not DS5's
#             0x05/0x09/0x20)
#   Round 7: DS4 BT versionNumber=0 so Chromium's
#             Dualshock4Controller::BusTypeFromVersionNumber routes vibration
#             through Report 0x11 BT instead of Report 0x05 USB
#   Round 8: DS5 Edge USB activeProfile=0x80 via inputDefaults overlay,
#             BT Edge same constant declared as a uint8 codec field with
#             initial=128 ordered before the CRC32 entry so it participates
#             in the checksum
#   Round 8b: SDK plumbing: ControllerProfile.InputDefaults +
#              InputBytePatch list, applied in BOTH SubmitState's legacy
#              branch AND SubmitRawReport (PadForge's USB path calls both
#              and the raw bytes would otherwise clobber the overlay)
#   Round 8c: Edge USB inputDefaults extended to bytes 50/51/52 = 0 to
#              override PadForge's SonyReportPackers Timer 2 counter at
#              data[48..51] (was leaking the counter's middle bytes into
#              triggerLevel and triggering isStickModuleLost ~94% of frames)
#   Feature 1: touchpad-finger / int16-le / uint32-le / bitfield /
#               uint8-battery codec types + sensor/touchpad/battery byte
#               positions in DS5 USB+BT
#   Feature 2: audio block declared in all 9 Sony extendedOutputReport
#               specs (DS5: headphoneVolume/speakerVolume/micVolume/
#               audioControlFlags; DS4: stereo headphoneVolumeLeft/Right
#               + micVolume/speakerVolume)
#   Feature 3: DS5 BT btTag uint8-rolling stride 16 (cycles 0x00..0xF0
#               then wraps; per-controller HMController.EncodeOutput
#               threads the auto-advancing counter)
function Scenario-V135-Features-Check {
    Invoke-Probe -Dir 'v135_features_check' -Exe 'V135FeaturesCheck.exe' `
                 -Message 'one or more v1.3.5 fix invariants regressed (see probe stdout)'
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
    Invoke-Probe -Dir 'trigger_classifier_check' -Exe 'TriggerClassifierCheck.exe' `
                 -Message 'one or more trigger-classifier invariants regressed (see probe stdout)'
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
    Invoke-Probe -Dir 'trigger_live_check' -Exe 'TriggerLiveCheck.exe' `
                 -Message 'end-to-end trigger wire fidelity regressed (see probe stdout)'
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
    Invoke-Probe -Dir 'sidewinder_ffb_check' -Exe 'SideWinderFfbCheck.exe' `
                 -Message 'SideWinder PID FFB end-to-end regressed (see probe stdout)'
}

# S36: arbitrary-axis addressing: HMAxis enum / ExtraAxes / AvailableAxes /
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
    Invoke-Probe -Dir 'axis_addressing_check' -Exe 'AxisAddressingCheck.exe' `
                 -Message 'HMAxis / ExtraAxes / AvailableAxes / AddAxis fidelity regressed (see probe stdout)'
}

# S37: layout audit (v1.3.9). For every catalog profile, verifies the
# layout block (when authored) schema-validates against the descriptor and
# every axis/button reference resolves. For profiles WITHOUT a layout
# block, asserts the classifier-fallback Sticks/Triggers lists are
# coherent, and the presentation plan of every catalog profile and of the
# builder cases (enumerator, hardware and compatible IDs, the companion
# and the steps around it) equals the probe's golden record but for the
# changes its change list names. Then every catalog report descriptor's
# input, output and feature lengths against hid-tools', the report and
# length the driver is given for each profile, and hand-made descriptors
# for Push and Pop, long items and the other items a descriptor walker can
# get wrong, and every catalog descriptor through Windows' own parser, the
# machine's hidparse.sys loaded into the probe, which refuses at the start
# what it refuses there. Those parts need no device. Last, live, with
# three controllers read in one window: genesis-nso as 0x3F, never 0x21,
# and microsoft-designer-keyboard's raw frame as its keyboard report 0x01
# (Raw Input), beside dualshock-4-v2's unchanged 0x01. Then the 5-byte
# wingman report. Then ps-move and xbox-360-flight-stick: each
# collection's HidP_GetCaps against the captured descriptor, and the
# input report with the trigger where the capture puts it. In session 0,
# as over SSH, Raw Input lists no device, and the keyboard's two Raw Input
# checks print [SKIP] while the rest run.
function Scenario-Layout-Audit-Check {
    Invoke-Probe -Dir 'layout_audit_check' -Exe 'LayoutAuditCheck.exe' `
                 -Message 'layout schema/descriptor mismatch (see probe stdout)'
    Invoke-Probe -Dir 'report_length_check' -Exe 'ReportLengthCheck.exe' `
                 -Message 'a report length or ID the SDK reads from a descriptor differs from the independent parser, or Windows refuses a catalog descriptor (see probe stdout)'
    Invoke-Probe -Dir 'report_length_check' -Exe 'ReportLengthCheck.exe' -Arguments 'live' -Tag 'live' `
                 -Message 'the driver sent a data-only frame under another report or at another length (see probe stdout)'
}

# S38: Sony BT axis-role regression (v1.3.13, #23). Asserts the Sony
# Bluetooth profiles' Profile.Sticks/Triggers resolve to the Sony-convention
# axis keys (right stick Z/Rz, triggers Rx/Ry) and that a Sony-convention
# state.Axes fill encodes through VendorBlobCodec with no right-stick/trigger
# swap. No driver install, no virtual device.
function Scenario-Sony-BT-Axis-Role-Check {
    Invoke-Probe -Dir 'sony_bt_axis_role_check' -Exe 'SonyBtAxisRoleCheck.exe' `
                 -Message 'Sony BT axis roles swapped or mis-resolved (see probe stdout)'
}

# S39: Foreign devnode survival regression (v1.3.16, #28). Creates a foreign
# root-enumerated HIDClass devnode with HardwareID `root\VID_1234&PID_BEAD&
# REV_0222` (the vJoy shape), runs a full HM CreateController + Dispose
# cycle, then asserts the foreign device was not disabled, uninstalled,
# renamed, or had ControllerIndex injected into its Device Parameters by
# any of the now-HwId-gated sweep paths in DeviceOrchestrator /
# DeviceNodeCreator / DeviceProperties / DeviceManager.
function Scenario-Foreign-Devnode-Survival-Check {
    Invoke-Probe -Dir 'foreign_devnode_survival_check' -Exe 'ForeignDevnodeSurvivalCheck.exe' `
                 -Message 'HM sweep mutated a foreign root devnode (issue #28; see probe stdout)'
}

# S40: Xbox 360 combined-Z + split-trigger regression (v1.3.17, PadForge #130).
# Loads the real xbox-360-wired profile and asserts the v1.3.9 state.Axes
# refactor's combined-Z synthesis sources trigger values from the canonical
# user-facing positions (HMAxis.Z / HMAxis.Rz or axisMap-declared), then
# writes BOTH the combined Z byte AND the separate Vx / Vy bytes so
# joy.cpl sees the dinput-combined trigger and WGI sees the raw LT / RT.
function Scenario-Xbox-Combined-Trigger-Check {
    Invoke-Probe -Dir 'xbox_combined_trigger_check' -Exe 'XboxCombinedTriggerCheck.exe' `
                 -Message 'Xbox 360 split-trigger workaround regressed (PadForge#130; see probe stdout)'
}

# Companion to S40. S40 exercises the HID descriptor lane (BuildReportInto's
# combined-Z synthesis). S41 exercises the GIP-buffer lane that drives every
# non-DirectInput API via the XUSB companion. The PadForge#130 follow-up
# (MultipadTester XInput / WGI / RawInput stuck at 0.5 even though joy.cpl
# was correct) was missed by S40 because S40 doesn't touch SubmitState's
# mlt / mrt resolution. S41 covers it.
function Scenario-Xbox-Gip-Trigger-Resolver-Check {
    Invoke-Probe -Dir 'xbox_gip_trigger_resolver_check' -Exe 'XboxGipTriggerResolverCheck.exe' `
                 -Message 'GIP-side trigger resolver regressed (PadForge#130 follow-up; see probe stdout)'
}

# --------------------------------------------------------------------
#  Composite USB personas and the bundled transport (issue #39)
# --------------------------------------------------------------------

function Scenario-Usb-Composite-Schema {
    Invoke-Probe -Dir 'usb_composite_schema_check' -Exe 'UsbCompositeSchemaCheck.exe' `
                 -Message 'a composite persona no longer matches its hardware dump, or a composite leaked into the UMDF2 path (see probe stdout)'
}

# S43 runs the probe twice: the checks that need no device (the audio
# engine, the connection owner lookup, the location match and the copy of
# gamecube-adapter the protocol GET_REPORT section imports), then the
# protocol against a live import. Both runs
# always run, and a failure of either is reported.
function Scenario-Usbip-Server-Protocol {
    $failed = @()
    try {
        Invoke-Probe -Dir 'usbip_server_check' -Exe 'UsbipServerCheck.exe' -Arguments '--offline' -Tag 'offline' `
                     -Message ('the USB/IP audio engine regressed offline: a wake event, event or timer left open at Dispose or when a thread does not start, ' +
                               'an exception thrown to the connection when its event thread does not start, a stale IsStreaming, ' +
                               'a change made before Dispose not raised, more than one waiting write per control, ' +
                               'a failed start missing from the diagnostic log, its event thread not counted as its own, ' +
                               'a Dispose that waits out the input pump, a wrong connection owner, ' +
                               'an import service string matched to the wrong port, or a gamecube-adapter copy with getReport "input" that does not load or compile (see probe stdout)')
    } catch { $failed += $_.Exception.Message }
    try {
        Invoke-Probe -Dir 'usbip_server_check' -Exe 'UsbipServerCheck.exe' `
                     -Message 'the USB/IP wire contract regressed (descriptors, HID both directions, isochronous pacing, unlink, a teardown racing Dispose, the import handshake, or a Dispose slower than 250 ms at p95), see probe stdout'
    } catch { $failed += $_.Exception.Message }
    if ($failed.Count -gt 0) { throw ($failed -join ' AND ') }
}

function Scenario-Usbip-Bundle-Deploy {
    Invoke-Probe -Dir 'usbip_bundle_check' -Exe 'UsbipBundleCheck.exe' `
                 -Message 'the bundled transport is missing, its hash no longer matches the pinned SHA-256, or the deploy path stopped refusing tampered bytes (see probe stdout)'
}

# The only scenario that needs the transport actually deployed. On a
# machine without it, the probe deploys it (that IS the thing under
# test). Exit 2 means the probe declined to run for an environmental
# reason and is reported as a skip rather than a failure.
# S46: Sony feature reports through the REAL UMDF2 driver. Two things at
# once: the calibration payload (#43) must be non-degenerate on the driver
# lane exactly as it is on the composite lane, and the Sony stub block must
# stay gated on VID 0x054C so a non-Sony profile declaring feature 0x02
# still gets its own report. The probe existed since the D3 audit but was
# never wired in, so neither property was gated before now.
function Scenario-Sony-Feature-Gate {
    Invoke-Probe -Dir 'sony_feature_gate_check' -Exe 'SonyFeatureGateCheck.exe' `
                 -Message 'Sony calibration is degenerate on the driver lane, or the VID gate regressed (see probe stdout)'
}

function Scenario-Switch2-Pro {
    Invoke-Probe -Dir 'switch2_pro_check' -Exe 'Switch2ProCheck.exe' `
                 -Message 'the Switch 2 Pro profile drifted from the two references it was reconstructed from (VIIPER ns2pro and SDL_hidapi_switch2), or the 12-bit stick packing / d-pad-as-buttons encoding regressed (see probe stdout)'
}

# S48: the Switch 2 Pro through a real SDL3, which is what v1.5.0 was
# withdrawn for asserting untested. Gates three separable properties that
# each broke once: the pad reaches SDL's joystick layer at all (it falls
# through to the Windows backend after SDL's own switch2 driver declines
# it), every button and axis carries live input (the profile streams its
# vendor report from the first frame rather than waiting for a handshake
# that never comes), and the profile's sdlMapping promotes it to the
# gamepad layer with the right roles. Skips itself when the sibling
# SDL3-build checkout is absent, which is the normal state on the Atom.
# S49: the DualSense mic mute and the Edge's four extras (issue #48).
# Wire-byte asserts on both encode paths against the layout SDL,
# DS4Windows, ds5-edge-relay and dualsense-tester agree on, plus the
# sentinel that stopped HMButton.Share aliasing onto PS. Offline, no
# device.
# S50: the virtual VR controller subsystem (issues #32, #51, #55).
# Phase 1 always runs and pins the C# and C++ halves of the HIDMaestroVR
# IPC protocol to each other byte-for-byte, with the probe playing the
# driver's role, holds a HapticReceived handler past Dispose's join of the
# haptic reader in a child, and in another has a handler dispose its own
# controller. Phase 2 runs when a SteamVR install is
# present (BOTH
# battery machines carry the Steam-free steamcmd install at C:\SteamVR,
# with the headless null-HMD rig config): registers the embedded OpenVR
# driver, boots the stack, and asserts controller enumeration, hand
# roles, legacy axis types, the haptic round trip, the consumer-restart
# cycle, and the #55 legacy GetControllerState lane via a spawned
# scene-app reader. A [SKIP] of phase 2 on a battery machine is a rig
# fault, not a pass: both machines are provisioned with SteamVR, so the
# skip reason must be read, the rig repaired, and the scenario re-run.
function Scenario-Vr-Controller-Smoke {
    Invoke-Probe -Dir 'vr_controller_smoke' -Exe 'VrControllerSmoke.exe' `
                 -Message 'the VR IPC protocol drifted between the C# and C++ mirrors, the OpenVR driver failed to register/load in SteamVR, the controllers stopped enumerating, or the haptic round trip broke (see probe stdout)'
}

function Scenario-Sony-Extra-Buttons {
    Invoke-Probe -Dir 'sony_extra_buttons_check' -Exe 'SonyExtraButtonsCheck.exe' `
                 -Message 'the Sony extra-button wire placement drifted: mic mute (0x04), Edge paddles/Fn (0x10-0x80) in the third buttons byte, or the -1 buttonMap sentinel regressed and Share aliases onto PS again (see probe stdout)'
}

function Scenario-Switch2-Pro-Sdl3 {
    Invoke-Probe -Dir 'switch2_pro_sdl3_check' -Exe 'Switch2ProSdl3Check.exe' `
                 -Message 'SDL no longer sees the Switch 2 Pro as a full gamepad: either it stopped reaching the joystick layer, an input stopped arriving, or the profile''s sdlMapping drifted from its descriptor''s button and axis order (see probe stdout)'
}

# S51: the three Valve composite personas (issue #56, from PadForge
# discussion #337) - the Steam Deck, the 2015 Steam Controller and the
# 2026 one SDL calls Triton. Pins their wire truth with no device and no
# elevation: the multi-HID-interface descriptor sets (each controller
# interface plus the keyboard and mouse its lizard mode drives), endpoint
# addresses and intervals against the real units' lsusb dumps, the report
# ids Triton's single interface carries, and the feature-stub tables that
# answer Steam's GET_REPORT interrogation.
function Scenario-Valve-Personas {
    Invoke-Probe -Dir 'valve_persona_check' -Exe 'ValvePersonaCheck.exe' `
                 -Message 'a Valve persona drifted: a descriptor set, an endpoint, a declared report id, the feature-stub answers Steam reads, or the 64-byte Neptune input frame (see probe stdout)'
}

# S52: the three Valve personas on the wire. Each is created for real,
# driven through SubmitState - the same call any consumer makes - and the
# frame is read back off the HID stack and checked against that device's
# own wire format (SteamDeckStatePacket_t, ValveControllerStatePacket_t,
# TritonMTUFull_t). S51 pins the descriptors and feature answers with no
# device; this pins that input actually reaches a consumer, which is the
# half that shipped broken. Submits no buttons, so nothing reaches the
# desktop even with a Steam desktop layout bound to the device.
function Scenario-Valve-Wire {
    Invoke-Probe -Dir 'valve_wire_check' -Exe 'ValveWireCheck.exe' `
                 -Message 'a Valve persona stopped emitting correct input frames (see probe stdout)'
}

# S53: the three Valve personas through STOCK upstream SDL3. S52 decodes
# each frame with arithmetic copied out of SDL's drivers; this one hands the
# device to a real SDL3.dll built from libsdl-org/SDL and asks SDL what it
# sees. Those Steam drivers are the decoders Steam Input is built on, so a
# persona SDL binds, names and reads is one a Valve-aware consumer reads.
# Every rear button, both trackpad clicks and QAM land on the SDL gamepad
# button SDL documents for them, and the stick and grip touch sensors on its
# capacitive-sense readings. SKIPs when the sibling SDL3-build/build-stock
# checkout is absent. The fork beside it deliberately skips HIDMaestro
# devices and is never used here.
function Scenario-Valve-Sdl {
    Invoke-Probe -Dir 'valve_sdl_check' -Exe 'ValveSdlCheck.exe' `
                 -Message 'stock SDL stopped reading a Valve persona: enumeration, the Valve driver binding, sticks, triggers, rear buttons, trackpads, touch sensors or motion (see probe stdout)' -SkipCodes 2
}

# S54: the three Valve personas against the REAL Steam client. S53 proves
# SDL's Steam drivers read them, and those drivers are what Steam Input is
# built on, but that is an argument rather than a measurement. This one
# measures it: each persona is handed to a running Steam client and Steam's
# own controller log is read back for the claim, the protocol it picked and
# the per-model config set it loaded (neptune, steamcontroller_gordon,
# triton), plus the absence of the hid_read-failure close an idle device
# used to produce. Starts Steam with -silent and shuts it back down when it
# started it. Submits nothing, so nothing can reach the desktop through a
# Steam desktop layout. SKIPs when no Steam client is installed.
function Scenario-Valve-Steam {
    Invoke-Probe -Dir 'valve_steam_check' -Exe 'ValveSteamCheck.exe' `
                 -Message 'Steam stopped claiming a Valve persona, stopped classifying it as that model, or dropped it after opening it (see probe stdout)' -SkipCodes 2
}

# S55: several Valve personas at once. S51-S54 each drive one device, so
# neither the three-different case nor the two-of-the-same case is covered
# by them. The second is the one that was broken: the Deck's serial comes
# from a real unit and Steam keys per-controller config off it, so a pair
# of Decks reporting one serial shared one configuration. Verified through
# stock SDL. SKIPs without a stock SDL3 build beside the repo.
function Scenario-Valve-Multi {
    Invoke-Probe -Dir 'valve_multi_check' -Exe 'ValveMultiCheck.exe' `
                 -Message 'Valve personas stopped coexisting: three models at once, two of one model, or the per-instance serial collapsed back to one value (see probe stdout)' -SkipCodes 2
}

# S56: the Triton raw path lands on the controller report (issue #58).
# The steam-controller-2 descriptor carries a lizard-mode mouse (0x40) and
# keyboard (0x41) ahead of its controller state (0x42), reproduced from the
# real hardware. SubmitRawReport prepended the FIRST report id, so every
# frame a consumer submitted arrived as a mouse report and tore the host's
# cursor across the screen. This reads the frames back off the HID stack
# and asserts the report id on all three submit forms, with a device-free
# negative control proving the old position rule still picks the mouse.
function Scenario-Valve-Raw-Path {
    Invoke-Probe -Dir 'valve_raw_path_check' -Exe 'ValveRawPathCheck.exe' `
                 -Message 'the Triton raw path stopped landing on its declared report id, or a frame went out as the lizard mouse again (see probe stdout)'
}

# S57: XUSB-companion virtuals are ONE WGI gamepad (issue #59, from
# PadForge discussion #378). A virtual Xbox 360 registered twice in
# Windows.Gaming.Input, so shell surfaces read every press twice. WGI
# only honors the xinputhid tripwire on a re-arrival, so creation now
# stamps it into the pre-registration property state AND restarts the
# main devnode once in step 5. Three fresh cycles assert one Gamepad,
# one RawGameController, one XUSB interface, XInput intact, and the
# 10-button descriptor unchanged (the xinputhid synthesis trap). Thirty
# quick create-and-dispose cycles under one identity key then assert that
# no XInput slot reads connected again after Dispose returns.
function Scenario-Xusb-Wgi-Single {
    $probe = Resolve-ProbeBinary 'xusb_wgi_single_check' 'XusbWgiSingleCheck.exe'
    $p = Start-Process -FilePath $probe -PassThru -NoNewWindow -Wait
    if ($p.ExitCode -ne 0) {
        throw ("XusbWgiSingleCheck exited " + $p.ExitCode + " - a 360-family virtual doubled in WGI again, lost its tripwire, lost XInput, its HID child stopped reporting 10 buttons, or its XInput slot came back after Dispose (see probe stdout)")
    }
}

# S45: a composite persona end to end through the real USB stack (issue
# #39), then a sweep with that controller still alive. The probe's offline
# run, which needs no device, checks that the sweep leaves the sections a
# live controller's threads and submits use, and that a teardown stops a
# protocol tick or a PID call before it unmaps. Both runs always run, and a
# failure of either is reported.
function Scenario-Usbip-E2E-Composite {
    $failed = @()
    try {
        Invoke-Probe -Dir 'usbip_e2e_check' -Exe 'UsbipE2ECheck.exe' -Arguments '--offline' -Tag 'offline' `
                     -Message ('a sweep released a live controller''s sections under its idle repeat, protocol clock, output reader, a submit or a PID call, ' +
                               'a protocol tick or a PID call wrote a section after the teardown, or StopThreads left a thread running (see probe stdout)')
    } catch { $failed += $_.Exception.Message }
    try {
        Invoke-Probe -Dir 'usbip_e2e_check' -Exe 'UsbipE2ECheck.exe' `
                     -Message 'a composite persona failed end to end through the real USB stack (enumeration, the owner of the import connection, an import past full handshake slots, HID, audio endpoints, teardown, an attach racing the stale sweep or another import, a FramesReceived handler that disposed its controller and waited for the audio pump, or a sweep with a live controller), see probe stdout' -SkipCodes 2
    } catch { $failed += $_.Exception.Message }
    if ($failed.Count -gt 0) { throw ($failed -join ' AND ') }
}

# S58: identity derivation (issue #60). No device. The default key
# reproduces the index-shaped ids, a consumer key derives deterministic
# and collision-free ids, and the USB serial a persona serves follows
# the identity: synthesized for the Sony composites, captured and varied
# for the Valve ones. Then the 0xCA rival scan on recorded device trees: a
# parent on the identity's ParentIdPrefix, or with its token, under any
# ROOT enumerator HIDMaestro creates parents under is removed, refused when
# its owner runs, and refused untouched when HIDMaestro does not own it,
# and every enumerator a catalog profile's plan makes a parent under is
# walked by the full cleanup's Device Parameters and HID orphan phases.
function Scenario-Identity-Derivation {
    Invoke-Probe -Dir 'identity_derivation_check' -Exe 'IdentityDerivationCheck.exe' `
                 -Message 'identity derivation changed: a default id, a keyed id, or a persona serial no longer derives as documented, or the ParentIdPrefix rival scan missed a rival (see probe stdout)'
}

# S59: identity battery (issue #60). One controller per family across
# five lives (two same-process cycles, two process restarts, one driver
# uninstall and reinstall) must keep its parent instance id,
# ParentIdPrefix, ContainerId, HID children, interface paths, DirectInput
# GUID, SDL3 path and USB serial. Every life also passes the empty-shell
# check (Service, interface, input, output, one WGI Gamepad and one XInput
# slot for the Xbox families). Plus overlap and a profile change at the
# same key. The reboot life is a manual `--compare` run against the
# baseline this scenario writes.
function Scenario-Identity-Battery {
    Invoke-Probe -Dir 'identity_battery_check' -Exe 'IdentityBatteryCheck.exe' `
                 -Message 'a virtual controller changed identity across its lives, or came back as an empty shell (see probe stdout)' -SkipCodes 2
}

# S60: the XUSB battery reply (issue #61). The reply carries a two-byte
# version word before the type and level, and packing them one byte early
# read back as NIMH at EMPTY, which SDL shows as a flat battery on every
# virtual pad. Three cycles assert the raw four bytes position by position,
# what XInputGetBatteryInformation hands a caller, and the SDL mapping over
# those values.
function Scenario-Xusb-Battery {
    Invoke-Probe -Dir 'xusb_battery_check' -Exe 'XusbBatteryCheck.exe' `
                 -Message 'a virtual pad no longer reports a wired, full battery to XInput (see probe stdout)' -SkipCodes 2
}

# S61: the driver catalog per architecture (issue #63). No device. Stages
# the x64 and the ARM64 payload with the Inf2Cat tree exactly as
# EnsureExtracted does and runs the embedded Inf2Cat with the /os value
# GenerateCatalogs picks for each, requiring one catalog per INF. v1.9.0
# asked for 10_ARM64, which Inf2Cat rejects, so no ARM64 deploy reached
# the install. Controls: that value is rejected, and each architecture's
# INFs are refused under the other's value.
function Scenario-Driver-Catalog {
    Invoke-Probe -Dir 'driver_catalog_check' -Exe 'DriverCatalogCheck.exe' `
                 -Message 'an architecture''s driver INFs no longer catalog with the value GenerateCatalogs passes Inf2Cat (see probe stdout)'
}

# S62: the 2026 Steam Controller persona against Steam's own firmware
# updater (issue #62). The updater lists a controller whose build stamp
# differs from the one its config names, and Steam notifies for each one
# listed. The persona is enumerated with Steam's build and not listed. The
# same persona answering with the v1.9.0 capture is listed, in the same
# run. Only the updater's read-only queries run.
function Scenario-Valve-Firmware {
    Invoke-Probe -Dir 'valve_firmware_check' -Exe 'ValveFirmwareCheck.exe' `
                 -Message 'Steam''s firmware updater offers the 2026 Steam Controller persona an update (see probe stdout)' -SkipCodes 2
}

# S63: DualShock 4 input reports against SDL and Linux (issue #64). Each
# of the five DS4 maps is encoded and read back at the offsets SDL's
# PS4StatePacket_t and Linux's dualshock4_input_report_common use, with the
# Bluetooth CRC, battery, touch and sensor timestamp. The live Bluetooth
# persona is armed with a feature read of 0x02 and its report 0x11 read off
# the HID stack, then handed to stock SDL3, which must keep reading input
# after it arms the persona, read 1 g and no rotation at rest, and read a
# near-zero trigger as released. SKIPs only the SDL part when the sibling
# SDL3-build/build-stock checkout is absent.
function Scenario-Ds4-Report {
    Invoke-Probe -Dir 'ds4_report_check' -Exe 'Ds4ReportCheck.exe' `
                 -Message 'a DualShock 4 report no longer matches what SDL and Linux read (see probe stdout)' -SkipCodes 2
}

# S64: the DualShock 3 with pressure-sensitive buttons (PadForge discussion
# 476). dualshock-3-full presents the form sixaxis.sys and DsHidMini's SXS
# mode present, which PCSX2 and RPCS3 read pressure from on Windows. The
# native report is checked against SDL, Linux and RPCS3 offsets; the live
# persona's joystick report and report 0 feature reply against DsHidMini's
# conversions; RPCS3's read sequence and output reports; and stock SDL3
# with PCSX2's sixaxis hint, which must see a PS3 controller with 16 axes
# and 11 buttons and read every pressure axis. SKIPs only the SDL part when
# the sibling SDL3-build/build-stock checkout is absent.
function Scenario-Ds3-Sixaxis {
    Invoke-Probe -Dir 'ds3_sixaxis_check' -Exe 'Ds3SixaxisCheck.exe' `
                 -Message 'the DualShock 3 persona no longer reads as a sixaxis.sys DS3 with pressure, or the native one stopped decoding its output report (see probe stdout)' -SkipCodes 2
}

# S65: the Switch 2 Pro Controller composite persona (issue #66). The pad
# as a two-interface USB device: HID on interface 0 and a vendor bulk pair
# on interface 1, where a host sends the command protocol that starts it.
# The probe first plays the driver's side of the wire against a capture of
# a real pad: both descriptors, the Microsoft OS 1.0 requests, silence until
# command 03/0D, the command table, the flash image, one report every 4 ms,
# reports 0x09 and 0x05 and the motion gate. The live persona must then
# come up with HidUsb on interface 0 and WinUSB on interface 1 from its own
# descriptors, answer over WinUSB and decode rumble. The PadForge fork's
# SDL3 with libusb must open it as a Nintendo Switch Pro Controller and read
# 1 g and 100 deg/s back within 1 percent. Two personas must each answer
# their own handle. Steam must open it and keep reading it. SKIPs the SDL
# parts without the sibling SDL3-build/build-unfiltered build, which is that
# fork with its HIDMaestro filter off, and the Steam part without a Steam
# client.
function Scenario-Switch2-Composite {
    Invoke-Probe -Dir 'switch2_composite_check' -Exe 'Switch2CompositeCheck.exe' `
                 -Message 'the Switch 2 Pro composite persona no longer matches a real pad on the wire, lost its WinUSB binding, or stopped satisfying SDL or Steam (see probe stdout)' -SkipCodes 2
}

# S66: registry keys a virtual shares with real devices of the same VID and
# PID (foundation-sunshine #1138). The probe plants a real pad's joy.cpl OEM
# key with OEMName, OEMData and Axes, OEM keys of pads the run never creates
# under the vendor prefixes the cleanup used to delete, a vendor's GameInput
# mapping and a table HIDMaestro wrote through v1.11.0, then creates and disposes
# virtuals of those identities, runs the full cleanup and a fresh context,
# and diffs. It then follows the GameInput table a code-built game pad
# holds, HMOemNameOverride against a live virtual in all four orders of
# claim and release, and a consumer killed with its virtual alive.
function Scenario-Registry-Ownership {
    Invoke-Probe -Dir 'registry_ownership_check' -Exe 'RegistryOwnershipCheck.exe' `
                 -Message 'a registry key a real device owns was changed, or a key HIDMaestro wrote outlived its virtual (see probe stdout)'
}

# S67: what WGI reads from a virtual pad (foundation-sunshine #1056). The
# XUSB Xbox 360 pad is the same-window positive control. A DualSense must
# stay a RawGameController with no GameInput key, as a real one is. A
# DualShock 4 v1 must read correctly at rest and on every control through
# the mapping Windows ships for it. A game pad built in code must hold the
# table while it lives, read correctly through it and drop it on dispose.
# A joystick built in code gets neither.
function Scenario-Wgi-Mapping {
    Invoke-Probe -Dir 'wgi_mapping_check' -Exe 'WgiMappingCheck.exe' `
                 -Message 'WGI reads a virtual pad differently from the real device, or a GameInput key outlived its virtual (see probe stdout)'
}

# S68: another OpenVR driver registered under HIDMaestro's driver name,
# and HIDMaestro's own registration. OpenVR requires the name to be unique.
# HMVR.FindDriverNameConflicts lists every other folder SteamVR can see
# under it. The SDK writes the path registry itself, byte for byte as
# SteamVR's vrpathreg.exe does, and never runs that tool. The probe points
# VR_PATHREG_OVERRIDE at path registries of its own, so the real one is
# never touched. Elevated, it also points discovery at a SteamVR folder
# whose vrpathreg.exe is not a program and registers, and checks the write
# has the user's rights and no more and leaves the calling thread as it
# found it. It restores the two HKLM values it changes, and skips that part
# (exit 2) while SteamVR runs.
function Scenario-Vr-Driver-Conflict {
    Invoke-Probe -Dir 'vr_driver_conflict_check' -Exe 'VrDriverConflictCheck.exe' `
                 -Message 'a foreign OpenVR driver under the hidmaestro name went unreported, HIDMaestro reported its own, or its OpenVR registration differed from vrpathreg''s bytes, ran a file from the SteamVR folder, or wrote with more than the user''s rights (see probe stdout)' `
                 -SkipCodes 2
}

# S69: keyboard and mouse descriptors and packers, no devices (issue #67).
# Builder recipes that worked before build the same bytes, the new
# starters and their guards, the packers against bytes worked out by
# hand, and every catalog keyboard and mouse parsed, packed and read back.
function Scenario-Kbm-Descriptors {
    Invoke-Probe -Dir 'kbm_offline_check' -Exe 'KbmOfflineCheck.exe' `
                 -Message 'a keyboard or mouse descriptor parsed or packed wrong (see probe stdout)'
}

# S70: keyboards and mice through Raw Input (issue #67). Four mice with one
# VID and PID get four stable paths, bursts and four threads at 250 Hz lose
# no counts, absolute and extra-button mice, a keyboard's keys and LEDs, a
# consumer usage, every catalog keyboard and mouse starts as its kind, and a
# killed consumer's keys come up and its devices, keyboard included, go in
# the cleanup. Moves the cursor and puts it back, and presses Scroll Lock
# twice.
#
# Exit 3, a skip: the probe runs in session 0, as over SSH, where Raw Input
# lists no device. Exit 2, a running SDK consumer, stays a failure.
function Scenario-Kbm-Raw-Input {
    Invoke-Probe -Dir 'kbm_live_check' -Exe 'KbmLiveCheck.exe' `
                 -Message 'a virtual keyboard or mouse reached Raw Input wrong, lost input, or did not start (see probe stdout)' -SkipCodes 3
}

# S71: programs that embed HIDMaestro, side by side on one machine. Each
# program is a peer process the probe drives, and the probe judges what
# they did from outside both. Its header lists every section.
function Scenario-Coexistence {
    Invoke-Probe -Dir 'coexist_check' -Exe 'CoexistCheck.exe' -Arguments 'ABCDEFGHJKLMNOPQRSTUVWXYZ' -Tag 'coexistence' `
                 -Message 'two programs that embed HIDMaestro disturbed each other (see probe stdout)' -SkipCodes 2
}

# S72: everything the elevated SDK runs or hands to pnputil is staged under
# %ProgramFiles%\HIDMaestro\Staging in directories only Administrators and
# SYSTEM can write, a planted writable staging tree is repaired before use,
# and a staged file with the right length but the wrong bytes is replaced
# from the embedded copy. Section I of the coexistence probe, then its
# offline checks: whether a directory may be reset where it is, the refusal
# of one that may not be and cannot move, T6 at timeout scales 1, 1.5 and
# 2, the pin's opens and holds, the deploy's tools running from held files,
# the refusal of a foreign folder above Staging, the prune's keep-or-delete
# decision, and diagnostic files written as the user.
function Scenario-Protected-Staging {
    Invoke-Probe -Dir 'coexist_check' -Exe 'CoexistCheck.exe' -Arguments 'I' -Tag 'staging' `
                 -Message 'a staged file the elevated SDK runs was writable by a non-administrator or not checked by hash (see probe stdout)' -SkipCodes 2
    Invoke-Probe -Dir 'coexist_check' -Exe 'CoexistCheck.exe' -Arguments 'offline' -Tag 'staging-offline' `
                 -Message 'the offline staging checks failed (see probe stdout)'
}

# S73: the usbip-win2 install on a clean machine, in Windows Sandbox started
# without a window. A composite create on a machine without usbip-win2 runs
# the bundled installer and leaves no Start menu folder, desktop shortcut,
# GUI, SDK or PDB file, while the composite enumerates and carries input. A
# machine holding usbip-win2's uninstall entry without its driver package
# gets a refusal instead of the installer, which would run that entry's
# uninstaller first. Two programs whose first composites arrive together
# install once: one host controller, both composites up. Exit 2 when Windows
# Sandbox is not available. The sandbox is a machine of its own, so the
# battery starts this probe before S01 and the slot collects its result.
function Scenario-Usbip-Clean-Install {
    $message = 'the bundled usbip-win2 install left a shortcut, Start menu folder, GUI, SDK or PDB file, broke the composite, ran over an existing install, or ran twice for two programs at once (see probe stdout)'
    if ($script:S73Run) {
        Complete-BackgroundProbe -Run $script:S73Run -Message $message -SkipCodes 2
    } else {
        Invoke-Probe -Dir 'usbip_install_check' -Exe 'UsbipInstallCheck.exe' -Message $message -SkipCodes 2
    }
}

# The smoke apps the aot_smoke probe publishes for this machine's
# architecture.
function Get-SmokeArch {
    if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'x64' }
}

# S74: HIDMaestro.Core in a NativeAOT app. The whole catalog loads, the
# driver installs, a UMDF2 controller and a USB/IP composite each deliver the
# submitted state to a HID read and raise OutputReceived for an output report,
# and both go away on dispose.
function Scenario-NativeAot-Smoke {
    Invoke-Probe -Dir 'aot_smoke' -Exe ('aot-' + (Get-SmokeArch) + '\AotSmoke.exe') -Arguments 'aot' -Tag 'aot' `
                 -Message 'HIDMaestro.Core failed in a NativeAOT app (see probe stdout)' -SkipCodes 2
}

# S75: the same in a trimmed, self-contained, single-file app.
function Scenario-SingleFile-Smoke {
    Invoke-Probe -Dir 'aot_smoke' -Exe ('trim-' + (Get-SmokeArch) + '\AotSmoke.exe') -Arguments 'trim' -Tag 'trim' `
                 -Message 'HIDMaestro.Core failed in a trimmed single-file app (see probe stdout)' -SkipCodes 2
}

# S76: source-generated JSON reads every profile into the same objects
# v1.12.0's reflection-based reading did. No device.
function Scenario-Json-Parity {
    Invoke-Probe -Dir 'json_parity_check' -Exe 'JsonParityCheck.exe' `
                 -Message 'a profile reads differently through source-generated JSON than v1.12.0 read it (see probe stdout)'
}

# S77: PnP notifications reach the SDK's create and dispose waits. Without
# them every wait still finishes, on its 50 ms re-checks, so only the count
# taken inside the SDK's callback shows they arrive. The count is split by
# filter kind so the device-interface filter is checked on its own. First,
# with no device, the removal test and the registration gate get recorded
# sequences through the SDK's hooks: a removal is complete, and its id may
# be registered again, only after the REMOVED notification and the PHANTOM
# record's removal, and no locate cancels a removal. The live part then
# requires every removal of three profiles' disposes to end with its devnode
# gone, and each devnode in the tree when its removal test started to leave
# on that notification.
function Scenario-Pnp-Notify {
    Invoke-Probe -Dir 'pnp_notify_check' -Exe 'PnpNotifyCheck.exe' -Arguments 'offline' -Tag 'offline' `
                 -Message 'the removal test or the registration gate answered before a removal was complete (see probe stdout)'
    Invoke-Probe -Dir 'pnp_notify_check' -Exe 'PnpNotifyCheck.exe' `
                 -Message 'PnP notifications stopped reaching the SDK''s waits (see probe stdout)' -SkipCodes 2
}

# S78: what a build of the SDK carries. The release build must carry the
# driver and the USB/IP transport for x64 and ARM64 and the OpenVR driver,
# with no device involved. The probe's project build also makes a variant
# with HMSkipArm64 and HMSkipUsbip from a tree without an ARM64 or OpenVR
# driver. That variant must embed nothing stale, refuse what it lacks before
# any machine-wide step, and still run a UMDF2 controller on x64.
function Scenario-Build-Info {
    Invoke-Probe -Dir 'build_info_check' -Exe 'BuildInfoCheck.exe' -Arguments 'complete' -Tag 'complete' `
                 -Message 'the release build does not carry every payload, or carries Inf2Cat files Inf2Cat never loads (see probe stdout)'
    Invoke-Probe -Dir 'build_info_check' -Exe 'variant\BuildInfoCheck.exe' -Arguments 'variant' -Tag 'variant' `
                 -Message 'a build without the ARM64, USB/IP or OpenVR payload reported or refused the wrong thing (see probe stdout)' -SkipCodes 2
}

# S79: the Wii U GameCube adapter persona's bytes. Every catalog profile
# compiles as a device model, and copies of the persona with one mistake
# each (an unknown field type or semantic, a field wider than its block, a
# report past the extended lane, a rule longer than a packet, a null) are
# refused before anything machine-wide with a message naming the profile,
# the JSON path and the limit. The three 057E:0337 profiles carry the
# corrected descriptors and one Full, and the persona's device,
# configuration, string and report descriptors are a real WUP-028's as the
# persona serves them, as Windows enumerates them before any driver loads,
# and as libusb reads them through WinUSB after BindWinUsb.
function Scenario-GameCube-Descriptors {
    Invoke-Probe -Dir 'gc_descriptor_check' -Exe 'GcDescriptorCheck.exe' `
                 -Message 'the GameCube adapter persona served a byte a real WUP-028 does not (see probe stdout)' -SkipCodes 2
}

# S80: the adapter's protocol through WinUSB, as libusb hosts drive it:
# SET_PROTOCOL, no input before 0x13, each port's input, connection and
# rumble apart from the others, the origins, stalls and ignored commands,
# 0x15 and the port reset, and the 8 ms and 1 ms report rates. Runs the
# generic port and protocol model in process on exact clock ticks too.
function Scenario-GameCube-Protocol {
    Invoke-Probe -Dir 'gc_protocol_check' -Exe 'GcProtocolCheck.exe' `
                 -Message 'the GameCube adapter persona answered a command, a poll or a port change as a real WUP-028 does not (see probe stdout)' -SkipCodes 2
}

# S81: BindWinUsb puts WinUSB on one virtual adapter and no real one: the
# package ranks below input.inf, a second adapter stays on input.inf, and
# the bound devnode carries no reinstall flag. Dispose, and the reclaim
# after a killed program, flag the instance once its devnode is gone, so
# the next adapter on it installs by rank. An adapter arrives before the
# next program's reclaim and after it: one present before keeps WinUSB
# until it leaves.
function Scenario-GameCube-WinUsb {
    Invoke-Probe -Dir 'gc_winusb_check' -Exe 'GcWinUsbCheck.exe' `
                 -Message 'the WinUSB binding reached a device it should not, or was not put back (see probe stdout)' -SkipCodes 2
}

# S82: Dolphin's and Slippi's own adapter code, compiled unmodified with the
# libusb each pins, reads all four ports, sends rumble to each wired port
# alone and none to a WaveBird, and sees a port leave and return. Measures
# submit-to-read latency and the delivered report rate at 8 ms and 1 ms.
function Scenario-GameCube-Dolphin {
    Invoke-Probe -Dir 'gc_dolphin_check' -Exe 'GcDolphinCheck.exe' `
                 -Message 'Dolphin or Slippi read the GameCube adapter persona wrong (see probe stdout)' -SkipCodes 2
}

# S83: the official SDL3 3.4.18 lists nothing for the adapter beside a
# DualShock 4 it lists, and SDL 3.4.18 built with libusb lists one gamepad
# per port with SDL's mapping, refuses rumble on a WaveBird and without
# rumble power, and sends each port's rumble to that port. Measures latency
# and the report rate as S82 does.
function Scenario-GameCube-Sdl {
    Invoke-Probe -Dir 'gc_sdl_check' -Exe 'GcSdlCheck.exe' `
                 -Message 'SDL listed, mapped or drove the GameCube adapter persona wrong (see probe stdout)' -SkipCodes 2
}

# S84: no submit path allocates: SubmitState and SubmitRawReport on a
# DualShock 4, SubmitState on an Xbox 360, SubmitKeyboard, SubmitMouse, and a
# GameCube adapter port's SubmitState and the controller's, each called
# thousands of times on a live controller. With an Xbox 360 up, the census
# walk reads HKLM's Enum keys without falling back and finds what the
# configuration manager lists, also over Enum keys it cannot read (scratch
# keys under HKCU, denied, deleted or gone).
# Then a USB/IP device's frames on each of its three send paths, the input
# pump, a protocol's poll clock and the Switch 2 Pro's report clock, and a
# DualSense composite's output and audio transfers, with no allocation in
# the median run.
function Scenario-Perf-Budget {
    Invoke-Probe -Dir 'perf_budget_check' -Exe 'PerfBudgetCheck.exe' -Arguments 'gate' -Tag 'gate' `
                 -Message 'a submit path allocated or the census missed a devnode (see probe stdout)' -SkipCodes 2
}

# S85: an exception a program's event handler throws stops nothing else. A
# throwing handler sits ahead of a counting one on OutputReceived,
# OutputDecoded and KeyboardLedsChanged: the counting handler gets every
# event, the keyboard's LED state follows Scroll Lock, and a Bluetooth
# DualSense still switches to report 0x31 on the feature read that arms it.
# An OutputReceived handler still running when Dispose's join gives up: the
# output thread ends once it returns and meets nothing Dispose released.
# An OutputReceived handler that disposes its own controller does not wait
# for its own thread, alone, with two others at once, or through
# HMContext.Dispose. The probe's offline run, which needs no device, checks
# the set dispose's threads and its AggregateException. Both runs always
# run, and a failure of either is reported.
function Scenario-Handler-Isolation {
    $failed = @()
    try {
        Invoke-Probe -Dir 'handler_isolation_check' -Exe 'HandlerIsolationCheck.exe' -Arguments 'offline' -Tag 'offline' `
                     -Message 'a set dispose from a handler moved the handler''s controller off its thread, or lost a callback failure from its AggregateException (see probe stdout)'
    } catch { $failed += $_.Exception.Message }
    try {
        Invoke-Probe -Dir 'handler_isolation_check' -Exe 'HandlerIsolationCheck.exe' `
                     -Message 'a throwing event handler stopped another handler or the SDK, or a handler that disposed its controller waited for its own thread (see probe stdout)' -SkipCodes 2
    } catch { $failed += $_.Exception.Message }
    if ($failed.Count -gt 0) { throw ($failed -join ' AND ') }
}

# S87: interop declarations and the method names in the SDK's text. No
# DllImport is left, SetupDiGetClassDevsW takes its Enumerator as the pointer
# it is, and every method a message or XML doc names exists as written. The
# live run creates an Xbox 360 persona and reads the SDK's own XInput slot
# count, disposes it, and runs the phantom sweep.
function Scenario-Interop-Decl {
    Invoke-Probe -Dir 'interop_decl_check' -Exe 'InteropDeclCheck.exe' -Arguments 'static' -Tag 'static' `
                 -Message 'an interop declaration or a method name in the SDK''s text is wrong (see probe stdout)' -SkipCodes 2
    Invoke-Probe -Dir 'interop_decl_check' -Exe 'InteropDeclCheck.exe' -Arguments 'live' -Tag 'live' `
                 -Message 'the XInput slot count or a SetupDiGetClassDevsW caller failed on a live controller (see probe stdout)' -SkipCodes 2
}

# S88: no device shortcut in the SDK or the drivers. With comments removed,
# no file or declared name (locals and parameters too) holds a device or
# vendor word outside the probe's allowed names, each allowed by its exact
# name in the file its path names. The words come from the catalog at run
# time (vendor folders, profile ids, names and product strings, less the
# generic words the probe lists with a reason) and match inside lowercase
# compounds too. No vendor or product ID is compared, matched, switched on,
# assigned to an expression named for an ID or passed where the parameter
# is one, as a literal, an expression of literals, a truncating cast, a
# parsed hex string, a constant or a local initialized from one, and a
# local assigned from an ID is an ID whatever its name. No catalog vendor
# ID appears as a hex literal of four or more digits, and no catalog ID as
# four bytes in a row, or as two compared bytes or a two-byte list beside
# or compared with an ID. No VID_ or PID_ digits appear in a string or in
# an INF model line with its [Strings] tokens read, except the INF lines
# the probe allows. No device name is compared, searched, given to a name
# or written into a regular expression, with strings folded across +,
# interpolation, string.Concat and adjacent C literals, except a removed
# spelling the probe allows in the file that refuses it. No catalog
# profile id is a string, and no profile field is set by one catalog profile
# alone unless the probe's table allows it, counted per key, per field type
# (a center on int16-le apart from one on uint8-axis), per value the SDK
# names (a string it compares the property with, an enum member it writes),
# and over the state block's fields too. No region is exempt: a frozen-compat
# marker in any spelling fails, and each region of 9bd6279's drivers that
# v2.0.0 deletes (the 24 device regions and the default device's five)
# fails while any of its token-stream prints is in a C or C++ source under
# driver\, third_party and any extension cl.exe takes included. The prints
# file is pinned by its SHA-256. A catalog profile the SDK embeds and does
# not load fails too. The probe prints every allowance and what remains of
# each region. It fails on 9bd6279's tree by design and passes once every
# device shortcut is profile data and every region is gone. Exit 2 when the
# source tree is not beside the probe, as on a staged machine.
function Scenario-Device-Shortcut {
    Invoke-Probe -Dir 'device_shortcut_check' -Exe 'DeviceShortcutCheck.exe' `
                 -Message 'device-specific code is in the SDK or a driver outside its allowed names (see probe stdout)' -SkipCodes 2
}

# S89: the device code still gives 9bd6279's bytes. device_oracle_check
# replays its nine cases (the SDK's submit and decode paths, the packers,
# the Switch 2 Pro responder, the USB/IP feature answers and input paths,
# the UMDF2 driver and companion compiled natively, and the physical
# descriptors of the reference clones as an extraction builds them) and
# compares every stream with its golden file. A difference passes only when
# changes.txt beside the probe lists it with the lane that made it and the
# reason, and a listed change that does not happen fails.
# Offline. Exit 2 when the golden files are not beside the probe, as on a
# staged machine. With the golden files present, a machine without the
# reference clones case 9 reads fails the scenario (exit 1), so the battery
# never passes with case 9 unreplayed.
function Scenario-Device-Oracle {
    Invoke-Probe -Dir 'device_oracle_check' -Exe 'DeviceOracleCheck.exe' -Arguments 'replay' `
                 -Message 'a device code path no longer gives the bytes its golden file records (see probe stdout for the stream and output)' -SkipCodes 2
}

function Scenario-Vendor-Blob-Golden {
    Invoke-Probe -Dir 'vendor_blob_golden_check' -Exe 'VendorBlobGoldenCheck.exe' -Arguments '' `
                 -Message 'a vendor-blob report no longer encodes or decodes to its golden bytes, or the codec vocabulary no longer reproduces the code it replaces (see probe stdout)'
}

# S94: every report program three ways. program_equivalence_check runs each
# catalog program, decoder and layout set through its generated kernel, the
# interpreter and a naive evaluator written from the design note, on random
# states and on DD-0's recorded inputs, then the review cases, the domain
# edges, a corpus of 1,000 random programs drawn at the probe's build from
# the seed pre-tag-validate passes and prints, the negative corpus, the
# planted defects, every validation rule with its JSON path, the binder's
# fallback and key order, a profile from outside the catalog, and the
# inlining of each program into the code that sends it. Once under the JIT
# and once in the NativeAOT build beside it, where every catalog program must
# bind its generated kernel through the hash computed at run time. Offline.
function Scenario-Program-Equivalence {
    Invoke-Probe -Dir 'program_equivalence_check' -Exe 'ProgramEquivalenceCheck.exe' -Arguments 'check' -Tag 'jit' `
                 -Message 'a report program gives different bytes on its generated, interpreted or naive path, or a validation rule names the wrong path (see probe stdout)'
    Invoke-Probe -Dir 'program_equivalence_check' -Exe ('aot-' + (Get-SmokeArch) + '\ProgramEquivalenceCheck.exe') -Arguments 'check' -Tag 'aot' `
                 -Message 'under NativeAOT, a report program gives different bytes or binds no generated kernel (see probe stdout)'
}

# S95: the protocol engine and the device clock, offline: conditions with ||,
# != and in on 16-bit registers, two layouts on one report ID, masked
# matches, store, storeRecord, or and andNot, memory, bitmap, prefix and
# status replies, bulk replies in read-sized chunks, the three overflow
# rules, GET_REPORT of the layout that would go out, a clock counter, no
# allocation per command or report, the Triton's IMU register, the clock's
# grid under each missed-tick rule with start, stop and restart, and the
# idle repeat's period, condition and timestamps.
function Scenario-Protocol-Engine {
    Invoke-Probe -Dir 'protocol_engine_check' -Exe 'ProtocolEngineCheck.exe' `
                 -Message 'the protocol engine or the device clock no longer behaves as its profile states (see probe stdout)'
}

# S96: catalog data and driver text against the captures and sources they
# come from. Every comment line of the two driver INFs is plain ASCII. The
# Xbox 360 profiles carry a real pad's manufacturer string. The flight
# stick and the PS Move carry their captured descriptors, equal to the
# dumps their notes cite, and the Move's input report puts each control
# where psmoveapi reads a real Move's, its motion on the axes RPCS3 reads
# and in the units of the calibration its prepared feature answers serve.
# Offline. The INF part and the ControllersInfo dump read the source tree
# and its reference clones, and print [SKIP] without them.
function Scenario-Capture-Fidelity {
    Invoke-Probe -Dir 'capture_fidelity_check' -Exe 'CaptureFidelityCheck.exe' -Arguments '' `
                 -Message 'catalog data or driver text differs from the capture or source it comes from (see probe stdout)'
}

# S97: the Switch family's protocol mechanisms carry a second device, offline:
# joycon-l-proof, a Joy-Con (L) built from switch-pro's mechanisms, loads,
# answers SDL's Joy-Con open with nxbt's device info, and its 0x3F stick
# hat follows dekuNukem's grid in all eight directions.
function Scenario-Switch-Family {
    Invoke-Probe -Dir 'switch_family_check' -Exe 'SwitchFamilyCheck.exe' -Arguments '' `
                 -Message 'a Joy-Con built from the Switch Pro mechanisms no longer answers or reports as its sources state (see probe stdout)'
}

function Scenario-Arith-Roundtrip {
    Invoke-Probe -Dir 'arith_roundtrip_check' -Exe 'ArithRoundtripCheck.exe' -Arguments 'check' `
                 -Message 'a raw value a consumer reads from a device no longer returns through the SDK as that value (see probe stdout for the conversion)'
    # Every float in [0, 1] through each catalog conversion, against the
    # design note's table: a range a descriptor brings in, or a change of
    # arithmetic, that the table does not list fails here.
    Invoke-Probe -Dir 'arith_roundtrip_check' -Exe 'ArithRoundtripCheck.exe' -Arguments 'diff' -Tag 'diff' `
                 -Message 'a catalog conversion changes in a way the design note''s table does not list (see probe stdout for the conversion)'
}

# S92: profiles as data, the SDK side. profile_model_check loads the catalog
# and a user's directory strictly: every defect a file can carry is refused
# at its JSON path and listed in HMContext.ProfileLoadIssues, a create and a
# build meet one exception type for a bad profile, and the catalog files of
# every earlier v1.x tag load but for the removed spellings. A feature
# answer's per-controller bytes come from its value sources and patches,
# each entry of an answer table matches by its own rule, the SDK writes
# its half of the version contract, the deploy floor is 2.0.0, and
# FromProfile copies the whole profile. Offline.
function Scenario-Profile-Model {
    Invoke-Probe -Dir 'profile_model_check' -Exe 'ProfileModelCheck.exe' -Arguments 'check' `
                 -Message 'a profile loads, resolves or answers otherwise than the data-driven design states (see probe stdout for the check)'
}

# S93: the version contract on the live UMDF2 driver. A fresh-token
# controller's driver reads its index from the identity entry before its
# first start, on the ROOT path and on the SWD xinputhid path, a device
# without report IDs answers its message after HID class's leading 0, each
# catalog profile with a model, read back by HMDeviceExtractor, names the
# model its identity gives it and, created on the same index, answers as
# the device did, the three profiles whose descriptors Windows refused
# start, and a create whose descriptor Windows refuses fails with problem
# code 10 and removes its device.
function Scenario-Contract-Live {
    Invoke-Probe -Dir 'contract_live_check' -Exe 'ContractLiveCheck.exe' -Arguments 'check' `
                 -Message 'the live driver does not hold the version contract or FeatureAnswers as the design states (see probe stdout for the check)'
}

# S98: the Switch Pro protocol responder (issues #33, #36 and #37). The
# probe opens the virtual pad over raw HID under the real Bluetooth
# descriptor and runs the init SDL3's HIDAPI_DriverSwitch runs over
# Bluetooth: device info, stick and IMU calibration, vibration and input
# mode. It then checks the 0x30 stream, the input and IMU round trip and
# the rumble decode, and that the USB-only 0x80 family answers nothing, as
# on a real Bluetooth pad.
function Scenario-Switch-Pro-Protocol {
    Invoke-Probe -Dir 'switch_pro_check' -Exe 'SwitchProCheck.exe' `
                 -Message 'the Switch Pro no longer answers SDL''s Bluetooth init, streams 0x30, carries input and IMU, or decodes rumble as a real pad does (see probe stdout)'
}

# S99: the Switch Pro before its handshake (issues #35 and #37). Under the
# real Bluetooth descriptor the idle stream is the 12-byte 0x3F simple-mode
# report, the one report DirectInput can parse, and the first 0x01
# subcommand switches the stream to the 49-byte 0x30.
function Scenario-Switch-Descriptor-Idle {
    Invoke-Probe -Dir 'switch_descriptor_idle_check' -Exe 'SwitchDescriptorIdleCheck.exe' `
                 -Message 'the Switch Pro''s idle 0x3F stream, or its switch to 0x30 on the first 0x01 subcommand, no longer matches a real pad (see probe stdout)'
}

# S100: the Switch Pro through real SDL3 (issue #33's acceptance line).
# HIDAPI_DriverSwitch opens the virtual pad, completes init, reads
# calibrated sticks and buttons, and reports gyro and accel through
# SDL_GetGamepadSensorData. The probe loads the unfiltered SDL3 build
# beside the repository, as the fork's head hides HIDMaestro pads by
# design, and prints [SKIP] where no SDL3 build is there.
function Scenario-Switch-Pro-Sdl3 {
    Invoke-Probe -Dir 'switch_pro_sdl3_check' -Exe 'SwitchProSdl3Check.exe' `
                 -Message 'SDL3''s HIDAPI_DriverSwitch no longer opens the virtual Switch Pro, completes its init, or reads its sticks, buttons and motion (see probe stdout)'
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
    @{ Name = 'S45_Usbip_E2E_Composite';          Body = ${function:Scenario-Usbip-E2E-Composite} },
    @{ Name = 'S46_Sony_Feature_Gate';            Body = ${function:Scenario-Sony-Feature-Gate} },
    @{ Name = 'S47_Switch2_Pro_Profile';          Body = ${function:Scenario-Switch2-Pro} },
    @{ Name = 'S48_Switch2_Pro_Sdl3';             Body = ${function:Scenario-Switch2-Pro-Sdl3} },
    @{ Name = 'S49_Sony_Extra_Buttons';           Body = ${function:Scenario-Sony-Extra-Buttons} },
    @{ Name = 'S50_Vr_Controller_Smoke';          Body = ${function:Scenario-Vr-Controller-Smoke} },
    @{ Name = 'S51_Valve_Personas';               Body = ${function:Scenario-Valve-Personas} },
    @{ Name = 'S52_Valve_Wire';                   Body = ${function:Scenario-Valve-Wire} },
    @{ Name = 'S53_Valve_Sdl';                    Body = ${function:Scenario-Valve-Sdl} },
    @{ Name = 'S54_Valve_Steam';                  Body = ${function:Scenario-Valve-Steam} },
    @{ Name = 'S55_Valve_Multi';                  Body = ${function:Scenario-Valve-Multi} },
    @{ Name = 'S56_Valve_Raw_Path';               Body = ${function:Scenario-Valve-Raw-Path} },
    @{ Name = 'S57_Xusb_Wgi_Single';              Body = ${function:Scenario-Xusb-Wgi-Single} },
    @{ Name = 'S58_Identity_Derivation';         Body = ${function:Scenario-Identity-Derivation} },
    @{ Name = 'S59_Identity_Battery';            Body = ${function:Scenario-Identity-Battery} },
    @{ Name = 'S60_Xusb_Battery';                Body = ${function:Scenario-Xusb-Battery} },
    @{ Name = 'S61_Driver_Catalog';              Body = ${function:Scenario-Driver-Catalog} },
    @{ Name = 'S62_Valve_Firmware';              Body = ${function:Scenario-Valve-Firmware} },
    @{ Name = 'S63_Ds4_Report';                  Body = ${function:Scenario-Ds4-Report} },
    @{ Name = 'S64_Ds3_Sixaxis';                 Body = ${function:Scenario-Ds3-Sixaxis} },
    @{ Name = 'S65_Switch2_Composite';           Body = ${function:Scenario-Switch2-Composite} },
    @{ Name = 'S66_Registry_Ownership';          Body = ${function:Scenario-Registry-Ownership} },
    @{ Name = 'S67_Wgi_Mapping';                 Body = ${function:Scenario-Wgi-Mapping} },
    @{ Name = 'S68_Vr_Driver_Conflict';          Body = ${function:Scenario-Vr-Driver-Conflict} },
    @{ Name = 'S69_Kbm_Descriptors';             Body = ${function:Scenario-Kbm-Descriptors} },
    @{ Name = 'S70_Kbm_Raw_Input';               Body = ${function:Scenario-Kbm-Raw-Input} },
    @{ Name = 'S71_Coexistence';                 Body = ${function:Scenario-Coexistence} },
    @{ Name = 'S72_Protected_Staging';           Body = ${function:Scenario-Protected-Staging} },
    @{ Name = 'S73_Usbip_Clean_Install';         Body = ${function:Scenario-Usbip-Clean-Install} },
    @{ Name = 'S74_NativeAot_Smoke';             Body = ${function:Scenario-NativeAot-Smoke} },
    @{ Name = 'S75_SingleFile_Smoke';            Body = ${function:Scenario-SingleFile-Smoke} },
    @{ Name = 'S76_Json_Parity';                 Body = ${function:Scenario-Json-Parity} },
    @{ Name = 'S77_Pnp_Notify';                  Body = ${function:Scenario-Pnp-Notify} },
    @{ Name = 'S78_Build_Info';                  Body = ${function:Scenario-Build-Info} },
    @{ Name = 'S79_GameCube_Descriptors';        Body = ${function:Scenario-GameCube-Descriptors} },
    @{ Name = 'S80_GameCube_Protocol';           Body = ${function:Scenario-GameCube-Protocol} },
    @{ Name = 'S81_GameCube_WinUsb';             Body = ${function:Scenario-GameCube-WinUsb} },
    @{ Name = 'S82_GameCube_Dolphin';            Body = ${function:Scenario-GameCube-Dolphin} },
    @{ Name = 'S83_GameCube_Sdl';                Body = ${function:Scenario-GameCube-Sdl} },
    @{ Name = 'S84_Perf_Budget';                 Body = ${function:Scenario-Perf-Budget} },
    @{ Name = 'S85_Handler_Isolation';           Body = ${function:Scenario-Handler-Isolation} },
    @{ Name = 'S87_Interop_Decl';                Body = ${function:Scenario-Interop-Decl} },
    @{ Name = 'S88_Device_Shortcut';             Body = ${function:Scenario-Device-Shortcut} },
    @{ Name = 'S89_Device_Oracle';               Body = ${function:Scenario-Device-Oracle} },
    @{ Name = 'S90_Arith_Roundtrip';             Body = ${function:Scenario-Arith-Roundtrip} },
    @{ Name = 'S91_Vendor_Blob_Golden';          Body = ${function:Scenario-Vendor-Blob-Golden} },
    @{ Name = 'S94_Program_Equivalence';         Body = ${function:Scenario-Program-Equivalence} },
    @{ Name = 'S95_Protocol_Engine';             Body = ${function:Scenario-Protocol-Engine} },
    @{ Name = 'S96_Capture_Fidelity';            Body = ${function:Scenario-Capture-Fidelity} },
    @{ Name = 'S97_Switch_Family';               Body = ${function:Scenario-Switch-Family} },
    @{ Name = 'S92_Profile_Model';               Body = ${function:Scenario-Profile-Model} },
    @{ Name = 'S93_Contract_Live';               Body = ${function:Scenario-Contract-Live} },
    @{ Name = 'S98_Switch_Pro_Protocol';         Body = ${function:Scenario-Switch-Pro-Protocol} },
    @{ Name = 'S99_Switch_Descriptor_Idle';      Body = ${function:Scenario-Switch-Descriptor-Idle} },
    @{ Name = 'S100_Switch_Pro_Sdl3';            Body = ${function:Scenario-Switch-Pro-Sdl3} }
)

$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

# S73 runs in Windows Sandbox and spends about four minutes there, nearly
# all of it idle while Windows Installer's signature check of each of the
# VC++ runtime's two packages times out at 120 s in the fresh image. The
# sandbox adds no devnode to this machine and its probe loads no
# HIDMaestro.Core.dll here, so it starts now, beside S01 to S72, and its
# slot collects the result.
$script:S73Run = $null
if ('S73_Usbip_Clean_Install' -like $Filter) {
    $script:S73Run = Start-BackgroundProbe -Dir 'usbip_install_check' -Exe 'UsbipInstallCheck.exe'
}

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
