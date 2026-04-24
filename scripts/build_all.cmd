@echo off
setlocal enabledelayedexpansion

:: ==========================================================================
:: build_all.cmd - One-shot build for a fresh clone.
::
:: HIDMaestro has native driver components (HIDMaestro.dll + HMXInput.dll
:: + optional HMXusbShim.dll) that MUST exist in build\ before the .NET SDK
:: is compiled. The SDK's PackResources pre-build target copies those
:: binaries into Resources/ to embed them into HIDMaestro.Core.dll, but the
:: pre-build is evaluated AFTER MSBuild's item-evaluation phase - if
:: build\ is empty on a fresh clone, the embedded-resource glob emits zero
:: items and the assembly compiles with NO driver embedded.
::
:: This script performs the correct build order in one pass:
::   1. scripts\build.cmd           - driver.c -> build\HIDMaestro.dll
::                                    (also stamps INFs + builds xusbshim if present)
::   2. scripts\build_companion.cmd - companion.c -> build\HMXInput.dll
::   3. dotnet build                - first SDK build populates Resources/
::   4. dotnet build (again)        - second SDK build embeds fresh bytes
::
:: After this completes, `dotnet run --project example\SdkDemo` works
:: and `HIDMaestroTest.exe` in test\bin\... can deploy virtual controllers.
:: ==========================================================================

echo.
echo ==========================================================================
echo   HIDMaestro full build (driver + companion + SDK, two-phase)
echo ==========================================================================

call "%~dp0build.cmd"
if errorlevel 1 (
    echo.
    echo ERROR: scripts\build.cmd failed. See output above.
    exit /b 1
)

call "%~dp0build_companion.cmd"
if errorlevel 1 (
    echo.
    echo ERROR: scripts\build_companion.cmd failed. See output above.
    exit /b 1
)

echo.
echo ---- SDK phase 1 (populates Resources/ from build/) ----
dotnet build "%~dp0..\sdk\HIDMaestro.Core\HIDMaestro.Core.csproj" -nologo -v minimal
if errorlevel 1 (
    echo.
    echo ERROR: SDK build phase 1 failed. See output above.
    exit /b 1
)

echo.
echo ---- SDK phase 2 (embeds fresh driver binaries) ----
dotnet build "%~dp0..\sdk\HIDMaestro.Core\HIDMaestro.Core.csproj" -nologo -v minimal
if errorlevel 1 (
    echo.
    echo ERROR: SDK build phase 2 failed. See output above.
    exit /b 1
)

echo.
echo ==========================================================================
echo   BUILD SUCCEEDED
echo ==========================================================================
echo.
echo   You can now run:
echo     dotnet run --project example\SdkDemo
echo     dotnet build test\HIDMaestroTest.csproj
echo.
endlocal
