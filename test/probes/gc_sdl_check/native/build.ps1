# Builds what S83 runs:
#
#   sdl_harness.exe              loads an SDL3.dll from the directory it is given
#   sdl-official\SDL3.dll        the official SDL3 3.4.18 release for Windows x64,
#                                checked against its SHA-256
#   sdl-libusb\SDL3.dll          SDL 3.4.18 built from source by its own CMake,
#                                with SDL_HIDAPI_LIBUSB and SDL_HIDAPI_LIBUSB_SHARED
#   sdl-libusb\libusb-1.0.dll    libusb 1.0.29 built by its own MSVC project
#
# The SDL and libusb sources are the reference clones beside this repository,
# at the commits named below, and nothing from them is copied into it. The
# CMake options are the ones PadForge's native build passes for SDL with
# libusb (SDL3-build\build-natives.ps1).
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
$Obj = [IO.Path]::GetFullPath($Obj)

$sdlCommit = '829a65d769d935c4852f8159e964312c0957260a'      # release-3.4.18
$libusbCommit = '15a7ebb4d426c5ce196684347d2b7cafad862626'   # v1.0.29
# SDL3.dll from SDL3-3.4.18-win32-x64.zip, the release's Windows x64 asset
# (https://github.com/libsdl-org/SDL/releases/tag/release-3.4.18). Its
# .git-hash names $sdlCommit.
$officialSha = '2B4B128D8D510CC2D442693CF848C050D700D43DC667AFE9E5FD82B0570D667E'

$sdlSource = Join-Path $Refs 'SDL-3.4.18'
$libusbSource = Join-Path $Refs 'libusb-1.0.29'
$official = Join-Path $Refs 'SDL3-release-3.4.18\SDL3.dll'
foreach ($p in $sdlSource, $libusbSource, $official) { if (-not (Test-Path $p)) { throw "Reference missing: $p" } }
if ((git -C $sdlSource rev-parse HEAD).Trim() -ne $sdlCommit) { throw "$sdlSource is not at $sdlCommit." }
if ((git -C $libusbSource rev-parse HEAD).Trim() -ne $libusbCommit) { throw "$libusbSource is not at $libusbCommit." }
if ((Get-FileHash -Algorithm SHA256 $official).Hash -ne $officialSha) { throw "$official is not the release's SDL3.dll (SHA-256 $officialSha)." }

$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
# vcvars64.bat runs vswhere from PATH.
$env:PATH = "$installer;$env:PATH"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the x64 C++ tools.' }
$vsMajor = ((& $vswhere -latest -products * -property installationVersion) -split '\.')[0]
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
foreach ($p in $vcvars, $msbuild, $cmake) { if (-not (Test-Path $p)) { throw "Visual Studio is missing $p" } }
# libusb's msvc\Configuration.Base.props names a toolset for Visual Studio
# 2013 through 2022 only, so a newer one gets the toolset it installs. The
# CMake generator is named for the release year, which vswhere does not
# report for Visual Studio 2026.
$toolset = @{ '17' = 'v143'; '18' = 'v145' }[$vsMajor]
$generator = @{ '17' = 'Visual Studio 17 2022'; '18' = 'Visual Studio 18 2026' }[$vsMajor]
if (-not $toolset -or -not $generator) { throw "No platform toolset or CMake generator known for Visual Studio $vsMajor." }

New-Item -ItemType Directory -Force -Path $Out, $Obj | Out-Null
$ownHash = (Get-ChildItem -File (Join-Path $PSScriptRoot 'sdl_harness.cpp'), $PSCommandPath |
    Sort-Object FullName | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash) -join ''

function Run([string]$exe, [string]$arguments, [string]$log) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    # Reading the handle now keeps the exit code readable after the wait.
    $null = $p.Handle
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) {
        Get-Content $log, "$log.err" | Select-Object -Last 40 | Write-Host
        throw "$([IO.Path]::GetFileName($exe)) failed (exit $($p.ExitCode)). Log: $log"
    }
}

# Start-Process joins its arguments with spaces, so MSBuild's and CMake's are
# quoted here. A backslash before the closing quote is doubled, or the
# command line parser would read it as an escaped quote.
function Quote([string[]]$arguments) {
    ($arguments | ForEach-Object { '"' + ($_ -replace '(\\+)$', '$1$1') + '"' }) -join ' '
}

function Current([string]$stampPath, [string]$stamp, [string[]]$outputs) {
    foreach ($o in $outputs) { if (-not (Test-Path $o)) { return $false } }
    return (Test-Path $stampPath) -and ((Get-Content $stampPath -Raw).Trim() -eq $stamp)
}

# ── The two SDL builds ────────────────────────────────────────────────────

