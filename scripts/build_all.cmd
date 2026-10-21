@echo off
setlocal
:: Build the native payloads, then the universal managed SDK.
::
:: Two settings make a smaller build, as environment variables. MSBuild
:: reads them as properties, so they reach the SDK project too. Official
:: releases set neither. docs/design/coexistence.md ("Building from source")
:: lists them with the kit settings.
::   HMSkipArm64=true  x64 only: no ARM64 driver, companion or SWD helper,
::                     and no ARM64 usbip-win2 host controller in the SDK.
::   HMSkipUsbip=true  no USB/IP transport in the SDK.
::
:: Every INF of both architectures is stamped with one time, taken here
:: (stamp_inf.ps1). A caller can set HM_STAMP_TIME, yyyy-MM-ddTHH:mm, to
:: choose it.
if not defined HM_STAMP_TIME for /f "usebackq delims=" %%T in (`powershell -NoProfile -Command "(Get-Date).ToString('yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture)"`) do set "HM_STAMP_TIME=%%T"
if not defined HM_STAMP_TIME (
    echo STAMP TIME NOT READ
    exit /b 1
)
set "HM_ARCHS=x64 arm64"
if /i "%HMSkipArm64%"=="true" set "HM_ARCHS=x64"
for %%A in (%HM_ARCHS%) do (
    call "%~dp0build.cmd" %%A
    if errorlevel 1 exit /b 1
    call "%~dp0build_companion.cmd" %%A
    if errorlevel 1 exit /b 1
)
call "%~dp0build_openvr.cmd"
if errorlevel 1 exit /b 1

:: One pass. The SDK embeds the payload its PackResources target stages in
:: the same build.
dotnet build "%~dp0..\sdk\HIDMaestro.Core\HIDMaestro.Core.csproj" -nologo -v minimal
if errorlevel 1 exit /b 1

set "HM_BUILT=x64 and ARM64 native payloads"
if /i "%HMSkipArm64%"=="true" set "HM_BUILT=x64 native payload only"
set "HM_TRANSPORT=with the USB/IP transport"
if /i "%HMSkipUsbip%"=="true" set "HM_TRANSPORT=without the USB/IP transport"
echo BUILD SUCCEEDED: %HM_BUILT%, universal HIDMaestro.Core.dll %HM_TRANSPORT%
exit /b 0
