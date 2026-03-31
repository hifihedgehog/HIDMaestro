$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$driverDir = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\driver"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\inf2cat.exe"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_deploy.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== Deploying XUSB Device ==="

# Copy XUSB INF to build dir
Copy-Item "$driverDir\hidmaestro_xusb.inf" "$build\" -Force
Log "[1] Copied XUSB INF"

# Create catalog
& $inf2cat /driver:"$build" /os:10_X64 2>&1 | Out-Null
Log "[2] Created catalog"

# Sign DLL and catalog
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\HIDMaestro.dll" 2>&1 | Out-Null
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro_xusb.cat" 2>&1 | Out-Null
Log "[3] Signed"

# Remove old XUSB device if exists
pnputil /remove-device "ROOT\HIDMaestroXUSB\0000" /subtree 2>&1 | Out-Null

# Remove old XUSB driver from store
$drivers = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
foreach ($line in ($drivers -split "`n")) {
    if ($line -match "Published Name:\s+(oem\d+\.inf)") { $currentOem = $Matches[1].Trim() }
    if ($currentOem -and $line -match "hidmaestro_xusb") {
        Log "  Removing old $currentOem"
        pnputil /delete-driver $currentOem /force 2>&1 | Out-Null
        $currentOem = $null
    }
    if ([string]::IsNullOrWhiteSpace($line)) { $currentOem = $null }
}

# Add XUSB driver to store
Log "[4] Adding XUSB driver..."
$result = pnputil /add-driver "$build\hidmaestro_xusb.inf" /install 2>&1 | Out-String
Log $result.Trim()

# Create XUSB device node
Log "[5] Creating XUSB device node..."
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XNode {
    static readonly Guid SYS_GUID = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("newdev.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h,string hw,string inf,int fl,out bool rb);
    public static string Go(string inf) {
        Guid g = SYS_GUID;
        string hw = "root\\HIDMaestroXUSB";
        byte[] hb = Encoding.Unicode.GetBytes(hw + "\0\0");
        IntPtr ds = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (ds == new IntPtr(-1)) return "E1:" + Marshal.GetLastWin32Error();
        try {
            var d = new S { sz = Marshal.SizeOf(typeof(S)) };
            if (!SetupDiCreateDeviceInfoW(ds, "System", ref g, "HIDMaestro XInput Bridge", IntPtr.Zero, 1, ref d))
                return "E2:0x" + Marshal.GetLastWin32Error().ToString("X");
            if (!SetupDiSetDeviceRegistryPropertyW(ds, ref d, 1, hb, hb.Length))
                return "E3:0x" + Marshal.GetLastWin32Error().ToString("X");
            if (!SetupDiCallClassInstaller(0x19, ds, ref d))
                return "E4:0x" + Marshal.GetLastWin32Error().ToString("X");
            bool rb;
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hw, inf, 0, out rb))
                return "E5:0x" + Marshal.GetLastWin32Error().ToString("X");
            return "OK" + (rb ? " (reboot)" : "");
        } finally { SetupDiDestroyDeviceInfoList(ds); }
    }
}
"@

$result = [XNode]::Go("$build\hidmaestro_xusb.inf")
Log "  Device creation: $result"

Start-Sleep 2

# Verify
$dev = pnputil /enum-devices /instanceid "ROOT\HIDMaestroXUSB\0000" 2>&1 | Out-String
Log "`n[6] Verification:"
Log $dev.Trim()

Log "`n=== Done ==="
