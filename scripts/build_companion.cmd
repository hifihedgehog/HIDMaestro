@echo off
setlocal enabledelayedexpansion
set "DRIVER_DIR=%~dp0..\driver"
set "OUT_DIR=%~dp0..\build"
set "WDK=C:\Program Files (x86)\Windows Kits\10"
set "WDK_VER=10.0.26100.0"
set "UMDF_VER=2.15"

for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%%B\VC\Auxiliary\Build\vcvarsall.bat"
    )
)
call "%VCVARS%" amd64 >nul 2>&1

echo Compiling HIDMaestroCompanion.dll ...
cl.exe /nologo /W4 /GS /Gz /wd4324 ^
    /D _AMD64_ /D _WIN64 /D UNICODE /D _UNICODE ^
    /D UMDF_VERSION_MAJOR=2 /D UMDF_VERSION_MINOR=15 ^
    "/I%WDK%\Include\%WDK_VER%\um" "/I%WDK%\Include\%WDK_VER%\shared" ^
    "/I%WDK%\Include\%WDK_VER%\km" "/I%WDK%\Include\wdf\umdf\%UMDF_VER%" ^
    "/Fo%OUT_DIR%\\" /c "%DRIVER_DIR%\companion.c"
if errorlevel 1 (echo COMPILE FAILED & exit /b 1)

link.exe /nologo /DLL "/OUT:%OUT_DIR%\HIDMaestroCompanion.dll" ^
    "/LIBPATH:%WDK%\Lib\%WDK_VER%\um\x64" "/LIBPATH:%WDK%\Lib\wdf\umdf\x64\%UMDF_VER%" ^
    "%OUT_DIR%\companion.obj" WdfDriverStubUm.lib ntdll.lib OneCoreUAP.lib mincore.lib advapi32.lib
if errorlevel 1 (echo LINK FAILED & exit /b 1)

copy /y "%OUT_DIR%\HIDMaestroCompanion.dll" "%OUT_DIR%\HMXInput.dll" >nul
echo BUILD SUCCEEDED
