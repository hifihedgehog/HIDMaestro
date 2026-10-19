@echo off
setlocal enabledelayedexpansion
set "DRIVER_DIR=%~dp0..\driver"
call "%~dp0native_env.cmd" %1
if errorlevel 1 exit /b 1

:: The time both INFs are stamped with, taken once at the start, as
:: build.cmd does. build_all.cmd sets HM_STAMP_TIME for the whole build.
if not defined HM_STAMP_TIME for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "(Get-Date).ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture)"`) do set "HM_STAMP_TIME=%%T"
if not defined HM_STAMP_TIME (
    echo STAMP TIME NOT READ
    exit /b 1
)

:: Same version resource as the driver (see scripts/build.cmd).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen_version.ps1" ^
    -PropsPath "%~dp0..\Directory.Build.props" -OutPath "%OUT_DIR%\version_gen.h"
if errorlevel 1 (echo VERSION HEADER GENERATION FAILED & exit /b 1)

echo Compiling HIDMaestroCompanion.dll ...
:: UMDF headers are external at warning level 0, for the reason in
:: scripts/build.cmd (C4324 in the kit's wdfrequest.h). A header included
:: from an external header is external too. No HIDMaestro header is
:: included that way today, and none may be.
:: /Ox /Os: the WDK's release setting, as scripts/build.cmd gives it.
cl.exe /nologo /W4 /GS /Gz /Ox /Os ^
    /D %HM_ARCH_DEFINE% /D _WIN64 /D UNICODE /D _UNICODE ^
    /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 ^
    "/I%HM_SDK_DIR%\Include\%WDK_VER%\um" "/I%HM_SDK_DIR%\Include\%WDK_VER%\shared" ^
    "/I%HM_WDK_DIR%\Include\%WDK_VER%\km" "/external:I%HM_WDK_DIR%\Include\wdf\umdf\%UMDF_VER%" /external:W0 ^
    "/Fo%OUT_DIR%\\" /c "%DRIVER_DIR%\companion.c"
if errorlevel 1 (echo COMPILE FAILED & exit /b 1)

"%RC%" /nologo /fo "%OUT_DIR%\res_companion.res" ^
    "/I%OUT_DIR%" "/I%HM_SDK_DIR%\Include\%WDK_VER%\um" "/I%HM_SDK_DIR%\Include\%WDK_VER%\shared" ^
    "%DRIVER_DIR%\res_companion.rc"
if errorlevel 1 (echo RESOURCE COMPILE FAILED & exit /b 1)

link.exe /nologo /DLL /MACHINE:%HM_ARCH% "/OUT:%OUT_DIR%\HIDMaestroCompanion.dll" ^
    "/LIBPATH:%HM_SDK_DIR%\Lib\%WDK_VER%\um\%HM_ARCH%" "/LIBPATH:%HM_WDK_DIR%\Lib\wdf\umdf\%HM_ARCH%\%UMDF_VER%" ^
    "%OUT_DIR%\companion.obj" "%OUT_DIR%\res_companion.res" WdfDriverStubUm.lib ntdll.lib OneCoreUAP.lib mincore.lib advapi32.lib
if errorlevel 1 (echo LINK FAILED & exit /b 1)

copy /y "%OUT_DIR%\HIDMaestroCompanion.dll" "%OUT_DIR%\HMXInput.dll" >nul

:: Stamp both INFs of this architecture with the build's time, so the two
:: packages carry one DriverVer (stamp_inf.ps1). Under build_all.cmd that
:: is the time build.cmd stamped with, so nothing changes. Run alone, it is
:: a new time: the rebuilt companion's package is then newer than the one
:: installed, so the SDK built from build\ deploys it. Reusing the
:: installed stamp instead would leave the new HMXInput.dll out of the
:: driver store.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro.inf" -Dest "%OUT_DIR%\hidmaestro.inf" -Architecture %HM_ARCH% -Time "%HM_STAMP_TIME%"
if errorlevel 1 exit /b 1
if exist "%DRIVER_DIR%\hidmaestro_xusb.inf" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro_xusb.inf" -Dest "%OUT_DIR%\hidmaestro_xusb.inf" -Architecture %HM_ARCH% -Time "%HM_STAMP_TIME%"
if errorlevel 1 exit /b 1

echo BUILD SUCCEEDED
