@echo off
set "OUT=%~dp0..\build"
for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" call "%%B\VC\Auxiliary\Build\vcvarsall.bat" amd64 >nul 2>&1
    )
)
cl.exe /nologo /O2 "%~dp0test_xinput.c" "/Fe%OUT%\test_xinput.exe" /link user32.lib
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Running test...
"%OUT%\test_xinput.exe"
