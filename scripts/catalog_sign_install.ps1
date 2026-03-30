$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x86\inf2cat.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$log = "$build\final_install_log.txt"

"=== HIDMaestro Final Install ===" | Out-File $log

# Clean old catalog
Remove-Item "$build\hidmaestro.cat" -ErrorAction SilentlyContinue

# Step 1: inf2cat
"Step 1: Creating catalog with inf2cat (x86)..." | Tee-Object -Append $log
& $inf2cat /driver:"$build" /os:10_X64 2>&1 | Tee-Object -Append $log

if (-not (Test-Path "$build\hidmaestro.cat")) {
    "  Catalog not created. inf2cat output above." | Tee-Object -Append $log
    return
}
"  Catalog created." | Tee-Object -Append $log

# Step 2: Sign catalog
"Step 2: Signing catalog..." | Tee-Object -Append $log
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" 2>&1 | Tee-Object -Append $log

# Step 3: Verify
$sig = Get-AuthenticodeSignature "$build\hidmaestro.cat"
"  Signature: $($sig.Status)" | Tee-Object -Append $log

# Step 4: Cert to store (idempotent)
"Step 3: Ensuring cert in trusted stores..." | Tee-Object -Append $log
$cer = "$build\HIDMaestroTestCert.cer"
certutil -addstore Root $cer 2>&1 | Out-Null
certutil -addstore TrustedPublisher $cer 2>&1 | Out-Null

# Step 5: pnputil add driver
"Step 4: Adding driver to store..." | Tee-Object -Append $log
pnputil /add-driver "$build\hidmaestro.inf" /install 2>&1 | Tee-Object -Append $log
"  pnputil exit: $LASTEXITCODE" | Tee-Object -Append $log

if ($LASTEXITCODE -ne 0) {
    "  FAILED. Aborting." | Tee-Object -Append $log
    return
}

# Step 6: Create device node
"Step 5: Creating device node..." | Tee-Object -Append $log

Add-Type -TypeDefinition @"
using System; using System.Text; using System.Runtime.InteropServices;
public class DN {
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

$r = [DN]::Go("$build\hidmaestro.inf")
"  Result: $r" | Tee-Object -Append $log

# Step 7: Verify
Start-Sleep -Seconds 3
"Step 6: Verifying..." | Tee-Object -Append $log
$enum = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
if ($enum -match "HIDMaestro") {
    "  SUCCESS: HIDMaestro device present!" | Tee-Object -Append $log
} else {
    "  Not found in HIDClass." | Tee-Object -Append $log
}

"" | Tee-Object -Append $log
"Done. Log: $log" | Tee-Object -Append $log
