$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_deploy2.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

Log "=== Deploying XUSB Device (v2) ==="

# Remove old
pnputil /remove-device "ROOT\HIDMaestroXUSB\0000" /subtree 2>&1 | Out-Null

# The driver should already be in the store from the previous attempt.
# Check:
$drv = pnputil /enum-drivers 2>&1 | Out-String
if ($drv -match "hidmaestro_xusb") { Log "XUSB driver is in store" }
else { Log "XUSB driver NOT in store" }

# Create device node using SetupAPI — use the same hardware ID
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XNode2 {
    static readonly Guid G = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    public static string Create() {
        Guid g = G;
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
            return "OK";
        } finally { SetupDiDestroyDeviceInfoList(ds); }
    }
}
"@

$result = [XNode2]::Create()
Log "Device registration: $result"

# Now scan for hardware changes to trigger PnP matching
Log "Scanning for PnP changes..."
pnputil /scan-devices 2>&1 | Out-Null
Start-Sleep 3

# Verify
$dev = pnputil /enum-devices /instanceid "ROOT\HIDMaestroXUSB\0000" 2>&1 | Out-String
Log $dev.Trim()

# Test opening the XUSB interface
Log "`n=== Testing XUSB Interface Open ==="
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public class XTest {
    static readonly Guid XUSB = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    [DllImport("setupapi.dll",SetLastError=true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll",SetLastError=true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [StructLayout(LayoutKind.Sequential)] public struct SP_DID { public int cbSize; public Guid g; public int f; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DID did);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DID did, IntPtr det, uint sz, out uint req, IntPtr d);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string f, int a, int sh, IntPtr sec, int disp, int fl, IntPtr t);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    public static readonly IntPtr INVALID = new IntPtr(-1);

    public static string Test() {
        var sb = new System.Text.StringBuilder();
        Guid g = XUSB;
        IntPtr ds = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        for (uint idx = 0; idx < 8; idx++) {
            var did = new SP_DID { cbSize = Marshal.SizeOf(typeof(SP_DID)) };
            if (!SetupDiEnumDeviceInterfaces(ds, IntPtr.Zero, ref g, idx, ref did)) break;
            uint req;
            SetupDiGetDeviceInterfaceDetailW(ds, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
            IntPtr det = Marshal.AllocHGlobal((int)req);
            Marshal.WriteInt32(det, 8);
            SetupDiGetDeviceInterfaceDetailW(ds, ref did, det, req, out req, IntPtr.Zero);
            string path = Marshal.PtrToStringUni(det + 4);
            Marshal.FreeHGlobal(det);
            sb.AppendLine("Interface " + idx + ": " + path);
            IntPtr h = CreateFileW(path, unchecked((int)0xC0000000), 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();
            if (h == INVALID) sb.AppendLine("  OPEN FAILED: " + err);
            else { sb.AppendLine("  OPEN SUCCESS!"); CloseHandle(h); }
        }
        SetupDiDestroyDeviceInfoList(ds);
        return sb.ToString();
    }
}
"@

Log ([XTest]::Test())
Start-Sleep 2
