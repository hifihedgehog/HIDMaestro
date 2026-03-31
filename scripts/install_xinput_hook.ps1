# Install HIDMaestro XInput hook system-wide via AppInit_DLLs
# Must run elevated.
param([switch]$Uninstall)

$dllPath = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build\hidmaestro_xinput.dll"
$regKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows"

if ($Uninstall) {
    Write-Host "Uninstalling HIDMaestro XInput hook..."

    # Read current AppInit_DLLs
    $current = (Get-ItemProperty $regKey -EA SilentlyContinue).AppInit_DLLs
    if ($current -and $current -match [regex]::Escape($dllPath)) {
        $new = ($current -replace [regex]::Escape($dllPath), "").Trim().Trim(",").Trim()
        Set-ItemProperty $regKey -Name "AppInit_DLLs" -Value $new
        Write-Host "Removed from AppInit_DLLs"
    }

    # Disable if we were the only one
    $remaining = (Get-ItemProperty $regKey -EA SilentlyContinue).AppInit_DLLs
    if ([string]::IsNullOrWhiteSpace($remaining)) {
        Set-ItemProperty $regKey -Name "LoadAppInit_DLLs" -Value 0 -Type DWord
        Write-Host "Disabled AppInit_DLLs loading"
    }

    Write-Host "Done. XInput hook uninstalled."
    return
}

# Install
if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: $dllPath not found. Build first with xinput\build_hook.cmd"
    exit 1
}

Write-Host "Installing HIDMaestro XInput hook..."
Write-Host "  DLL: $dllPath"

# Check current AppInit_DLLs
$current = (Get-ItemProperty $regKey -EA SilentlyContinue).AppInit_DLLs
if ($current -match [regex]::Escape($dllPath)) {
    Write-Host "  Already installed."
} else {
    # Add our DLL
    $new = if ([string]::IsNullOrWhiteSpace($current)) { $dllPath } else { "$current $dllPath" }
    Set-ItemProperty $regKey -Name "AppInit_DLLs" -Value $new
    Write-Host "  Added to AppInit_DLLs"
}

# Enable AppInit_DLLs loading
Set-ItemProperty $regKey -Name "LoadAppInit_DLLs" -Value 1 -Type DWord

# RequireSignedAppInit_DLLs — our DLL is self-signed, need to allow unsigned
# (or sign it with our existing cert)
$reqSigned = (Get-ItemProperty $regKey -EA SilentlyContinue).RequireSignedAppInit_DLLs
Write-Host "  RequireSignedAppInit_DLLs: $reqSigned"
if ($reqSigned -eq 1) {
    Write-Host "  WARNING: AppInit_DLLs requires signed DLLs."
    Write-Host "  Signing our hook DLL..."
    $signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
    & $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 $dllPath 2>&1 | Out-Null
    $sig = Get-AuthenticodeSignature $dllPath
    Write-Host "  Signature: $($sig.Status)"
}

# Verify
$final = (Get-ItemProperty $regKey -EA SilentlyContinue)
Write-Host "`nCurrent settings:"
Write-Host "  LoadAppInit_DLLs: $($final.LoadAppInit_DLLs)"
Write-Host "  AppInit_DLLs: $($final.AppInit_DLLs)"
Write-Host "  RequireSignedAppInit_DLLs: $($final.RequireSignedAppInit_DLLs)"

Write-Host "`nDone. New processes will have XInput hook loaded."
Write-Host "NOTE: Already-running processes are not affected. Restart games to pick up the hook."
