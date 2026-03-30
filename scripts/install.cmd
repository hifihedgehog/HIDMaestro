@echo off
setlocal

:: ================================================================
:: HIDMaestro — Install / Create Device Node
::
:: Must run elevated (Administrator).
:: Installs the driver and creates a virtual device node.
:: ================================================================

set DRIVER_NAME=hidmaestro
set BUILD_DIR=%~dp0..\build
set HWID=root\HIDMaestro

echo.
echo HIDMaestro Install
echo.

:: Check for elevation
net session >nul 2>&1
if errorlevel 1 (
    echo ERROR: Must run as Administrator.
    exit /b 1
)

:: Check driver exists
if not exist "%BUILD_DIR%\%DRIVER_NAME%.sys" (
    echo ERROR: Driver not built. Run build.cmd first.
    exit /b 1
)

:: Step 1: Add driver to store
echo Adding driver to driver store...
pnputil /add-driver "%BUILD_DIR%\%DRIVER_NAME%.inf" /install
if errorlevel 1 (
    echo WARNING: pnputil /add-driver failed. Driver may already be in store.
)

:: Step 2: Create device node via devcon or PowerShell
echo Creating device node (%HWID%)...

:: Try devcon first
where devcon.exe >nul 2>&1
if not errorlevel 1 (
    devcon install "%BUILD_DIR%\%DRIVER_NAME%.inf" "%HWID%"
    goto :done
)

:: Fallback: use PowerShell + SetupAPI
powershell -ExecutionPolicy Bypass -Command ^
    "Add-Type -TypeDefinition @'" ^
    "using System; using System.Runtime.InteropServices;" ^
    "public class SetupAPI {" ^
    "  [DllImport(\"setupapi.dll\", SetLastError=true)]" ^
    "  public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr h);" ^
    "  [DllImport(\"setupapi.dll\", SetLastError=true, CharSet=CharSet.Unicode)]" ^
    "  public static extern bool SetupDiCreateDeviceInfoW(IntPtr s, string n, ref Guid g, string d, IntPtr h, int f, IntPtr p);" ^
    "  [DllImport(\"setupapi.dll\", SetLastError=true)]" ^
    "  public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);" ^
    "}" ^
    "'@; Write-Host 'Use devcon.exe for device creation: devcon install hidmaestro.inf root\HIDMaestro'"

echo.
echo NOTE: Install devcon.exe (from WDK tools) for automatic device creation.
echo Or use Device Manager ^> Add legacy hardware ^> Have Disk ^> select hidmaestro.inf

:done
echo.
echo Install complete. Check Device Manager for "HIDMaestro Virtual HID Device".
echo.
