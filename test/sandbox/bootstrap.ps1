<#
.SYNOPSIS
HIDMaestro Sandbox bootstrap. Runs at logon as WDAGUtilityAccount (admin).

.DESCRIPTION
Sandbox state doesn't survive reboot, so testsigning mode (which requires a
reboot) is not viable here — we let HIDMaestro's DriverBuilder pipeline do its
normal cert-generate-and-trust dance on first run. The bootstrap's job is just:
  1. Optionally switch the Sandbox display language to validate locale fixes.
  2. Make sure the .NET 10 Desktop Runtime is present.
  3. Drop the user into a HIDMaestro working directory with usage hints.
#>

param(
    [string]$Locale = 'en-US'
)

$ErrorActionPreference = 'Continue'

function Test-DotNet10Desktop {
    try {
        $rt = & dotnet --list-runtimes 2>$null
        return [bool]($rt -match 'Microsoft\.WindowsDesktop\.App 10\.')
    } catch { return $false }
}

# 1. Locale switch (optional). Simulates the issue #17 French Windows env.
if ($Locale -and $Locale -ne 'en-US') {
    Write-Host "Setting Sandbox display language: $Locale"
    try {
        Set-WinUILanguageOverride -Language $Locale -ErrorAction Stop
        Set-Culture $Locale -ErrorAction Stop
        Write-Host "  Display language override applied. Some pnputil text"
        Write-Host "  may still surface as English until next sign-in; the"
        Write-Host "  v1.2.2 XML parser is locale-stable regardless."
    } catch {
        Write-Host "  Locale switch failed: $($_.Exception.Message)"
        Write-Host "  Continuing in default locale."
    }
}

# 2. .NET 10 Desktop Runtime.
if (-not (Test-DotNet10Desktop)) {
    Write-Host ''
    Write-Host '.NET 10 Desktop Runtime not present. Installing...'

    # Prefer a side-loaded installer if the user dropped one next to bootstrap.ps1
    # on the host (mounted read-only at C:\HIDMaestro\sandbox). Otherwise pull
    # from Microsoft.
    $localInstaller = Get-ChildItem -Path 'C:\HIDMaestro\sandbox' -Filter 'windowsdesktop-runtime-10.0.*-win-x64.exe' -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($localInstaller) {
        Write-Host "  Using local installer: $($localInstaller.Name)"
        $installerPath = $localInstaller.FullName
    } else {
        # aka.ms redirector points at the latest 10.0.x desktop runtime x64.
        $url = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
        $installerPath = Join-Path $env:TEMP 'windowsdesktop-runtime-10.exe'
        Write-Host "  Downloading from $url ..."
        try {
            Invoke-WebRequest -Uri $url -OutFile $installerPath -UseBasicParsing
        } catch {
            Write-Host "  Download failed: $($_.Exception.Message)"
            Write-Host "  Drop the runtime installer next to bootstrap.ps1 on the"
            Write-Host "  host (filename pattern: windowsdesktop-runtime-10.0.*-win-x64.exe),"
            Write-Host "  then relaunch Sandbox."
            return
        }
    }

    Write-Host "  Running silent install..."
    $proc = Start-Process -FilePath $installerPath -ArgumentList '/quiet', '/install', '/norestart' -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Host "  Installer returned exit code $($proc.ExitCode). HIDMaestroTest may not start."
    } else {
        Write-Host "  .NET 10 Desktop Runtime installed."
    }
}

# 3. Working dir + usage hints.
$banner = @"

======================================================================
  HIDMaestro Sandbox ready
======================================================================

  Single-controller create (golden path):
    cd C:\HIDMaestro\HIDMaestroTest
    .\HIDMaestroTest.exe emulate xbox-360-wired

  Multi-controller (exercises issue #17's failure path):
    .\HIDMaestroTest.exe emulate xbox-series-xs-bt xbox-360-wired dualsense

  PID FFB end-to-end probe (S26 + round-trip magnitude):
    cd C:\HIDMaestro\PidSetUsagesProbe
    .\PidSetUsagesProbe.exe

  Subset of regression battery (full battery is 33+ min on fast hw):
    powershell -File C:\HIDMaestro\regression\swap_regression.ps1 -Filter 'S0[1-3]*'

  When done — close the Sandbox window. State is discarded automatically.

  Locale active: $Locale

"@
Write-Host $banner

Set-Location 'C:\HIDMaestro\HIDMaestroTest'
