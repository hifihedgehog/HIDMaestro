$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_v3.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

# Step 1: Remove old
pnputil /remove-device "ROOT\HIDMaestroXUSB\0000" /subtree 2>&1 | Out-Null

# Remove old driver from store
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "hidmaestro_xusb") {
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        Log "Removed old $currentOem"
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

# Step 2: Create device node WITHOUT installing driver
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XN5 {
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
        if (!SetupDiCreateDeviceInfoW(ds, "System", ref g, "HIDMaestro XInput Bridge", IntPtr.Zero, 1, ref d))
            return "E2:0x" + Marshal.GetLastWin32Error().ToString("X");
        if (!SetupDiSetDeviceRegistryPropertyW(ds, ref d, 1, hb, hb.Length))
            return "E3:0x" + Marshal.GetLastWin32Error().ToString("X");
        if (!SetupDiCallClassInstaller(0x19, ds, ref d))
            return "E4:0x" + Marshal.GetLastWin32Error().ToString("X");
        SetupDiDestroyDeviceInfoList(ds);
        return "OK";
    }
}
"@

$r = [XN5]::Create()
Log "Device registration: $r"

# Step 3: Add driver and install on matching devices
Log "Adding driver with /install..."
$addResult = pnputil /add-driver "$build\hidmaestro_xusb.inf" /install 2>&1 | Out-String
Log $addResult.Trim()

Start-Sleep 3

# Step 4: Check result
Log "`nDevice state:"
$devResult = pnputil /enum-devices /instanceid "ROOT\HIDMaestroXUSB\0000" 2>&1 | Out-String
Log $devResult.Trim()
