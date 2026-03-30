$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"

Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class DN3 {
    static readonly Guid G = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    [StructLayout(LayoutKind.Sequential)] struct S { public int sz; public Guid g; public int d; public IntPtr r; }
    [DllImport("setupapi.dll",SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g,IntPtr h);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s,string n,ref Guid g,string d,IntPtr h,int f,ref S p);
    [DllImport("setupapi.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s,ref S d,int p,byte[] b,int sz);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f,IntPtr s,ref S d);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("newdev.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h,string hw,string inf,int fl,out bool rb);
    public static string Go(string inf) {
        Guid g=G; string hw="root\\HIDMaestro"; byte[] hb=Encoding.Unicode.GetBytes(hw+"\0\0");
        IntPtr ds=SetupDiCreateDeviceInfoList(ref g,IntPtr.Zero);
        if(ds==new IntPtr(-1))return "E1:"+Marshal.GetLastWin32Error();
        try{var d=new S{sz=Marshal.SizeOf(typeof(S))};
        if(!SetupDiCreateDeviceInfoW(ds,"HIDClass",ref g,"HIDMaestro Virtual HID Device",IntPtr.Zero,1,ref d))return "E2:0x"+Marshal.GetLastWin32Error().ToString("X");
        if(!SetupDiSetDeviceRegistryPropertyW(ds,ref d,1,hb,hb.Length))return "E3:0x"+Marshal.GetLastWin32Error().ToString("X");
        if(!SetupDiCallClassInstaller(0x19,ds,ref d))return "E4:0x"+Marshal.GetLastWin32Error().ToString("X");
        bool rb;if(!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero,hw,inf,0,out rb))return "E5:0x"+Marshal.GetLastWin32Error().ToString("X");
        return "OK"+(rb?" (reboot)":"");}finally{SetupDiDestroyDeviceInfoList(ds);}
    }
}
"@

$r = [DN3]::Go("$build\hidmaestro.inf")
Write-Host "Device creation: $r"
