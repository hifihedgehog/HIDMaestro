@echo off
setlocal

:: ================================================================
:: HIDMaestro — Test Signing Script
::
:: Creates a self-signed test certificate and signs the driver.
:: After signing, enable test signing mode and reboot:
::
::   bcdedit /set testsigning on
::   shutdown /r /t 0
::
:: To disable test signing later:
::   bcdedit /set testsigning off
:: ================================================================

set DRIVER_NAME=hidmaestro
set BUILD_DIR=%~dp0..\build
set CERT_NAME=HIDMaestroTestCert
set CERT_STORE=PrivateCertStore
set WDK_BIN=C:\Program Files (x86)\Windows Kits\10\bin

:: Find latest WDK bin version
set "WDK_BIN_VER="
for /d %%d in ("%WDK_BIN%\*") do (
    if exist "%%d\x64\signtool.exe" set "WDK_BIN_VER=%%d\x64"
)

if "%WDK_BIN_VER%"=="" (
    echo ERROR: signtool.exe not found in WDK bin directory.
    exit /b 1
)

set SIGNTOOL=%WDK_BIN_VER%\signtool.exe
set MAKECERT=%WDK_BIN_VER%\makecert.exe
set CERTMGR=%WDK_BIN_VER%\certmgr.exe
set INF2CAT=%WDK_BIN_VER%\inf2cat.exe

echo.
echo HIDMaestro Test Signing
echo   signtool: %SIGNTOOL%
echo   Driver:   %BUILD_DIR%\%DRIVER_NAME%.sys
echo.

:: Check driver exists
if not exist "%BUILD_DIR%\%DRIVER_NAME%.sys" (
    echo ERROR: Driver not built. Run build.cmd first.
    exit /b 1
)

:: Step 1: Create test certificate (if not already present)
echo Creating test certificate "%CERT_NAME%"...
"%MAKECERT%" -r -pe -ss %CERT_STORE% -n "CN=%CERT_NAME%" "%BUILD_DIR%\%CERT_NAME%.cer" 2>nul
if errorlevel 1 (
    echo Certificate may already exist, continuing...
)

:: Step 2: Add cert to Trusted Root (so Windows trusts it in test mode)
echo Adding certificate to Trusted Root store...
certutil -addstore Root "%BUILD_DIR%\%CERT_NAME%.cer" >nul 2>&1
certutil -addstore TrustedPublisher "%BUILD_DIR%\%CERT_NAME%.cer" >nul 2>&1

:: Step 3: Create catalog file
echo Creating catalog file...
if exist "%INF2CAT%" (
    "%INF2CAT%" /driver:"%BUILD_DIR%" /os:10_X64 /verbose 2>nul
) else (
    echo inf2cat not found, skipping catalog. Driver will be embedded-signed.
)

:: Step 4: Sign the driver
echo Signing %DRIVER_NAME%.sys...
"%SIGNTOOL%" sign /v /s %CERT_STORE% /n %CERT_NAME% /t http://timestamp.digicert.com ^
    "%BUILD_DIR%\%DRIVER_NAME%.sys"

if errorlevel 1 (
    echo.
    echo Signing failed. Trying without timestamp...
    "%SIGNTOOL%" sign /v /s %CERT_STORE% /n %CERT_NAME% "%BUILD_DIR%\%DRIVER_NAME%.sys"
)

if errorlevel 1 (
    echo SIGNING FAILED
    exit /b 1
)

:: Step 5: Sign catalog if it exists
if exist "%BUILD_DIR%\%DRIVER_NAME%.cat" (
    echo Signing %DRIVER_NAME%.cat...
    "%SIGNTOOL%" sign /v /s %CERT_STORE% /n %CERT_NAME% /t http://timestamp.digicert.com ^
        "%BUILD_DIR%\%DRIVER_NAME%.cat"
)

:: Verify
echo.
echo Verifying signature...
"%SIGNTOOL%" verify /v /pa "%BUILD_DIR%\%DRIVER_NAME%.sys" 2>nul
if errorlevel 1 (
    echo.
    echo NOTE: Signature verification failed in production mode.
    echo This is expected for test-signed drivers.
    echo Enable test signing mode to load the driver:
    echo.
    echo   bcdedit /set testsigning on
    echo   shutdown /r /t 0
)

echo.
echo SIGNING COMPLETE
echo.
echo To install:
echo   1. bcdedit /set testsigning on  (if not already enabled)
echo   2. Reboot
echo   3. pnputil /add-driver "%BUILD_DIR%\%DRIVER_NAME%.inf" /install
echo.
