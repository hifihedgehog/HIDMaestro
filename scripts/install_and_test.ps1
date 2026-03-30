$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$cer = "$build\HIDMaestroTestCert.cer"
$inf = "$build\hidmaestro.inf"
$log = "$build\install_log.txt"

"=== HIDMaestro Install Log ===" | Out-File $log

# Step 1: Add cert to Trusted Root CAs (so UMDF2 driver loads without test signing)
"Step 1: Adding cert to Trusted Root CAs..." | Tee-Object -Append $log
certutil -addstore Root $cer *>> $log 2>&1
certutil -addstore TrustedPublisher $cer *>> $log 2>&1
"  Done." | Tee-Object -Append $log

# Step 2: Add driver to driver store
"Step 2: Adding driver to store..." | Tee-Object -Append $log
pnputil /add-driver $inf /install *>> $log 2>&1
"  pnputil exit code: $LASTEXITCODE" | Tee-Object -Append $log

# Step 3: Create device node
"Step 3: Creating device node (root\HIDMaestro)..." | Tee-Object -Append $log

# Use pnputil /add-device if available (Win11 22H2+), otherwise devcon
$pnpVer = (pnputil /?) 2>&1 | Select-String "add-device"
if ($pnpVer) {
    pnputil /add-device /hardwareid "root\HIDMaestro" /driver $inf *>> $log 2>&1
    "  pnputil /add-device exit code: $LASTEXITCODE" | Tee-Object -Append $log
} else {
    # Try devcon
    $devcon = Get-Command devcon.exe -ErrorAction SilentlyContinue
    if ($devcon) {
        & devcon.exe install $inf "root\HIDMaestro" *>> $log 2>&1
        "  devcon exit code: $LASTEXITCODE" | Tee-Object -Append $log
    } else {
        # SetupAPI fallback
        "  No pnputil /add-device or devcon. Using SetupAPI..." | Tee-Object -Append $log

        Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public class DevNode {
    static readonly Guid HID_GUID = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    const int DICD_GENERATE_ID = 0x01;
    const int DIF_REGISTERDEVICE = 0x19;
    const int SPDRP_HARDWAREID = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

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

    public static bool Create(string infPath) {
        Guid g = HID_GUID;
        string hwid = "root\\HIDMaestro";
        byte[] hwidBytes = Encoding.Unicode.GetBytes(hwid + "\0\0");

        IntPtr dis = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (dis == new IntPtr(-1)) return false;

        try {
            SP_DEVINFO_DATA did = new SP_DEVINFO_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

            if (!SetupDiCreateDeviceInfoW(dis, "HIDClass", ref g, "HIDMaestro Virtual HID Device", IntPtr.Zero, DICD_GENERATE_ID, ref did))
                return false;
            if (!SetupDiSetDeviceRegistryPropertyW(dis, ref did, SPDRP_HARDWAREID, hwidBytes, hwidBytes.Length))
                return false;
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, dis, ref did))
                return false;

            bool reboot;
            UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, infPath, 0, out reboot);
            return true;
        } finally {
            SetupDiDestroyDeviceInfoList(dis);
        }
    }
}
"@

        $result = [DevNode]::Create($inf)
        "  SetupAPI device creation: $result" | Tee-Object -Append $log
    }
}

# Step 4: Verify
"Step 4: Verifying..." | Tee-Object -Append $log
Start-Sleep -Seconds 3

# Check for HIDMaestro device
$devices = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
if ($devices -match "HIDMaestro") {
    "  SUCCESS: HIDMaestro device found in HIDClass!" | Tee-Object -Append $log
} else {
    "  WARNING: HIDMaestro device not found yet. Check Device Manager." | Tee-Object -Append $log
}

# Check for device interface
$interfaces = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -like "*HIDMaestro*" -or $_.InstanceId -like "*HIDMaestro*" }
if ($interfaces) {
    foreach ($dev in $interfaces) {
        "  Device: $($dev.InstanceId) Status: $($dev.Status)" | Tee-Object -Append $log
    }
} else {
    "  No PnP device with HIDMaestro name found." | Tee-Object -Append $log
}

""  | Tee-Object -Append $log
"Install complete. Log: $log" | Tee-Object -Append $log
