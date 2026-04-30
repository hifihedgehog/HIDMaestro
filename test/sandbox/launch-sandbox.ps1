<#
.SYNOPSIS
Spin up a Windows Sandbox preconfigured for HIDMaestro testing.

.DESCRIPTION
Generates a .wsb at $env:TEMP with this repo's absolute paths mapped in, then
shells out to Sandbox.exe. Each Sandbox launch is a pristine Win 11 image —
useful for catching missing dependencies, validating the fresh-install path,
and confirming the locale fix works against a non-English Windows display
language. Not useful for slow-hardware simulation; Sandbox inherits host CPU.

Prerequisites on the host:
  - Windows Sandbox optional feature enabled (Settings -> Apps -> Optional
    features -> "Windows Sandbox", or `Enable-WindowsOptionalFeature -Online
    -FeatureName Containers-DisposableClientVM -All`).
  - This repo built (build_all.cmd has run; HIDMaestroTest + probe binaries
    exist under test/bin/.../win-x64 and test/probes/.../win-x64).

.PARAMETER Locale
Display-language tag to set inside Sandbox at logon. Use to validate the v1.2.2
locale fix end-to-end (e.g. -Locale fr-FR to reproduce the original issue #17
French Windows scenario). Default: en-US (no change).

.EXAMPLE
.\launch-sandbox.ps1
Launches an English Sandbox preloaded with the test app.

.EXAMPLE
.\launch-sandbox.ps1 -Locale fr-FR
Launches a French Sandbox to validate locale-stable pnputil parsing.
#>

[CmdletBinding()]
param(
    [string]$Locale = 'en-US'
)

$ErrorActionPreference = 'Stop'

# Resolve repo root: this script lives at test/sandbox/, so root is two up.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

# Required artifact paths. Built by scripts\build_all.cmd + dotnet build of
# the test app and the probe.
$tfm = 'net10.0-windows10.0.26100.0'
$rid = 'win-x64'
$testAppDir   = Join-Path $repoRoot "test\bin\Release\$tfm\$rid"
$probeDir     = Join-Path $repoRoot "test\probes\pid_setusages_probe\bin\Release\$tfm\$rid"
$regressionDir = Join-Path $repoRoot 'test\regression'

# Validate. If anything is missing, tell the user how to fix it instead of
# punting it onto the Sandbox bootstrap (which can't recover from missing
# host artifacts).
$missing = @()
if (-not (Test-Path (Join-Path $testAppDir 'HIDMaestroTest.exe'))) { $missing += "HIDMaestroTest.exe at $testAppDir" }
if (-not (Test-Path (Join-Path $probeDir 'PidSetUsagesProbe.exe'))) { $missing += "PidSetUsagesProbe.exe at $probeDir" }
if ($missing.Count -gt 0) {
    Write-Error @"
Missing build artifacts:
$(($missing | ForEach-Object { "  - $_" }) -join "`n")

Run from repo root:
    cmd /c scripts\build_all.cmd
    dotnet build test\HIDMaestroTest.csproj -c Release -r win-x64
    dotnet build test\probes\pid_setusages_probe\PidSetUsagesProbe.csproj -c Release -r win-x64
"@
    exit 1
}

# Sandbox feature check.
if (-not (Get-Command 'WindowsSandbox.exe' -ErrorAction SilentlyContinue) -and
    -not (Test-Path 'C:\Windows\System32\WindowsSandbox.exe')) {
    Write-Error @"
Windows Sandbox is not enabled on this host. Enable with:
    Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
(elevated PowerShell, then reboot).
"@
    exit 1
}

# Generate the .wsb. Sandbox requires absolute host paths.
$wsbPath = Join-Path $env:TEMP 'hidmaestro-sandbox.wsb'
$bootstrapHost = Join-Path $PSScriptRoot 'bootstrap.ps1'

$wsbXml = @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$testAppDir</HostFolder>
      <SandboxFolder>C:\HIDMaestro\HIDMaestroTest</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$probeDir</HostFolder>
      <SandboxFolder>C:\HIDMaestro\PidSetUsagesProbe</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$regressionDir</HostFolder>
      <SandboxFolder>C:\HIDMaestro\regression</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$PSScriptRoot</HostFolder>
      <SandboxFolder>C:\HIDMaestro\sandbox</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -ExecutionPolicy Bypass -NoExit -File C:\HIDMaestro\sandbox\bootstrap.ps1 -Locale $Locale</Command>
  </LogonCommand>
  <MemoryInMB>4096</MemoryInMB>
  <Networking>Default</Networking>
  <vGPU>Disable</vGPU>
  <ClipboardRedirection>true</ClipboardRedirection>
</Configuration>
"@

Set-Content -Path $wsbPath -Value $wsbXml -Encoding UTF8

Write-Host "Sandbox config: $wsbPath"
Write-Host "Mapped folders:"
Write-Host "  C:\HIDMaestro\HIDMaestroTest    <- $testAppDir"
Write-Host "  C:\HIDMaestro\PidSetUsagesProbe <- $probeDir"
Write-Host "  C:\HIDMaestro\regression        <- $regressionDir (read-only)"
Write-Host "  C:\HIDMaestro\sandbox           <- $PSScriptRoot (read-only)"
Write-Host ""
Write-Host "Locale: $Locale"
Write-Host "Launching..."

# Open the .wsb. Sandbox handles the rest; LogonCommand fires once the
# WDAGUtilityAccount session is up.
Start-Process $wsbPath
