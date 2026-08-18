@echo off
setlocal enabledelayedexpansion

:: ================================================================
:: HIDMaestro OpenVR driver build (issue #32)
::
:: Compiles driver/openvr into build\openvr\hidmaestro\ laid out
:: exactly as a SteamVR driver folder, so the SDK can embed and
:: extract it verbatim:
::
::   build\openvr\hidmaestro\
::     driver.vrdrivermanifest
::     bin\win64\driver_hidmaestro.dll
::     resources\input\*.json          (input profile + legacy bindings)
::     resources\settings\default.vrsettings
::
:: Toolchain discovery mirrors build.cmd. cl.exe is driven directly,
:: same as the UMDF2 driver build, rather than through msbuild, so a
:: bare Build Tools install without the vcxproj targets still works.
:: The vcxproj remains for IDE use.
:: ================================================================

set "SRC_DIR=%~dp0..\driver\openvr"
set "OUT_DIR=%~dp0..\build\openvr"
set "PKG_DIR=%OUT_DIR%\hidmaestro"

:: Find VS (same loop as build.cmd)
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
echo HIDMaestro OpenVR driver build
echo   VS: %VCVARS%
echo.

call "%VCVARS%" amd64 >nul 2>&1

if not exist "%PKG_DIR%\bin\win64" mkdir "%PKG_DIR%\bin\win64"
if not exist "%OUT_DIR%\obj" mkdir "%OUT_DIR%\obj"

echo Compiling driver_hidmaestro.dll ...

cl.exe /nologo /W4 /O2 /EHsc /MD /std:c++17 ^
    /DNDEBUG /D_WINDOWS /D_USRDLL /DWIN32_LEAN_AND_MEAN /DNOMINMAX /D_USE_MATH_DEFINES ^
    /I"%SRC_DIR%\third_party\openvr" /I"%SRC_DIR%\src" ^
    /Fo"%OUT_DIR%\obj\\" /Fe"%PKG_DIR%\bin\win64\driver_hidmaestro.dll" ^
    "%SRC_DIR%\src\controller_device.cpp" ^
    "%SRC_DIR%\src\device_provider.cpp" ^
    "%SRC_DIR%\src\driver_log.cpp" ^
    "%SRC_DIR%\src\hmd_driver_factory.cpp" ^
    "%SRC_DIR%\src\vr_transport.cpp" ^
    /link /DLL /SUBSYSTEM:WINDOWS

if errorlevel 1 (
    echo ERROR: OpenVR driver compile failed.
    exit /b 1
)

:: Stage the driver-folder layout beside the DLL.
copy /y "%SRC_DIR%\driver.vrdrivermanifest" "%PKG_DIR%\" >nul
if not exist "%PKG_DIR%\resources\input" mkdir "%PKG_DIR%\resources\input"
if not exist "%PKG_DIR%\resources\settings" mkdir "%PKG_DIR%\resources\settings"
:: Wildcard, not per-file: a resource added to the source tree but missed
:: here would silently ship a payload without it (the legacy binding is
:: exactly such a file).
copy /y "%SRC_DIR%\resources\input\*.json" "%PKG_DIR%\resources\input\" >nul
copy /y "%SRC_DIR%\resources\settings\default.vrsettings" "%PKG_DIR%\resources\settings\" >nul

echo   OK: %PKG_DIR%
exit /b 0
