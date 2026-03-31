$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\xinput_detailed.log"
"" | Out-File -Encoding ASCII $log
function Log($msg) { Write-Host $msg; $msg | Out-File -Append -Encoding ASCII $log }

# Enumerate XUSB device interfaces
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class XUSBEnum {
    static readonly Guid XUSB_GUID = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    [DllImport("setupapi.dll", SetLastError=true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll", SetLastError=true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [StructLayout(LayoutKind.Sequential)] public struct SP_DID { public int cbSize; public Guid g; public int f; public IntPtr r; }
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DID did);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DID did, IntPtr det, uint sz, out uint req, IntPtr d);

    public static string Enumerate() {
        var sb = new StringBuilder();
        Guid g = XUSB_GUID;
        IntPtr ds = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (ds == new IntPtr(-1)) return "SetupDi failed: " + Marshal.GetLastWin32Error();
        int count = 0;
        for (uint idx = 0; idx < 64; idx++) {
            var did = new SP_DID { cbSize = Marshal.SizeOf(typeof(SP_DID)) };
            if (!SetupDiEnumDeviceInterfaces(ds, IntPtr.Zero, ref g, idx, ref did)) break;
            uint req;
            SetupDiGetDeviceInterfaceDetailW(ds, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
            IntPtr det = Marshal.AllocHGlobal((int)req);
            Marshal.WriteInt32(det, 8);
            if (SetupDiGetDeviceInterfaceDetailW(ds, ref did, det, req, out req, IntPtr.Zero)) {
                string path = Marshal.PtrToStringUni(det + 4);
                sb.AppendLine("  XUSB Interface " + idx + ": " + path);
                count++;
            }
            Marshal.FreeHGlobal(det);
        }
        SetupDiDestroyDeviceInfoList(ds);
        sb.Insert(0, "XUSB interfaces found: " + count + "\n");
        return sb.ToString();
    }
}
"@

Log "=== XUSB Device Interface Enumeration ==="
Log ([XUSBEnum]::Enumerate())

# XInput state
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class XI2 {
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLT; public byte bRT; public short sLX; public short sLY; public short sRX; public short sRY; }
    [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);
}
"@

Log "`n=== XInput State ==="
for ($i = 0; $i -lt 4; $i++) {
    $state = New-Object XI2+XINPUT_STATE
    $r = [XI2]::XInputGetState($i, [ref]$state)
    if ($r -eq 0) {
        Log "  Slot $i : CONNECTED pkt=$($state.dwPacketNumber) LX=$($state.sLX) LY=$($state.sLY) Btns=0x$($state.wButtons.ToString('X4'))"
    } else {
        Log "  Slot $i : err=$r"
    }
}
