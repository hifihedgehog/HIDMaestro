@echo off
rem ====================================================================
rem  pre-tag-validate.cmd
rem
rem  Mandatory pre-release validation. Run before `git tag vX.Y.Z`.
rem  - Builds the SDK + test app
rem  - Runs the full live-swap regression battery (23 scenarios, ~32 min)
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
rem    0  All 23 scenarios PASSED. Safe to tag/push/release.
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
echo [1/3] Building SDK + driver + test apps...
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
echo       BUILD OK
echo.

rem 3. Run the full regression battery
echo [2/3] Running live-swap regression battery (23 scenarios, ~32 min)...
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

echo [3/3] Validation complete.
echo.
echo ====================================================================
echo  [PASS] 23/23 scenarios passed. Safe to:
echo         git tag vX.Y.Z
echo         git push origin master vX.Y.Z
echo         gh release create vX.Y.Z ...
echo ====================================================================

popd
exit /b 0
