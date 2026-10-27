@echo off
rem ====================================================================
rem  pre-tag-validate.cmd
rem
rem  Mandatory pre-release validation. Run before `git tag vX.Y.Z`.
rem  - Builds the SDK + test app
rem  - Runs the full regression battery (101 scenarios, 30 min
rem    on the development laptop, a Lenovo Legion Pro 7)
rem  - Exits non-zero if any scenario FAILed; do NOT tag/push/release in
rem    that case
rem
rem  This script encodes the release rule: the full battery passes before
rem  any tag. The release recipe should invoke this between
rem  "build_all.cmd succeeds" and "git tag".
rem
rem  Requires:
rem    - Elevated cmd/PowerShell (HIDMaestroTest needs admin; if invoked
rem      from a non-elevated shell, the inner test app would re-launch
rem      and orphan the regression script's stdin pipe).
rem    - sudo/gsudo NOT used here (caller is responsible for elevation).
rem
rem  Exit codes:
rem    0  All 101 scenarios PASSED. Safe to tag/push/release.
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

rem 2. Build SDK + test app + extractor at the current source version.
rem    A release carries every payload, so the two settings that leave one
rem    out are cleared first: one left in the environment would build a
rem    smaller SDK. S78 (build_info_check) also fails the battery unless
rem    the SDK reports itself complete.
set "HMSkipArm64="
set "HMSkipUsbip="
rem    A release is stamped with the time its build starts, never a
rem    stamp time left in the environment (stamp_inf.ps1).
set "HM_STAMP_TIME="
echo [1/3] Building SDK + driver + test apps...
call scripts\build_all.cmd >nul 2>&1
if errorlevel 1 (
    echo [ERROR] build_all.cmd failed. Run it directly to see the error.
    popd
    exit /b 2
)
rem    Every kit layout the build supports (NuGet WDK, an EWDK prompt's
rem    variables) and the errors for wrong roots, built from copies of this
rem    tree and compared with the build above.
echo        Checking build layouts...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\check_build_layouts.ps1"
if errorlevel 1 (
    echo [ERROR] A build layout check failed. See its output above.
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
rem The profile check tool ships in the release ZIP beside the extractor,
rem and profile_model_check (S92) runs this build of it.
dotnet build tools\HIDMaestroProfileCheck\HIDMaestroProfileCheck.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] HIDMaestroProfileCheck build failed.
    popd
    exit /b 2
)
rem The removal tool ships in the release ZIP as NativeAOT for x64 and
rem ARM64, which this build publishes into aot-x64\ and aot-arm64\, and
rem uninstall_check (S101) runs it.
dotnet build tools\HIDMaestroUninstall\HIDMaestroUninstall.csproj -c Release --nologo -v:minimal >nul 2>&1
if errorlevel 1 (
    echo [ERROR] HIDMaestroUninstall build failed.
    popd
    exit /b 2
)
rem Rebuild every managed probe so the battery's probe scenarios (S24+)
rem run the SDK built THIS run, not whatever a prior iteration left in
rem bin\. RID-agnostic (plain dotnet build): the harness consumes that
rem flavor and its freshness gate (regression_battery.ps1) hash-compares
rem each probe's HIDMaestro.Core.dll against the SDK project's own
rem RID-agnostic output, refusing to run stale. This loop is what makes
rem the gate pass automatically. 2026-07-21 audit: a battery ran a
rem same-version-but-days-old probe SDK, which the old version-only
rem gate could not see.
echo        Rebuilding probe set...
for /d %%p in (test\probes\*) do (
    if exist "%%p\*.csproj" (
        dotnet build "%%p" -c Release --nologo -v:quiet >nul 2>&1
        if errorlevel 1 (
            echo [ERROR] probe build failed: %%p
            popd
            exit /b 2
        )
    )
)
rem program_equivalence_check (S94) draws its 1,000 random programs at
rem build time. Each run draws them from a new seed, printed here and by the
rem probe, so a failing corpus can be built again with the same seed.
set /a HM_CORPUS_SEED=%RANDOM% * 32768 + %RANDOM%
echo        Program corpus seed %HM_CORPUS_SEED%
dotnet build test\probes\program_equivalence_check -c Release --nologo -v:quiet -p:ProgramCorpusSeed=%HM_CORPUS_SEED% >nul 2>&1
if errorlevel 1 (
    echo [ERROR] program_equivalence_check build failed with corpus seed %HM_CORPUS_SEED%.
    popd
    exit /b 2
)
echo       BUILD OK
echo.

rem 3. Run the full regression battery
echo [2/3] Running regression battery (101 scenarios)...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "test\regression\regression_battery.ps1" -StopOnFailure
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
echo  [PASS] 101/101 scenarios passed. Safe to:
echo         git tag vX.Y.Z
echo         git push origin master vX.Y.Z
echo         gh release create vX.Y.Z ...
echo ====================================================================

popd
exit /b 0
