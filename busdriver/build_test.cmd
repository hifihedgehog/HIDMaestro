@echo off
setlocal

set "WDK=C:\Program Files (x86)\Windows Kits\10"
set "WDK_VER=10.0.26100.0"
set "OUT=%~dp0..\build"
set "SIGNTOOL=%WDK%\bin\%WDK_VER%\x64\signtool.exe"

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

call "%VCVARS%" amd64 >nul 2>&1

echo Compiling minimal_test.sys ...

set "KM_INC=%WDK%\Include\%WDK_VER%\km"
set "SHARED_INC=%WDK%\Include\%WDK_VER%\shared"
set "WDF_INC=%WDK%\Include\wdf\kmdf\1.15"

cl.exe /nologo /W4 /GS /Gz /kernel /wd4324 ^
    /D _AMD64_ /D _WIN64 ^
    "/I%KM_INC%" ^
    "/I%SHARED_INC%" ^
    "/I%WDF_INC%" ^
    "/Fo%OUT%\\" ^
    /c "%~dp0minimal_test.c"

if errorlevel 1 (
    echo COMPILE FAILED
    exit /b 1
)

echo Linking minimal_test.sys ...

set "KM_LIB=%WDK%\Lib\%WDK_VER%\km\x64"
set "WDF_LIB=%WDK%\Lib\wdf\kmdf\x64\1.15"

link.exe /nologo /DRIVER /SUBSYSTEM:NATIVE /ENTRY:FxDriverEntry /NODEFAULTLIB ^
    "/OUT:%OUT%\minimal_test.sys" ^
    "/LIBPATH:%KM_LIB%" ^
    "/LIBPATH:%WDF_LIB%" ^
    "%OUT%\minimal_test.obj" ^
    WdfDriverEntry.lib WdfLdr.lib ntoskrnl.lib hal.lib wmilib.lib BufferOverflowFastFailK.lib

if errorlevel 1 (
    echo LINK FAILED
    exit /b 1
)

echo Signing minimal_test.sys ...
"%SIGNTOOL%" sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "%OUT%\minimal_test.sys"

if errorlevel 1 (
    echo SIGN FAILED
    exit /b 1
)

echo.
echo BUILD + SIGN SUCCEEDED: %OUT%\minimal_test.sys
echo.
echo Testing if it loads...
sc create MinimalTest type= kernel binPath= "%OUT%\minimal_test.sys"
sc start MinimalTest
echo Start result: %ERRORLEVEL%
sc stop MinimalTest 2>nul
sc delete MinimalTest
