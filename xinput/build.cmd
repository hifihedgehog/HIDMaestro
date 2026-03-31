@echo off
setlocal

set "WDK=C:\Program Files (x86)\Windows Kits\10"
set "WDK_VER=10.0.26100.0"
set "OUT=%~dp0..\build"

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

call "%VCVARS%" amd64 >nul 2>&1

echo Building xinput1_4.dll (XInput wrapper)...

cl.exe /nologo /W4 /O2 /GS /LD ^
    /D WIN32_LEAN_AND_MEAN /D _AMD64_ /D _WIN64 /D UNICODE /D _UNICODE ^
    "/I%WDK%\Include\%WDK_VER%\um" ^
    "/I%WDK%\Include\%WDK_VER%\shared" ^
    "/Fo%OUT%\\" ^
    "%~dp0xinput_wrapper.c" ^
    "/Fe%OUT%\xinput1_4.dll" ^
    "/link" "/LIBPATH:%WDK%\Lib\%WDK_VER%\um\x64" ^
    hid.lib setupapi.lib user32.lib

if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

echo.
echo BUILD SUCCEEDED: %OUT%\xinput1_4.dll
echo.
echo Copy to a game directory to enable XInput support for HIDMaestro controllers.
