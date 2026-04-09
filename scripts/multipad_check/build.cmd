@echo off
setlocal enabledelayedexpansion

:: ================================================================
:: multipad_check.exe Build Script
:: Compiles the cross-API ordering diagnostic to build/multipad_check.exe.
::
:: Uses MultiPadTester's backend source files (xinput_backend.cpp,
:: dinput_backend.cpp, hidapi_backend.cpp, rawinput_backend.cpp,
:: wgi_backend.cpp + their helpers) compiled into a headless harness.
::
:: Dependencies:
::   - Visual Studio with C++ workload (vcvarsall)
::   - Windows SDK 10.0 (for HID, SetupAPI, DInput, WinRT C++/WinRT, etc.)
::   - Microsoft WIL (header-only, expected at ..\..\..\wil\include
::     relative to this script — clone from github.com/microsoft/wil)
:: ================================================================

set "SRC_DIR=%~dp0"
set "OUT_DIR=%~dp0..\..\build"
set "WIL_INC=%~dp0..\..\..\wil\include"

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
if not exist "%WIL_INC%\wil\com.h" (
    echo ERROR: WIL headers not found at %WIL_INC%
    echo Clone microsoft/wil:
    echo   cd ..\..\..  ^&^&  git clone https://github.com/microsoft/wil.git
    exit /b 1
)

call "%VCVARS%" amd64 >nul 2>&1

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

echo Compiling multipad_check.exe ...

:: /std:c++23 because MultiPadTester uses std::to_underlying / std::cmp_*.
:: /EHsc for C++/WinRT exception model. /MD dynamic CRT (no static link
:: complications with WinRT and the SDK helpers).
cl.exe /nologo /W3 /EHsc /std:c++latest /MD ^
    /D WIN32_LEAN_AND_MEAN /D NOMINMAX /D UNICODE /D _UNICODE ^
    /D _WIN32_WINNT=0x0A00 /D INITGUID ^
    /I"%WIL_INC%" ^
    /Fo"%OUT_DIR%\\" /Fe"%OUT_DIR%\multipad_check.exe" ^
    "%SRC_DIR%multipad_check.cpp" ^
    "%SRC_DIR%xinput_shim.cpp" ^
    "%SRC_DIR%xinput_backend.cpp" ^
    "%SRC_DIR%dinput_backend.cpp" ^
    "%SRC_DIR%hidapi_backend.cpp" ^
    "%SRC_DIR%rawinput_backend.cpp" ^
    "%SRC_DIR%wgi_backend.cpp" ^
    "%SRC_DIR%usb_names.cpp" ^
    "%SRC_DIR%xbox_wireless_hid.cpp" ^
    /link dinput8.lib dxguid.lib hid.lib setupapi.lib ^
          ole32.lib user32.lib windowsapp.lib

if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)

echo BUILD SUCCEEDED: %OUT_DIR%\multipad_check.exe
exit /b 0
