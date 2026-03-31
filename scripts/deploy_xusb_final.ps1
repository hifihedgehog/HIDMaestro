$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_final.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== XUSB Final Deploy ==="

# Step 1: Set XusbMode=1 in registry BEFORE device creation
$regKey = "HKLM:\SOFTWARE\HIDMaestro"
if (-not (Test-Path $regKey)) { New-Item -Path $regKey -Force | Out-Null }
Set-ItemProperty $regKey -Name "XusbMode" -Value 1 -Type DWord
Log "[1] XusbMode=1 set"

# Step 2: Remove old XUSB devices
Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" } | ForEach-Object {
    pnputil /remove-device $_.InstanceId /subtree 2>&1 | Out-Null
    Log "  Removed $($_.InstanceId)"
}

# Step 3: Sign and prepare
& "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\scripts\resign_xusb.ps1" 2>&1 | Out-Null
& "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\HIDMaestro.dll" 2>&1 | Out-Null
Log "[2] Signed"

# Step 4: Remove old driver from store and re-add
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "hidmaestro_xusb") {
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}
$addResult = pnputil /add-driver "$build\hidmaestro_xusb.inf" 2>&1 | Out-String
Log "[3] Driver: $($addResult.Trim())"

# Step 5: Create XUSB device node
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XNF {
    static readonly Guid G = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    public static string Create() {
        Guid g = G; string hw = "root\\HIDMaestroXUSB";
        byte[] hb = Encoding.Unicode.GetBytes(hw + "\0\0");
        IntPtr ds = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        var d = new S { sz = Marshal.SizeOf(typeof(S)) };
        SetupDiCreateDeviceInfoW(ds, "System", ref g, "HIDMaestro XInput Bridge", IntPtr.Zero, 1, ref d);
        SetupDiSetDeviceRegistryPropertyW(ds, ref d, 1, hb, hb.Length);
        SetupDiCallClassInstaller(0x19, ds, ref d);
        SetupDiDestroyDeviceInfoList(ds);
        return "OK";
    }
}
"@
[XNF]::Create() | Out-Null
Log "[4] Device node created"

Start-Sleep 5

# Step 6: Verify
$dev = Get-PnpDevice 2>$null | Where-Object { $_.FriendlyName -match "XInput Bridge" }
if ($dev) {
    Log "[5] Device: $($dev.InstanceId) Status=$($dev.Status)"
    # Check XUSB interface
    $ifaces = pnputil /enum-interfaces /class "{EC87F1E3-C13B-4100-B5F7-8B84D54260CB}" 2>&1 | Out-String
    Log "XUSB Interfaces: $($ifaces.Trim())"
} else {
    Log "[5] XUSB device NOT FOUND"
}

# Step 7: Clear XusbMode so HID device works next time
Set-ItemProperty $regKey -Name "XusbMode" -Value 0 -Type DWord
Log "[6] XusbMode=0 restored"

Log "`n=== Done ==="
