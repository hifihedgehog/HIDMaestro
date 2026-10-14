@echo off
:: Shared native compiler setup. Called within the build script's setlocal.
::
:: Where the kits and Visual Studio come from. Each setting is an environment
:: variable, and docs/design/v1.13.0.md ("Building from source") lists them.
:: A root the build cannot use stops it here, with an error that names the
:: setting to change.
::   HM_SDK_ROOT        The Windows SDK: the um and shared headers, the um
::                      libraries and rc.exe. Default WindowsSdkDir, which a
::                      Visual Studio or EWDK prompt sets, else the installed kit.
::   HM_WDK_ROOT        The WDK: the km headers and the UMDF headers and stub
::                      library. Default WDKContentRoot, which an EWDK prompt
::                      sets, else HM_SDK_ROOT. For the NuGet WDK, the c folder
::                      of Microsoft.Windows.WDK.x64.
::   HM_WDK_ROOT_ARM64  The WDK for the ARM64 build. Default HM_WDK_ROOT. For
::                      the NuGet WDK, the c folder of Microsoft.Windows.WDK.ARM64,
::                      since each package carries its own architecture's
::                      stub library only.
::   HM_WDK_VERSION     The kits' version folder. Default Version_Number, which
::                      an EWDK prompt sets, else 10.0.26100.0.
::   VSINSTALLDIR       The Visual Studio a Visual Studio or EWDK prompt set.
::                      Without one with the C++ tools, the newest under
::                      Program Files.
set "HM_ARCH=%~1"
if not defined HM_ARCH set "HM_ARCH=x64"
if /i "%HM_ARCH%"=="x64" (set "HM_ARCH=x64") else if /i "%HM_ARCH%"=="arm64" (set "HM_ARCH=arm64") else (
    echo ERROR: architecture must be x64 or arm64.
    exit /b 1
)

:: One line per step, outside parenthesized blocks: a path such as
:: "C:\Program Files (x86)" would close a block it was expanded into. A
:: trailing backslash is dropped through for's ~f, which also makes the
:: path absolute. A substring such as %X:~-1% cannot do it, since cmd
:: expands that even behind "if defined X" and mangles the line when X is
:: unset.
set "HM_SDK_FROM=HM_SDK_ROOT"
set "HM_SDK_DIR=%HM_SDK_ROOT%"
if not defined HM_SDK_DIR set "HM_SDK_FROM=WindowsSdkDir"
if not defined HM_SDK_DIR set "HM_SDK_DIR=%WindowsSdkDir%"
if not defined HM_SDK_DIR set "HM_SDK_FROM=installed kit"
if not defined HM_SDK_DIR set "HM_SDK_DIR=%ProgramFiles(x86)%\Windows Kits\10"
for %%I in ("%HM_SDK_DIR%.") do set "HM_SDK_DIR=%%~fI"

set "HM_WDK_SETTING=HM_WDK_ROOT"
set "HM_NUGET_ARCH=x64"
if "%HM_ARCH%"=="arm64" set "HM_WDK_SETTING=HM_WDK_ROOT_ARM64"
if "%HM_ARCH%"=="arm64" set "HM_NUGET_ARCH=ARM64"
set "HM_WDK_DIR="
if "%HM_ARCH%"=="arm64" if defined HM_WDK_ROOT_ARM64 set "HM_WDK_FROM=HM_WDK_ROOT_ARM64"
if "%HM_ARCH%"=="arm64" if defined HM_WDK_ROOT_ARM64 set "HM_WDK_DIR=%HM_WDK_ROOT_ARM64%"
if not defined HM_WDK_DIR if defined HM_WDK_ROOT set "HM_WDK_FROM=HM_WDK_ROOT"
if not defined HM_WDK_DIR if defined HM_WDK_ROOT set "HM_WDK_DIR=%HM_WDK_ROOT%"
if not defined HM_WDK_DIR if defined WDKContentRoot set "HM_WDK_FROM=WDKContentRoot"
if not defined HM_WDK_DIR if defined WDKContentRoot set "HM_WDK_DIR=%WDKContentRoot%"
if not defined HM_WDK_DIR set "HM_WDK_FROM=%HM_SDK_FROM%"
if not defined HM_WDK_DIR set "HM_WDK_DIR=%HM_SDK_DIR%"
for %%I in ("%HM_WDK_DIR%.") do set "HM_WDK_DIR=%%~fI"

set "HM_VER_FROM=HM_WDK_VERSION"
set "WDK_VER=%HM_WDK_VERSION%"
if not defined WDK_VER set "HM_VER_FROM=Version_Number"
if not defined WDK_VER set "WDK_VER=%Version_Number%"
if not defined WDK_VER set "HM_VER_FROM=default"
if not defined WDK_VER set "WDK_VER=10.0.26100.0"
set "UMDF_VER=2.15"

set "OUT_DIR=%~dp0..\build"
set "HM_ARCH_DEFINE=_AMD64_"
if "%HM_ARCH%"=="arm64" (
    set "OUT_DIR=%~dp0..\build\arm64"
    set "HM_ARCH_DEFINE=_ARM64_"
)

