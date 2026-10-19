@echo off
setlocal enabledelayedexpansion

:: ================================================================
:: HIDMaestro Build Script (UMDF2: compiles as DLL)
:: ================================================================

set "DRIVER_NAME=HIDMaestro"
set "DRIVER_DIR=%~dp0..\driver"
set "INC_DIR=%~dp0..\include"
echo.
echo HIDMaestro UMDF2 Build
call "%~dp0native_env.cmd" %1
if errorlevel 1 exit /b 1
echo.

:: The time every INF of this build is stamped with, taken once at the
:: start (stamp_inf.ps1). build_all.cmd sets HM_STAMP_TIME for both
:: architectures, and a caller can set it to drive the stamp.
if not defined HM_STAMP_TIME for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "(Get-Date).ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture)"`) do set "HM_STAMP_TIME=%%T"
if not defined HM_STAMP_TIME (
    echo STAMP TIME NOT READ
    exit /b 1
)

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

echo Compiling %DRIVER_NAME%.dll ...

set "UM_INC=%HM_SDK_DIR%\Include\%WDK_VER%\um"
set "SHARED_INC=%HM_SDK_DIR%\Include\%WDK_VER%\shared"
set "KM_INC=%HM_WDK_DIR%\Include\%WDK_VER%\km"
set "WDF_INC=%HM_WDK_DIR%\Include\wdf\umdf\%UMDF_VER%"

:: In the kit's wdfrequest.h, the POINTER_ALIGNMENT members of the Create
:: parameters in WDF_REQUEST_PARAMETERS raise C4324 (structure padded due
:: to alignment specifier) at /W4. /external:I marks the UMDF headers
:: external and /external:W0 turns warnings off in them only, so C4324 and
:: every other /W4 warning still reports in our own sources. A header
:: included from an external header is external too, wherever it lives. No
:: HIDMaestro header is included that way today, and none may be. Microsoft
:: Learn, "/external (External headers diagnostics)".
:: /Ox /Os is the WDK's own release setting for a driver
:: (WindowsDriver.Common.props, UseDebugLibraries false: Optimization
:: Full, FavorSizeOrSpeed Size). Without it cl.exe compiles /Od, and
:: every frame, feature answer and XInput reply ran unoptimized code.
cl.exe /nologo /W4 /GS /Gz /Ox /Os ^
    /D %HM_ARCH_DEFINE% /D _WIN64 /D UNICODE /D _UNICODE ^
    /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 ^
    "/I%UM_INC%" ^
    "/I%SHARED_INC%" ^
    "/I%KM_INC%" ^
    "/external:I%WDF_INC%" /external:W0 ^
    "/I%INC_DIR%" ^
    "/I%OUT_DIR%" ^
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

set "UM_LIB=%HM_SDK_DIR%\Lib\%WDK_VER%\um\%HM_ARCH%"
set "WDF_LIB=%HM_WDK_DIR%\Lib\wdf\umdf\%HM_ARCH%\%UMDF_VER%"

link.exe /nologo /DLL /MACHINE:%HM_ARCH% ^
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
:: hmswd.exe: SWD-enumerated device creation helper. Invoked by the SDK
:: to create devices with real ContainerIds (bypasses a .NET P/Invoke
:: incompatibility with cfgmgr32!SwDeviceCreate on Win11 26200: see
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
    link.exe /nologo /MACHINE:%HM_ARCH% ^
        "/OUT:%OUT_DIR%\hmswd.exe" ^
        "/LIBPATH:%UM_LIB%" ^
        "%OUT_DIR%\hmswd.obj" ^
        "%OUT_DIR%\res_hmswd.res" ^
        swdevice.lib cfgmgr32.lib ole32.lib newdev.lib
    if errorlevel 1 (
        echo HMSWD LINK FAILED
        exit /b 1
    )
)

:: Stamp each INF's DriverVer with the build's date + HHmm build number.
:: The committed source INF keeps a stable 1.x.y.0 for review; the build/
:: INF gets a fresh stamp so every package is uniquely versioned: pnputil
:: will never see "same version, skip install" against a prior DriverStore
:: directory (which was the failure mode that hid every driver bugfix in
:: this session behind a stale already-installed binary).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro.inf" -Dest "%OUT_DIR%\hidmaestro.inf" -Architecture %HM_ARCH% -Time "%HM_STAMP_TIME%"
if errorlevel 1 exit /b 1
if exist "%DRIVER_DIR%\hidmaestro_xusb.inf" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro_xusb.inf" -Dest "%OUT_DIR%\hidmaestro_xusb.inf" -Architecture %HM_ARCH% -Time "%HM_STAMP_TIME%"
if errorlevel 1 exit /b 1

echo.
echo BUILD SUCCEEDED: %OUT_DIR%\%DRIVER_NAME%.dll
echo.
