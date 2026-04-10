@echo off
setlocal enabledelayedexpansion
set "SRC_DIR=%~dp0"
set "OUT_DIR=%~dp0..\..\build"
set "VCVARS="
for /d %%A in ("C:\Program Files\Microsoft Visual Studio\*") do (
    for /d %%B in ("%%A\*") do (
        if exist "%%B\VC\Auxiliary\Build\vcvarsall.bat" set "VCVARS=%%B\VC\Auxiliary\Build\vcvarsall.bat"
    )
)
if not defined VCVARS (echo ERROR: Visual Studio not found. & exit /b 1)
call "%VCVARS%" amd64 >nul 2>&1
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
cl.exe /nologo /W3 /EHsc /std:c++17 /MD ^
    /D WIN32_LEAN_AND_MEAN /D NOMINMAX /D _WIN32_WINNT=0x0A00 ^
    /Fo"%OUT_DIR%\\" /Fe"%OUT_DIR%\hid_capture.exe" ^
    "%SRC_DIR%hid_capture.cpp" ^
    /link hid.lib setupapi.lib
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD SUCCEEDED: %OUT_DIR%\hid_capture.exe
exit /b 0
