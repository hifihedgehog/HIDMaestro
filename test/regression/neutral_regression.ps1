<#
.SYNOPSIS
  Build-and-run regression battery for HIDMaestro's neutral-input override.

.DESCRIPTION
  Runs three elevated probes:

    1. neutral_xinput_check
       Direct SDK path: HMController.Neutralized suppresses active input while
       the virtual stays connected through XInput.

    2. neutral_cli_check
       User-facing CLI path: HIDMaestroTest.exe emulate accepts
       "neutral on/off/toggle" over stdin, acknowledges each command, and the
       same XInput slot stays connected but idle while neutral is on.

    3. browser_neutral_check
       Browser Gamepad API path: creates a virtual DS4, samples it through
       navigator.getGamepads(), and verifies neutral-on is stable across
       multiple samples. Also documents the expected active test pattern after
       neutral off.

  Exit code 0 if all probes pass.
#>
[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\CODING\SDKs\dotnet',
    [string]$EwdkPath = 'D:\CODING\SDKs\EWDK',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$scriptDir = if ($PSScriptRoot) {
    $PSScriptRoot
} elseif ($MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    $PWD.Path
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..\..'))
$tfm = 'net10.0-windows10.0.26100.0'
$dotnet = Join-Path $DotnetPath 'dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must run elevated. Start an Administrator PowerShell or run it through UAC."
    exit 2
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Body
    )
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

if (-not $SkipBuild) {
    Invoke-Step 'Build driver + SDK payload' {
        & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'build.ps1')
    }

    $wdkBin = ($EwdkPath -replace '\\','/') + '/Program Files/Windows Kits/10/bin/10.0.28000.0/'
    $buildDir = ($repoRoot -replace '\\','/') + '/build/'

    Invoke-Step 'Build SDK win-x64 reference' {
        & $dotnet build (Join-Path $repoRoot 'sdk\HIDMaestro.Core\HIDMaestro.Core.csproj') `
            -c Release -r win-x64 -nologo -v minimal `
            "/p:HMWdkBin=$wdkBin" "/p:HMBuildDir=$buildDir"
    }

    Invoke-Step 'Build HIDMaestroTest CLI' {
        & $dotnet build (Join-Path $repoRoot 'test\HIDMaestroTest.csproj') `
            -c Release -nologo -v minimal --no-dependencies
    }

    Invoke-Step 'Build neutral_xinput_check probe' {
        & $dotnet build (Join-Path $repoRoot 'test\probes\neutral_xinput_check\NeutralXInputCheck.csproj') `
            -c Release -r win-x64 -nologo -v minimal --no-dependencies
    }

    Invoke-Step 'Build neutral_cli_check probe' {
        & $dotnet build (Join-Path $repoRoot 'test\probes\neutral_cli_check\NeutralCliCheck.csproj') `
            -c Release -r win-x64 -nologo -v minimal
    }

    Invoke-Step 'Build browser_neutral_check probe' {
        & $dotnet build (Join-Path $repoRoot 'test\probes\browser_neutral_check\BrowserNeutralCheck.csproj') `
            -c Release -r win-x64 -nologo -v minimal
    }
}

$directProbe = Join-Path $repoRoot "test\probes\neutral_xinput_check\bin\Release\$tfm\win-x64\NeutralXInputCheck.exe"
$cliProbe = Join-Path $repoRoot "test\probes\neutral_cli_check\bin\Release\$tfm\win-x64\NeutralCliCheck.exe"
$browserProbe = Join-Path $repoRoot "test\probes\browser_neutral_check\bin\Release\$tfm\win-x64\BrowserNeutralCheck.exe"

Invoke-Step 'Run direct SDK neutral probe' {
    & $directProbe
}

Invoke-Step 'Run CLI neutral probe' {
    & $cliProbe
}

Invoke-Step 'Run Browser Gamepad API neutral probe' {
    & $browserProbe
}

Write-Host ""
Write-Host "=== NEUTRAL REGRESSION BATTERY: ALL PASS ===" -ForegroundColor Green
exit 0
