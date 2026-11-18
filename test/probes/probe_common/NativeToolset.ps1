# The Visual Studio C++ tools the probes' native harnesses build with, for
# the architecture the probe is built for. Dot-sourced by the native\build.ps1
# of device_oracle_check, gc_dolphin_check, gc_sdl_check and
# perf_budget_check, whose project files pass -Arch $(HMTargetArchitecture),
# so an ARM64 build of a probe carries ARM64 harnesses that run natively on an
# ARM64 machine.
#
# Get-HMNativeToolset -Arch x64|arm64 finds the newest Visual Studio with the
# C++ tools for that architecture and returns:
#   Vs         its installation path
#   Vcvars     the cmd line that sets up the compiler: vcvarsall.bat with the
#              host and target pair, x64 on an x64 machine building for x64
#              (what vcvars64.bat runs), x64_arm64 for the cross compiler on
#              an x64 machine, arm64 on an ARM64 machine building for ARM64
#   Arch       x64 or arm64
#   Define     the architecture define the Windows kit headers need when
#              windows.h is not the first include: _AMD64_ or _ARM64_
#   Platform   the MSBuild platform and CMake -A name: x64 or ARM64
function Get-HMNativeToolset {
    param([Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string]$Arch)
    $hostArch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' } else { 'x64' }
    $component = if ($Arch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $vswhere = Join-Path $installer 'vswhere.exe'
    # vcvarsall.bat runs vswhere from PATH.
    if (($env:PATH -split ';') -notcontains $installer) { $env:PATH = "$installer;$env:PATH" }
    $vs = & $vswhere -latest -products * -requires $component -property installationPath
    if (-not $vs) { throw "No Visual Studio with the $Arch C++ tools ($component)." }
    $vcvarsall = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path $vcvarsall)) { throw "Visual Studio is missing $vcvarsall" }
    $pair = if ($hostArch -eq $Arch) { $Arch } else { "$($hostArch)_$Arch" }
    [PSCustomObject]@{
        Vs       = $vs
        Vcvars   = "`"$vcvarsall`" $pair"
        Arch     = $Arch
        Define   = if ($Arch -eq 'arm64') { '_ARM64_' } else { '_AMD64_' }
        Platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    }
}
