$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xusb_node.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

# Remove old
pnputil /remove-device "ROOT\HIDMaestroXUSB\0000" /subtree 2>&1 | Out-Null

Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class XN4 {
    static readonly Guid G = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("newdev.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h,string hw,string inf,int fl,out bool rb);
    public static string Go(string inf) {
        Guid g = G; string hw = "root\\HIDMaestroXUSB";
        byte[] hb = Encoding.Unicode.GetBytes(hw + "\0\0");
        IntPtr ds = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (ds == new IntPtr(-1)) return "E1:" + Marshal.GetLastWin32Error();
        var d = new S { sz = Marshal.SizeOf(typeof(S)) };
        bool r1 = SetupDiCreateDeviceInfoW(ds, "System", ref g, "HIDMaestro XInput Bridge", IntPtr.Zero, 1, ref d);
        if (!r1) { SetupDiDestroyDeviceInfoList(ds); return "E2:0x" + Marshal.GetLastWin32Error().ToString("X"); }
        bool r2 = SetupDiSetDeviceRegistryPropertyW(ds, ref d, 1, hb, hb.Length);
        if (!r2) { SetupDiDestroyDeviceInfoList(ds); return "E3:0x" + Marshal.GetLastWin32Error().ToString("X"); }
        bool r3 = SetupDiCallClassInstaller(0x19, ds, ref d);
        if (!r3) { SetupDiDestroyDeviceInfoList(ds); return "E4:0x" + Marshal.GetLastWin32Error().ToString("X"); }
        SetupDiDestroyDeviceInfoList(ds);
        // Now install the driver using the full INF path
        bool rb;
        bool r4 = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hw, inf, 1, out rb);
        int lastErr = Marshal.GetLastWin32Error();
        return "Register=OK UpdateDriver=" + r4 + " err=0x" + lastErr.ToString("X") + " reboot=" + rb;
    }
}
"@

$infPath = "$build\hidmaestro_xusb.inf"
Log "INF: $infPath"
Log "Exists: $(Test-Path $infPath)"

$result = [XN4]::Go($infPath)
Log "Result: $result"

Start-Sleep 3

Log "`nDevice state:"
pnputil /enum-devices /instanceid "ROOT\HIDMaestroXUSB\0000" 2>&1 | ForEach-Object { Log "  $_" }
