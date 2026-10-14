# Builds the S82 harnesses: gc_harness_dolphin.exe and gc_harness_slippi.exe.
#
# Each compiles the emulator's own GCAdapter.cpp and LibusbUtils.cpp,
# unmodified, from the reference clones beside this repository at the commits
# the design note names, with the libusb and fmt that emulator pins as
# submodules, built the way the emulator builds them on Windows
# (Externals/libusb/CMakeLists.txt: core, descriptor, hotplug, io, strerror,
# sync and the five Windows sources, with libusb/msvc for config.h). Nothing
# from the emulators is copied into this repository: the sources are read with
# git show at the pinned commit, and the stand-in headers under stubs/ are
# this repository's own.
#
# Usage: build.ps1 -Out <dir> [-Refs <reference root>] [-Obj <dir>]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Refs,
    [string]$Obj
)
$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults.
if (-not $Refs) { $Refs = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..')).Path }
if (-not $Obj) { $Obj = Join-Path $PSScriptRoot '..\obj\native' }

$flavors = @(
    @{
        Name = 'dolphin'; Repo = 'dolphin'; Commit = '0d93fc0355'
        Libusb = 'libusb-dolphin'; LibusbCommit = '15a7ebb4d426c5ce196684347d2b7cafad862626'
        Fmt = 'fmt-dolphin'; FmtCommit = 'e424e3f2e607da02742f73db84873b8084fc714c'
        Define = ''
    },
    @{
        Name = 'slippi'; Repo = 'slippi-dolphin'; Commit = '9d13b39'
        Libusb = 'libusb-slippi'; LibusbCommit = 'ba698478afc3d3a72644eef9fc4cd24ce8383a4c'
        Fmt = 'fmt-slippi'; FmtCommit = 'e69e5f977d458f2650bb346dadf2ad30c5320281'
        Define = '/DHM_FLAVOR_SLIPPI'
    }
)

# The emulator sources each harness compiles, and the Dolphin headers used
# as they are. Everything else they include is a stand-in under stubs/.
$sources = @(
    'Source/Core/InputCommon/GCAdapter.cpp',
    'Source/Core/InputCommon/GCAdapter.h',
    'Source/Core/InputCommon/GCPadStatus.h',
    'Source/Core/Core/LibusbUtils.cpp',
    'Source/Core/Core/LibusbUtils.h',
    'Source/Core/Common/CommonTypes.h',
    'Source/Core/Common/BitUtils.h',
    'Source/Core/Common/Flag.h',
    'Source/Core/Common/Event.h',
    'Source/Core/Common/ScopeGuard.h',
    'Source/Core/Common/Thread.h'
)
$libusbSources = @(
    'libusb/core.c', 'libusb/descriptor.c', 'libusb/hotplug.c', 'libusb/io.c', 'libusb/strerror.c',
    'libusb/sync.c', 'libusb/os/threads_windows.c', 'libusb/os/windows_common.c',
    'libusb/os/windows_usbdk.c', 'libusb/os/windows_winusb.c', 'libusb/os/events_windows.c'
)

function Head([string]$dir) { (git -C $dir rev-parse HEAD).Trim() }

$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
# vcvars64.bat runs vswhere from PATH.
$env:PATH = "$installer;$env:PATH"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the x64 C++ tools.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

New-Item -ItemType Directory -Force -Path $Out, $Obj | Out-Null
$stubs = Join-Path $PSScriptRoot 'stubs'
$harness = Join-Path $PSScriptRoot 'harness.cpp'
$ownHash = (Get-ChildItem -Recurse -File $stubs, $harness, $PSCommandPath |
    Sort-Object FullName | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash) -join ''

foreach ($f in $flavors) {
    $repo = Join-Path $Refs $f.Repo
    $libusb = Join-Path $Refs $f.Libusb
    $fmt = Join-Path $Refs $f.Fmt
    foreach ($d in $repo, $libusb, $fmt) { if (-not (Test-Path $d)) { throw "Reference clone missing: $d" } }
    $commit = (git -C $repo rev-parse "$($f.Commit)^{commit}").Trim()
    if ((Head $libusb) -ne $f.LibusbCommit) { throw "$libusb is not at $($f.LibusbCommit)." }
    if ((Head $fmt) -ne $f.FmtCommit) { throw "$fmt is not at $($f.FmtCommit)." }

    $exe = Join-Path $Out "gc_harness_$($f.Name).exe"
    $stampPath = Join-Path $Out "gc_harness_$($f.Name).stamp"
    $stamp = "$commit $($f.LibusbCommit) $($f.FmtCommit) $ownHash"
    if ((Test-Path $exe) -and (Test-Path $stampPath) -and ((Get-Content $stampPath -Raw).Trim() -eq $stamp)) {
        Write-Host "gc_harness_$($f.Name).exe is current"
        continue
    }

    $work = Join-Path $Obj $f.Name
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    # git archive and tar keep every byte as committed. Text read through
    # PowerShell would pass through the console code page.
    $tarPath = Join-Path $work 'sources.tar'
    & git -C $repo archive --format=tar -o $tarPath $commit @sources
    if ($LASTEXITCODE -ne 0) { throw "git archive of $($f.Repo) $commit failed." }
    # Windows' own tar by full path: a GNU tar earlier on PATH reads "C:"
    # as a remote host.
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $tarPath -C $work
    if ($LASTEXITCODE -ne 0) { throw "Extracting $tarPath failed." }
    $src = Join-Path $work 'Source\Core'
    $libObj = Join-Path $work 'libusb'
    $appObj = Join-Path $work 'app'
    New-Item -ItemType Directory -Force -Path $libObj, $appObj | Out-Null

    $libusbFiles = ($libusbSources | ForEach-Object { '"' + (Join-Path $libusb $_) + '"' }) -join ' '
    $appFiles = @(
        (Join-Path $src 'InputCommon\GCAdapter.cpp'),
        (Join-Path $src 'Core\LibusbUtils.cpp'),
        $harness
    ) | ForEach-Object { '"' + $_ + '"' }
    $cmd = @"
@echo off
call "$vcvars" >nul || exit /b 1
cl /nologo /c /O2 /MT /DWIN32 /D_WINDOWS /I"$libusb\msvc" /I"$libusb\libusb" /Fo"$libObj\\" $libusbFiles || exit /b 1
lib /nologo /OUT:"$work\libusb.lib" "$libObj\*.obj" || exit /b 1
cl /nologo /c /O2 /MT /EHsc /std:c++latest /Zc:preprocessor /permissive- /utf-8 /DWIN32 /DNOMINMAX /D_WIN32_WINNT=0x0A00 /D__LIBUSB__ /DFMT_HEADER_ONLY $($f.Define) /I"$src" /I"$stubs" /I"$libusb\libusb" /I"$fmt\include" /Fo"$appObj\\" $($appFiles -join ' ') || exit /b 1
link /nologo /OUT:"$exe" "$appObj\*.obj" "$work\libusb.lib" || exit /b 1
"@
    $cmdPath = Join-Path $work 'build.cmd'
    Set-Content -Path $cmdPath -Value $cmd -Encoding ascii
    $p = Start-Process -FilePath $env:ComSpec -ArgumentList '/d', '/c', "`"$cmdPath`"" -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $work 'build.log') -RedirectStandardError (Join-Path $work 'build.err')
    # Reading the handle now keeps the exit code readable after the wait.
    $null = $p.Handle
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) {
        Get-Content (Join-Path $work 'build.log'), (Join-Path $work 'build.err') | Select-Object -Last 40 | Write-Host
        throw "Building gc_harness_$($f.Name).exe failed (exit $($p.ExitCode))."
    }
    Set-Content -Path $stampPath -Value $stamp -Encoding ascii
    Write-Host "built gc_harness_$($f.Name).exe from $($f.Repo) $commit, libusb $($f.LibusbCommit), fmt $($f.FmtCommit)"
}
