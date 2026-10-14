@echo off
setlocal enabledelayedexpansion
set "DRIVER_DIR=%~dp0..\driver"
call "%~dp0native_env.cmd" %1
if errorlevel 1 exit /b 1

:: Same version resource as the driver (see scripts/build.cmd).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gen_version.ps1" ^
    -PropsPath "%~dp0..\Directory.Build.props" -OutPath "%OUT_DIR%\version_gen.h"
if errorlevel 1 (echo VERSION HEADER GENERATION FAILED & exit /b 1)

echo Compiling HIDMaestroCompanion.dll ...
cl.exe /nologo /W4 /GS /Gz /wd4324 ^
    /D %HM_ARCH_DEFINE% /D _WIN64 /D UNICODE /D _UNICODE ^
    /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 ^
    "/I%HM_SDK_DIR%\Include\%WDK_VER%\um" "/I%HM_SDK_DIR%\Include\%WDK_VER%\shared" ^
    "/I%HM_WDK_DIR%\Include\%WDK_VER%\km" "/I%HM_WDK_DIR%\Include\wdf\umdf\%UMDF_VER%" ^
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

:: Also stamp + stage the companion INF so a standalone `build_companion`
:: invocation leaves build/ in a consistent state (otherwise SDK rebuilds
:: after this script would pick up whatever hidmaestro_xusb.inf was in
:: build/ from the last full build: potentially stale).
if exist "%DRIVER_DIR%\hidmaestro_xusb.inf" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stamp_inf.ps1" ^
    -Source "%DRIVER_DIR%\hidmaestro_xusb.inf" -Dest "%OUT_DIR%\hidmaestro_xusb.inf" -Architecture %HM_ARCH%
if errorlevel 1 exit /b 1

echo BUILD SUCCEEDED
