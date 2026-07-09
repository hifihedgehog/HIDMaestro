@echo off
setlocal enabledelayedexpansion

:: ================================================================
:: HIDMaestro OpenVR driver build (driver_hidmaestro.dll)
::
:: Compiles driver\openvr\hidmaestro_openvr.vcxproj via msbuild and
:: assembles the complete SteamVR driver-folder layout under
:: build\openvr\hidmaestro\:
::   driver.vrdrivermanifest
::   bin\win64\driver_hidmaestro.dll
::   resources\input\*.json
::   resources\settings\default.vrsettings
:: The SDK's PackResources target embeds that folder verbatim
:: (HIDMaestro.VR.* logical names) and VrDriverBuilder re-creates it
:: under %ProgramData%\HIDMaestro\openvr\hidmaestro at install time.
:: ================================================================

set "OPENVR_DIR=%~dp0..\driver\openvr"
set "OUT_ROOT=%~dp0..\build\openvr\hidmaestro"

:: Find VS (same discovery loop as scripts\build.cmd)
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

echo.
echo Compiling driver_hidmaestro.dll ...
msbuild "%OPENVR_DIR%\hidmaestro_openvr.vcxproj" -nologo -v:minimal ^
    -p:Configuration=Release -p:Platform=x64
if errorlevel 1 (
    echo OPENVR DRIVER BUILD FAILED
    exit /b 1
)

echo Assembling SteamVR driver folder layout ...
copy /y "%OPENVR_DIR%\resources\driver.vrdrivermanifest" "%OUT_ROOT%\" >nul
if errorlevel 1 ( echo MANIFEST COPY FAILED & exit /b 1 )
xcopy /y /i /q "%OPENVR_DIR%\resources\input" "%OUT_ROOT%\resources\input" >nul
if errorlevel 1 ( echo INPUT PROFILE COPY FAILED & exit /b 1 )
xcopy /y /i /q "%OPENVR_DIR%\resources\settings" "%OUT_ROOT%\resources\settings" >nul
if errorlevel 1 ( echo SETTINGS COPY FAILED & exit /b 1 )

echo   build\openvr\hidmaestro\ ready.
endlocal
