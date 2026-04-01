$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"

# Read VID/PID from registry if available
$vid = "0000"; $devPid = "0000"; $prodName = "HIDMaestro Virtual HID Device"
try {
    $reg = Get-ItemProperty "HKLM:\SOFTWARE\HIDMaestro" -EA Stop
    if ($reg.VendorId)  { $vid = "{0:X4}" -f [int]$reg.VendorId }
    if ($reg.ProductId) { $devPid = "{0:X4}" -f [int]$reg.ProductId }
    if ($reg.ProductString) { $prodName = $reg.ProductString }
} catch {}

Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class DN5 {
    static readonly Guid G = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("newdev.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h,string hw,string inf,int fl,out bool rb);
    public static string Go(string inf, string vid, string pid, string desc) {
        Guid g = G;
        // Hardware IDs: multi-string with VID&PID first, then our match ID
        // HID class uses the first HWID to construct child IDs
        // INF matches on root\HIDMaestro (second entry)
        // Standard hardware ID — WGI standard mapping via GameInput registry
        string hwMulti = "root\\VID_" + vid + "&PID_" + pid + "\0" + "root\\HIDMaestro" + "\0\0";
        byte[] hb = Encoding.Unicode.GetBytes(hwMulti);
        IntPtr ds = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (ds == new IntPtr(-1)) return "E1:" + Marshal.GetLastWin32Error();
        try {
            var d = new S { sz = Marshal.SizeOf(typeof(S)) };
            // Enumerator name becomes part of instance ID and HID child's device interface path.
            // Including "IG_00" makes Chrome's Raw Input filter it as an XInput device.
            if (!SetupDiCreateDeviceInfoW(ds, "HID_IG_00", ref g, desc, IntPtr.Zero, 1, ref d))
                return "E2:0x" + Marshal.GetLastWin32Error().ToString("X");
            // SPDRP_HARDWAREID = 1 (REG_MULTI_SZ)
            if (!SetupDiSetDeviceRegistryPropertyW(ds, ref d, 1, hb, hb.Length))
                return "E3:0x" + Marshal.GetLastWin32Error().ToString("X");
            // DIF_REGISTERDEVICE = 0x19
            if (!SetupDiCallClassInstaller(0x19, ds, ref d))
                return "E4:0x" + Marshal.GetLastWin32Error().ToString("X");
            // Match on root\HIDMaestro for INF
            bool rb;
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, "root\\HIDMaestro", inf, 0, out rb))
                return "E5:0x" + Marshal.GetLastWin32Error().ToString("X");
            return "OK" + (rb ? " (reboot)" : "");
        } finally {
            SetupDiDestroyDeviceInfoList(ds);
        }
    }
}
"@

$r = [DN5]::Go("$build\hidmaestro.inf", $vid, $devPid, $prodName)
Write-Host "Device creation: $r"
