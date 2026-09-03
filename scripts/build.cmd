@echo off
setlocal enabledelayedexpansion

:: ================================================================
:: HIDMaestro Build Script (UMDF2 — compiles as DLL)
:: ================================================================

set "DRIVER_NAME=HIDMaestro"
set "DRIVER_DIR=%~dp0..\driver"
set "INC_DIR=%~dp0..\include"
set "OUT_DIR=%~dp0..\build"
set "WDK=C:\Program Files (x86)\Windows Kits\10"
set "WDK_VER=10.0.26100.0"
set "UMDF_VER=2.15"

:: Find VS
set "VCVARS="
for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%%B\VC\Auxiliary\Build\vcvarsall.bat"
    )
)
if not defined VCVARS (
    echo ERROR: Visual Studio not found.
    exit /b 1
)

echo.
echo HIDMaestro UMDF2 Build
echo   VS:  %VCVARS%
echo   WDK: %WDK_VER%
echo.

call "%VCVARS%" amd64 >nul 2>&1

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

:: Generate build\version_gen.h from Directory.Build.props so the
:: VERSIONINFO in every native binary matches the released version.
:: Binaries with no version resource read as anonymous to antivirus
:: machine-learning models, which is what got HIDMaestro.dll flagged
:: as Trojan:Win32/Bearfoos.A!ml on 2026-09-02.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen_version.ps1" ^
    -PropsPath "%~dp0..\Directory.Build.props" -OutPath "%OUT_DIR%\version_gen.h"
if errorlevel 1 (
    echo VERSION HEADER GENERATION FAILED
    exit /b 1
)

set "RC=%WDK%\bin\%WDK_VER%\x64\rc.exe"

echo Compiling %DRIVER_NAME%.dll ...

set "UM_INC=%WDK%\Include\%WDK_VER%\um"
set "SHARED_INC=%WDK%\Include\%WDK_VER%\shared"
set "KM_INC=%WDK%\Include\%WDK_VER%\km"
set "WDF_INC=%WDK%\Include\wdf\umdf\%UMDF_VER%"

cl.exe /nologo /W4 /GS /Gz /wd4324 ^
    /D _AMD64_ /D _WIN64 /D UNICODE /D _UNICODE ^
    /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 ^
    "/I%UM_INC%" ^
    "/I%SHARED_INC%" ^
    "/I%KM_INC%" ^
    "/I%WDF_INC%" ^
    "/I%INC_DIR%" ^
    "/Fo%OUT_DIR%\\" ^
    /c "%DRIVER_DIR%\driver.c"

if errorlevel 1 (
    echo COMPILE FAILED
    exit /b 1
)

"%RC%" /nologo /fo "%OUT_DIR%\res_hidmaestro.res" ^
    "/I%OUT_DIR%" "/I%UM_INC%" "/I%SHARED_INC%" ^
    "%DRIVER_DIR%\res_hidmaestro.rc"
if errorlevel 1 (
    echo RESOURCE COMPILE FAILED
    exit /b 1
)

echo Linking %DRIVER_NAME%.dll ...

set "UM_LIB=%WDK%\Lib\%WDK_VER%\um\x64"
set "WDF_LIB=%WDK%\Lib\wdf\umdf\x64\%UMDF_VER%"

link.exe /nologo /DLL ^
    "/OUT:%OUT_DIR%\%DRIVER_NAME%.dll" ^
    "/LIBPATH:%UM_LIB%" ^
    "/LIBPATH:%WDF_LIB%" ^
    "%OUT_DIR%\driver.obj" ^
    "%OUT_DIR%\res_hidmaestro.res" ^
    WdfDriverStubUm.lib ^
    ntdll.lib ^
    OneCoreUAP.lib ^
    mincore.lib ^
    advapi32.lib

if errorlevel 1 (
    echo LINK FAILED
    exit /b 1
)

:: ----------------------------------------------------------------------
:: hmswd.exe — SWD-enumerated device creation helper. Invoked by the SDK
:: to create devices with real ContainerIds (bypasses a .NET P/Invoke
:: incompatibility with cfgmgr32!SwDeviceCreate on Win11 26200 — see
:: driver\hmswd\hmswd.c header for context).
:: ----------------------------------------------------------------------
if exist "%DRIVER_DIR%\hmswd\hmswd.c" (
    echo.
    echo Compiling hmswd.exe ...
    cl.exe /nologo /W3 /O1 /DUNICODE /D_UNICODE /EHsc ^
        "/I%UM_INC%" "/I%SHARED_INC%" ^
        "/Fo%OUT_DIR%\\" ^
        /c "%DRIVER_DIR%\hmswd\hmswd.c"
    if errorlevel 1 (
        echo HMSWD COMPILE FAILED
        exit /b 1
    )
    "%RC%" /nologo /fo "%OUT_DIR%\res_hmswd.res" ^
        "/I%OUT_DIR%" "/I%UM_INC%" "/I%SHARED_INC%" ^
        "%DRIVER_DIR%\hmswd\res_hmswd.rc"
    if errorlevel 1 (
        echo HMSWD RESOURCE COMPILE FAILED
        exit /b 1
    )
    link.exe /nologo ^
        "/OUT:%OUT_DIR%\hmswd.exe" ^
        "/LIBPATH:%UM_LIB%" ^
        "%OUT_DIR%\hmswd.obj" ^
        "%OUT_DIR%\res_hmswd.res" ^
        swdevice.lib cfgmgr32.lib ole32.lib
    if errorlevel 1 (
        echo HMSWD LINK FAILED
        exit /b 1
    )
)

:: Stamp each INF's DriverVer with today's date + HHmm build number.
:: The committed source INF keeps a stable 1.x.y.0 for review; the build/
:: INF gets a fresh stamp so every package is uniquely versioned — pnputil
:: will never see "same version, skip install" against a prior DriverStore
:: directory (which was the failure mode that hid every driver bugfix in
:: this session behind a stale already-installed binary).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro.inf" -Dest "%OUT_DIR%\hidmaestro.inf"
if exist "%DRIVER_DIR%\hidmaestro_xusb.inf" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro_xusb.inf" -Dest "%OUT_DIR%\hidmaestro_xusb.inf"

echo.
echo BUILD SUCCEEDED: %OUT_DIR%\%DRIVER_NAME%.dll
echo.
