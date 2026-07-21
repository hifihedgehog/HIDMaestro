@echo off
rem ====================================================================
rem  pre-tag-validate.cmd
rem
rem  Mandatory pre-release validation. Run before `git tag vX.Y.Z`.
rem  - Builds the SDK + test app
rem  - Runs the full live-swap regression battery (41 scenarios, ~32 min)
rem  - Exits non-zero if any scenario FAILed; do NOT tag/push/release in
rem    that case
rem
rem  Per memory:feedback-always-run-swap-regression-before-release.md
rem  this script encodes the discipline rule. The release recipe should
rem  invoke this between "build_all.cmd succeeds" and "git tag".
rem
rem  Requires:
rem    - Elevated cmd/PowerShell (HIDMaestroTest needs admin; if invoked
rem      from a non-elevated shell, the inner test app would re-launch
rem      and orphan the regression script's stdin pipe).
rem    - sudo/gsudo NOT used here (caller is responsible for elevation).
rem
rem  Exit codes:
rem    0  All 41 scenarios PASSED. Safe to tag/push/release.
rem    1  At least one scenario FAILED. Do NOT release.
rem    2  Build failed. Fix before re-running.
rem ====================================================================

setlocal

rem Move to repo root (script lives in scripts/)
pushd "%~dp0..\"

echo.
echo ====================================================================
echo  HIDMaestro pre-tag validation
echo ====================================================================
echo.

rem 1. Verify elevation
net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This script must run elevated.
    echo         Re-run from an Administrator command prompt.
    popd
    exit /b 2
)

rem 2. Build SDK + test app + extractor at the current source version
echo [1/4] Building SDK + driver + test apps...
call scripts\build_all.cmd >nul 2>&1
if errorlevel 1 (
    echo [ERROR] build_all.cmd failed. Run it directly to see the error.
    popd
    exit /b 2
)
dotnet build test\HIDMaestroTest.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] HIDMaestroTest build failed.
    popd
    exit /b 2
)
dotnet build tools\HIDMaestroProfileExtractor\HIDMaestroProfileExtractor.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] HIDMaestroProfileExtractor build failed.
    popd
    exit /b 2
)
dotnet build test\probes\switch_pro_check\SwitchProCheck.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] SwitchProCheck build failed.
    popd
    exit /b 2
)
dotnet build test\probes\switch_descriptor_idle_check\switch_descriptor_idle_check.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] SwitchDescriptorIdleCheck build failed.
    popd
    exit /b 2
)
dotnet build test\probes\switch_pro_sdl3_check\SwitchProSdl3Check.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] SwitchProSdl3Check build failed.
    popd
    exit /b 2
)
echo       BUILD OK
echo.

rem 3. Switch Pro protocol responder check (issue #33). Headless and
rem    self-contained: creates the virtual pad, runs SDL's exact USB init
rem    + subcommand sequence over raw HID, validates 0x30 streaming,
rem    input/IMU round-trip, and rumble decode. 43 asserts, ~15 s.
echo [2/4] Running Switch Pro protocol check...
test\probes\switch_pro_check\bin\Release\net10.0-windows10.0.26100.0\SwitchProCheck.exe
if errorlevel 1 (
    echo ====================================================================
    echo  [FAIL] Switch Pro protocol check failed. DO NOT TAG OR RELEASE.
    echo ====================================================================
    popd
    exit /b 1
)
rem     Pre-handshake descriptor conformance (issue #35): the idle 0x30
rem     stream must parse correctly through the HID descriptor
rem     (DirectInput/joy.cpl) and flip to the Nintendo layout on the
rem     first protocol write. 21 asserts, ~10 s.
test\probes\switch_descriptor_idle_check\bin\Release\net10.0-windows10.0.26100.0\SwitchDescriptorIdleCheck.exe
if errorlevel 1 (
    echo ====================================================================
    echo  [FAIL] Switch descriptor idle check failed. DO NOT TAG OR RELEASE.
    echo ====================================================================
    popd
    exit /b 1
)
rem     Real-SDL3 acceptance (issue #33 acceptance line): SKIPs cleanly
rem     when the sibling SDL3-build checkout is absent; a FAIL is real.
test\probes\switch_pro_sdl3_check\bin\Release\net10.0-windows10.0.26100.0\SwitchProSdl3Check.exe
if errorlevel 1 (
    echo ====================================================================
    echo  [FAIL] Switch Pro SDL3 acceptance failed. DO NOT TAG OR RELEASE.
    echo ====================================================================
    popd
    exit /b 1
)
echo.

rem 4. Run the full regression battery
echo [3/4] Running live-swap regression battery (41 scenarios, ~32 min)...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "test\regression\swap_regression.ps1"
set BATTERY_EXIT=%ERRORLEVEL%
echo.

if %BATTERY_EXIT% neq 0 (
    echo ====================================================================
    echo  [FAIL] Battery exit code %BATTERY_EXIT%. DO NOT TAG OR RELEASE.
    echo  Diagnose via %%TEMP%%\HIDMaestro\teardown_diag.log
    echo ====================================================================
    popd
    exit /b 1
)

echo [4/4] Validation complete.
echo.
echo ====================================================================
echo  [PASS] Switch Pro check + 41/41 swap scenarios passed. Safe to:
echo         git tag vX.Y.Z
echo         git push origin master vX.Y.Z
echo         gh release create vX.Y.Z ...
echo ====================================================================

popd
exit /b 0
