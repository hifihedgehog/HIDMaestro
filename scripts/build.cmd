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

echo Linking %DRIVER_NAME%.dll ...

set "UM_LIB=%WDK%\Lib\%WDK_VER%\um\x64"
set "WDF_LIB=%WDK%\Lib\wdf\umdf\x64\%UMDF_VER%"

link.exe /nologo /DLL ^
    "/OUT:%OUT_DIR%\%DRIVER_NAME%.dll" ^
    "/LIBPATH:%UM_LIB%" ^
    "/LIBPATH:%WDF_LIB%" ^
    "%OUT_DIR%\driver.obj" ^
    WdfDriverStubUm.lib ^
    ntdll.lib ^
    OneCoreUAP.lib ^
    mincore.lib

if errorlevel 1 (
    echo LINK FAILED
    exit /b 1
)

copy /y "%DRIVER_DIR%\hidmaestro.inf" "%OUT_DIR%\" >nul

echo.
echo BUILD SUCCEEDED: %OUT_DIR%\%DRIVER_NAME%.dll
echo.
