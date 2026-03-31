@echo off
setlocal

set "WDK=C:\Program Files (x86)\Windows Kits\10"
set "WDK_VER=10.0.26100.0"
set "OUT=%~dp0..\build"

for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" call "%%B\VC\Auxiliary\Build\vcvarsall.bat" amd64 >nul 2>&1
    )
)

echo Building hidmaestro_xinput.dll (system-wide XInput hook)...

cl.exe /nologo /W4 /O2 /GS /LD ^
    /D UNICODE /D _UNICODE /D _AMD64_ /D _WIN64 ^
    "/I%WDK%\Include\%WDK_VER%\um" ^
    "/I%WDK%\Include\%WDK_VER%\shared" ^
    "/Fo%OUT%\\" ^
    "%~dp0xinput_hook.c" ^
    "/Fe%OUT%\hidmaestro_xinput.dll" ^
    "/link" "/LIBPATH:%WDK%\Lib\%WDK_VER%\um\x64" ^
    hid.lib setupapi.lib user32.lib

if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

echo.
echo BUILD SUCCEEDED: %OUT%\hidmaestro_xinput.dll
