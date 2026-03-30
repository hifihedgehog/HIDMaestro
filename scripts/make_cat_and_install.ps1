$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$makecat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makecat.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$log = "$build\install_log2.txt"

"=== HIDMaestro Catalog + Install ===" | Out-File $log

# Step 1: Create catalog
"Creating catalog via makecat..." | Tee-Object -Append $log
Push-Location $build
& $makecat "hidmaestro.cdf" *>> $log 2>&1
Pop-Location

if (Test-Path "$build\hidmaestro.cat") {
    "  Catalog created." | Tee-Object -Append $log
} else {
    "  ERROR: Catalog not created!" | Tee-Object -Append $log
    Get-Content $log
    return
}

# Step 2: Sign the catalog
"Signing catalog..." | Tee-Object -Append $log
& $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" *>> $log 2>&1
"  signtool exit: $LASTEXITCODE" | Tee-Object -Append $log

# Step 3: Verify catalog signature
$sig = Get-AuthenticodeSignature "$build\hidmaestro.cat"
"  Catalog signature: $($sig.Status)" | Tee-Object -Append $log

# Step 4: Add to driver store
"Adding to driver store..." | Tee-Object -Append $log
pnputil /add-driver "$build\hidmaestro.inf" /install *>> $log 2>&1
"  pnputil exit: $LASTEXITCODE" | Tee-Object -Append $log

if ($LASTEXITCODE -ne 0) {
    "  Driver store add failed. Check log for details." | Tee-Object -Append $log
    Get-Content $log
    return
}

# Step 5: Create device node
"Creating device node..." | Tee-Object -Append $log

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class DevNodeCreate {
    static readonly Guid HID_GUID = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA {
        public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError=true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr h);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool SetupDiCreateDeviceInfoW(IntPtr s, string n, ref Guid g, string d, IntPtr h, int f, ref SP_DEVINFO_DATA p);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s, ref SP_DEVINFO_DATA d, int prop, byte[] buf, int size);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiCallClassInstaller(int f, IntPtr s, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("newdev.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h, string hwid, string inf, int flags, out bool reboot);

    public static string Create(string infPath) {
        Guid g = HID_GUID; string hwid = "root\\HIDMaestro";
        byte[] hwidBytes = Encoding.Unicode.GetBytes(hwid + "\0\0");
        IntPtr dis = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (dis == new IntPtr(-1)) return "CreateDeviceInfoList failed: " + Marshal.GetLastWin32Error();
        try {
            var did = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
            if (!SetupDiCreateDeviceInfoW(dis, "HIDClass", ref g, "HIDMaestro Virtual HID Device", IntPtr.Zero, 1, ref did))
                return "CreateDeviceInfo failed: 0x" + Marshal.GetLastWin32Error().ToString("X");
            if (!SetupDiSetDeviceRegistryPropertyW(dis, ref did, 1, hwidBytes, hwidBytes.Length))
                return "SetRegistryProperty failed: 0x" + Marshal.GetLastWin32Error().ToString("X");
            if (!SetupDiCallClassInstaller(0x19, dis, ref did))
                return "CallClassInstaller failed: 0x" + Marshal.GetLastWin32Error().ToString("X");
            bool reboot;
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, infPath, 0, out reboot))
                return "UpdateDriver failed: 0x" + Marshal.GetLastWin32Error().ToString("X");
            return "OK" + (reboot ? " (reboot needed)" : "");
        } finally { SetupDiDestroyDeviceInfoList(dis); }
    }
}
"@

$result = [DevNodeCreate]::Create("$build\hidmaestro.inf")
"  Result: $result" | Tee-Object -Append $log

# Step 6: Verify
Start-Sleep -Seconds 3
"Verifying..." | Tee-Object -Append $log

$found = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
if ($found -match "HIDMaestro") {
    "SUCCESS: HIDMaestro device found!" | Tee-Object -Append $log
    $found -split "`n" | Where-Object { $_ -match "HIDMaestro" -or ($prev -and $_ -match "Status|Driver") } | ForEach-Object { "  $_" } *>> $log
} else {
    "Device not in HIDClass yet." | Tee-Object -Append $log
}

"" | Tee-Object -Append $log
"Done. Log: $log" | Tee-Object -Append $log
