$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$inf2cat = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\inf2cat.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$log = "$build\catalog_log.txt"

"=== Catalog + Install ===" | Out-File $log

# Step 1: Create catalog from INF
"Creating catalog..." | Tee-Object -Append $log
& $inf2cat /driver:"$build" /os:10_X64 /verbose *>> $log 2>&1

if (-not (Test-Path "$build\hidmaestro.cat")) {
    "inf2cat failed. Trying stampinf first..." | Tee-Object -Append $log

    # inf2cat may fail if DriverVer date is in the future or wrong format.
    # Try creating a minimal catalog manually via makecat
    # Alternative: just sign the INF directly using signtool (embedded signature)
    "Signing INF directly instead..." | Tee-Object -Append $log
    & $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.inf" *>> $log 2>&1
}

# Step 2: Sign catalog if it exists
if (Test-Path "$build\hidmaestro.cat") {
    "Signing catalog..." | Tee-Object -Append $log
    & $signtool sign /a /s PrivateCertStore /n HIDMaestroTestCert /fd SHA256 "$build\hidmaestro.cat" *>> $log 2>&1
    "Catalog signed." | Tee-Object -Append $log
}

# Step 3: Try pnputil again
"Adding driver to store..." | Tee-Object -Append $log
pnputil /add-driver "$build\hidmaestro.inf" /install *>> $log 2>&1
"pnputil exit: $LASTEXITCODE" | Tee-Object -Append $log

# Step 4: Create device node if pnputil succeeded
if ($LASTEXITCODE -eq 0) {
    "Creating device node via SetupAPI..." | Tee-Object -Append $log

    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class DevNode2 {
    static readonly Guid HID_GUID = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr h);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfoW(IntPtr s, string n, ref Guid g, string d, IntPtr h, int f, ref SP_DEVINFO_DATA p);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr s, ref SP_DEVINFO_DATA d, int prop, byte[] buf, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int f, IntPtr s, ref SP_DEVINFO_DATA d);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr h, string hwid, string inf, int flags, out bool reboot);

    public static string Create(string infPath) {
        Guid g = HID_GUID;
        string hwid = "root\\HIDMaestro";
        byte[] hwidBytes = Encoding.Unicode.GetBytes(hwid + "\0\0");

        IntPtr dis = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (dis == new IntPtr(-1))
            return "SetupDiCreateDeviceInfoList failed: " + Marshal.GetLastWin32Error();

        try {
            SP_DEVINFO_DATA did = new SP_DEVINFO_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

            if (!SetupDiCreateDeviceInfoW(dis, "HIDClass", ref g, "HIDMaestro Virtual HID Device", IntPtr.Zero, 1, ref did))
                return "SetupDiCreateDeviceInfoW failed: 0x" + Marshal.GetLastWin32Error().ToString("X");
            if (!SetupDiSetDeviceRegistryPropertyW(dis, ref did, 1, hwidBytes, hwidBytes.Length))
                return "SetupDiSetDeviceRegistryPropertyW failed: 0x" + Marshal.GetLastWin32Error().ToString("X");
            if (!SetupDiCallClassInstaller(0x19, dis, ref did))
                return "SetupDiCallClassInstaller failed: 0x" + Marshal.GetLastWin32Error().ToString("X");

            bool reboot;
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, infPath, 0, out reboot))
                return "UpdateDriverForPlugAndPlayDevices failed: 0x" + Marshal.GetLastWin32Error().ToString("X");

            return "OK" + (reboot ? " (reboot needed)" : "");
        } finally {
            SetupDiDestroyDeviceInfoList(dis);
        }
    }
}
"@

    $result = [DevNode2]::Create("$build\hidmaestro.inf")
    "  Device creation: $result" | Tee-Object -Append $log
}

# Step 5: Verify
Start-Sleep -Seconds 3
"Verifying..." | Tee-Object -Append $log
pnputil /enum-devices /class HIDClass 2>&1 | Select-String -Pattern "HIDMaestro" -Context 0,5 *>> $log 2>&1

$found = pnputil /enum-devices /class HIDClass 2>&1 | Select-String "HIDMaestro"
if ($found) {
    "SUCCESS: HIDMaestro device found!" | Tee-Object -Append $log
} else {
    "Device not found in HIDClass. Checking all devices..." | Tee-Object -Append $log
    pnputil /enum-devices 2>&1 | Select-String -Pattern "HIDMaestro" -Context 0,5 *>> $log 2>&1
}

"" | Tee-Object -Append $log
"Done. Log: $log" | Tee-Object -Append $log