set "HM_HOST=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "HM_HOST=arm64"

if not exist "%HM_SDK_DIR%\Include\%WDK_VER%\um\windows.h" goto :no_sdk
if not exist "%HM_SDK_DIR%\Include\%WDK_VER%\shared\sdkddkver.h" goto :no_sdk
if not exist "%HM_SDK_DIR%\Lib\%WDK_VER%\um\%HM_ARCH%\kernel32.lib" goto :no_sdk
if not exist "%HM_SDK_DIR%\bin\%WDK_VER%\%HM_HOST%\rc.exe" goto :no_sdk
if not exist "%HM_WDK_DIR%\Include\%WDK_VER%\km\hidport.h" goto :no_wdk
if not exist "%HM_WDK_DIR%\Include\wdf\umdf\%UMDF_VER%\wdf.h" goto :no_wdk
if not exist "%HM_WDK_DIR%\Lib\wdf\umdf\%HM_ARCH%\%UMDF_VER%\WdfDriverStubUm.lib" goto :no_wdk

set "VCVARS="
set "HM_VS_FROM=newest under Program Files"
set "HM_VS_DIR=%VSINSTALLDIR%"
if defined HM_VS_DIR for %%I in ("%HM_VS_DIR%.") do set "HM_VS_DIR=%%~fI"
if defined HM_VS_DIR if exist "%HM_VS_DIR%\VC\Auxiliary\Build\vcvarsall.bat" set "HM_VS_FROM=VSINSTALLDIR"
if defined HM_VS_DIR if exist "%HM_VS_DIR%\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%HM_VS_DIR%\VC\Auxiliary\Build\vcvarsall.bat"
if defined HM_VS_DIR if not defined VCVARS echo   VSINSTALLDIR "%HM_VS_DIR%" has no C++ build tools.
if not defined VCVARS for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%%B\VC\Auxiliary\Build\vcvarsall.bat"
    )
)
if not defined VCVARS (
    echo ERROR: Visual Studio C++ build tools are required.
    exit /b 1
)

echo   SDK  %HM_SDK_DIR% (%HM_SDK_FROM%)
echo   WDK  %HM_WDK_DIR% (%HM_WDK_FROM%)
echo   Kit  %WDK_VER% (%HM_VER_FROM%)
echo   VS   %VCVARS% (%HM_VS_FROM%)

set "HM_VC_ARCH=%HM_HOST%"
if not "%HM_HOST%"=="%HM_ARCH%" set "HM_VC_ARCH=%HM_HOST%_%HM_ARCH%"
:: An explicit compiler/CRT pair supports an unpacked Microsoft toolchain.
if "%HM_ARCH%"=="arm64" if defined HM_ARM64_COMPILER_DIR set "HM_VC_ARCH=%HM_HOST%"
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%VCVARS%" %HM_VC_ARCH% >nul
if errorlevel 1 exit /b 1

if "%HM_ARCH%"=="arm64" if defined HM_ARM64_COMPILER_DIR (
    if not exist "%HM_ARM64_COMPILER_DIR%\cl.exe" (
        echo ERROR: HM_ARM64_COMPILER_DIR must contain the ARM64 cross-compiler.
        exit /b 1
    )
    if not exist "%HM_ARM64_CRT_DIR%\libcmt.lib" (
        echo ERROR: HM_ARM64_CRT_DIR must contain the ARM64 C runtime libraries.
        exit /b 1
    )
    if not exist "%HM_ARM64_CRT_DIR%\oldnames.lib" (
        echo ERROR: HM_ARM64_CRT_DIR must include the complete ARM64 CRT library set.
        exit /b 1
    )
    set "PATH=%HM_ARM64_COMPILER_DIR%;%PATH%"
    set "LIB=%HM_ARM64_CRT_DIR%;%HM_SDK_DIR%\Lib\%WDK_VER%\ucrt\arm64;%HM_SDK_DIR%\Lib\%WDK_VER%\um\arm64"
)

set "RC=%HM_SDK_DIR%\bin\%WDK_VER%\%HM_HOST%\rc.exe"
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
exit /b 0

:no_sdk
echo ERROR: The Windows SDK root "%HM_SDK_DIR%", from %HM_SDK_FROM%, lacks the %WDK_VER% um and shared headers, the %HM_ARCH% um libraries or rc.exe.
echo        Set HM_SDK_ROOT to a Windows SDK root such as %ProgramFiles(x86)%\Windows Kits\10, or HM_WDK_VERSION to its version folder.
exit /b 1

:no_wdk
echo ERROR: The WDK root "%HM_WDK_DIR%", from %HM_WDK_FROM%, lacks the %WDK_VER% km headers or the UMDF %UMDF_VER% headers and %HM_ARCH% stub library.
echo        Set %HM_WDK_SETTING% to a WDK root: an installed WDK, the EWDK's Program Files\Windows Kits\10, or the c folder of the Microsoft.Windows.WDK.%HM_NUGET_ARCH% NuGet package.
exit /b 1
