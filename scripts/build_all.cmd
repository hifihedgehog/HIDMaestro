@echo off
:: HIDMaestro — build all native drivers in one step.
:: Runs build.cmd (main HID minidriver + xusbshim filter + INF stamping)
:: and build_companion.cmd (HMCOMPANION / HMXInput.dll).

setlocal
set "SCRIPT_DIR=%~dp0"

echo.
echo [1/2] Building main driver + xusbshim filter...
call "%SCRIPT_DIR%build.cmd"
if errorlevel 1 (
    echo build.cmd FAILED
    exit /b 1
)

echo.
echo [2/2] Building HMCOMPANION / HMXInput.dll...
call "%SCRIPT_DIR%build_companion.cmd"
if errorlevel 1 (
    echo build_companion.cmd FAILED
    exit /b 1
)

echo.
echo BUILD_ALL SUCCEEDED
endlocal