$officialDir = Join-Path $Out 'sdl-official'
$libusbDir = Join-Path $Out 'sdl-libusb'
$sdlStampPath = Join-Path $Out 'sdl-builds.stamp'
$sdlStamp = "$sdlCommit $libusbCommit $officialSha $toolset $((Get-FileHash -Algorithm SHA256 $PSCommandPath).Hash)"
$sdlOutputs = @((Join-Path $officialDir 'SDL3.dll'), (Join-Path $libusbDir 'SDL3.dll'), (Join-Path $libusbDir 'libusb-1.0.dll'))
if (Current $sdlStampPath $sdlStamp $sdlOutputs) {
    Write-Host 'the SDL builds are current'
} else {
    foreach ($d in $officialDir, $libusbDir, (Join-Path $Obj 'libusb'), (Join-Path $Obj 'sdl')) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d }
    }
    New-Item -ItemType Directory -Force -Path $officialDir, $libusbDir | Out-Null
    Copy-Item $official (Join-Path $officialDir 'SDL3.dll')

    # libusb's own DLL project, its Release x64 configuration. Its intermediate
    # and output directories are redirected here so the clone stays clean.
    # Warning C5287, which libusb's project treats as an error, is off: the
    # compilers in Visual Studio 2022 17.14 and later raise it on libusb's
    # LIBUSB_ENDPOINT_OUT | LIBUSB_REQUEST_TYPE_CLASS expressions, and libusb
    # turns it off the same way after 1.0.29 (msvc/config.h, "Disable:
    # warning C5287: operands are different enum types"). _CL_ appends the
    # option to every cl command line.
    $libusbObj = Join-Path $Obj 'libusb'
    $env:_CL_ = '/wd5287'
    try {
        Run $msbuild (Quote @((Join-Path $libusbSource 'msvc\libusb_dll.vcxproj'), '/nologo', '/v:minimal',
            '/p:Configuration=Release', '/p:Platform=x64', "/p:PlatformToolset=$toolset",
            "/p:IntDir=$libusbObj\obj\", "/p:OutDir=$libusbObj\out\")) (Join-Path $Obj 'libusb.log')
    } finally {
        Remove-Item Env:\_CL_
    }
    $importLib = Join-Path $libusbObj 'out\libusb-1.0.lib'

    # SDL's CMake with PadForge's options. With SDL_HIDAPI_LIBUSB_SHARED, SDL
    # names the DLL behind the import library (cmake/macros.cmake,
    # target_get_dynamic_library) and loads it by that name at run time.
    # SDL_REVISION is given because SDL's git_describe reads the HEAD of the
    # repository a worktree belongs to, not the worktree's own
    # (cmake/GetGitRevisionDescription.cmake), and the clone here is a
    # worktree.
    $sdlBuild = Join-Path $Obj 'sdl'
    $revision = 'SDL-' + (git -C $sdlSource describe --tags --long --abbrev=12).Trim()
    Run $cmake (Quote @('-S', $sdlSource, '-B', $sdlBuild, '-G', $generator, '-A', 'x64',
        '-DSDL_SHARED=ON', '-DSDL_STATIC=OFF', '-DSDL_TESTS=OFF', '-DSDL_TEST_LIBRARY=OFF', '-DSDL_EXAMPLES=OFF',
        "-DSDL_REVISION=$revision", '-DSDL_HIDAPI_LIBUSB=ON', '-DSDL_HIDAPI_LIBUSB_SHARED=ON',
        "-DLibUSB_INCLUDE_PATH=$(Join-Path $libusbSource 'libusb')", "-DLibUSB_LIBRARY=$importLib")) (Join-Path $Obj 'sdl-configure.log')
    $config = Join-Path $sdlBuild 'include-config-release\build_config\SDL_build_config.h'
    foreach ($line in '#define HAVE_LIBUSB 1', '#define SDL_LIBUSB_DYNAMIC "libusb-1.0.dll"') {
        if (-not (Select-String -Path $config -SimpleMatch -Pattern $line -Quiet)) { throw "SDL's configuration lacks $line ($config)." }
    }
    Run $cmake (Quote @('--build', $sdlBuild, '--config', 'Release', '--target', 'SDL3-shared')) (Join-Path $Obj 'sdl-build.log')
    Copy-Item (Join-Path $sdlBuild 'Release\SDL3.dll') (Join-Path $libusbDir 'SDL3.dll')
    Copy-Item (Join-Path $libusbObj 'out\libusb-1.0.dll') (Join-Path $libusbDir 'libusb-1.0.dll')
    Set-Content -Path $sdlStampPath -Value $sdlStamp -Encoding ascii
    Write-Host "built SDL $sdlCommit with libusb $libusbCommit, and copied the official SDL3.dll"
}

# ── The harness ───────────────────────────────────────────────────────────

$exe = Join-Path $Out 'sdl_harness.exe'
$harnessStampPath = Join-Path $Out 'sdl_harness.stamp'
$harnessStamp = "$sdlCommit $ownHash"
if (Current $harnessStampPath $harnessStamp @($exe)) {
    Write-Host 'sdl_harness.exe is current'
} else {
    $work = Join-Path $Obj 'harness'
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $source = Join-Path $PSScriptRoot 'sdl_harness.cpp'
    $cmd = @"
@echo off
call "$vcvars" >nul || exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /W4 /WX /utf-8 /DUNICODE /D_UNICODE /I"$sdlSource\include" /Fo"$work\\" /Fe"$exe" "$source" winmm.lib || exit /b 1
"@
    $cmdPath = Join-Path $work 'build.cmd'
    Set-Content -Path $cmdPath -Value $cmd -Encoding ascii
    Run $env:ComSpec "/d /c `"$cmdPath`"" (Join-Path $work 'build.log')
    Set-Content -Path $harnessStampPath -Value $harnessStamp -Encoding ascii
    Write-Host "built sdl_harness.exe against SDL $sdlCommit's headers"
}
