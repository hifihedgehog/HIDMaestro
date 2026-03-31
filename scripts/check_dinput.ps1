$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\dinput.log"
"" | Out-File -Encoding ASCII $logFile

function Log($msg) {
    Write-Host $msg
    $msg | Out-File -Append -Encoding ASCII $logFile
}

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public class JoyFull {
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr ppd);
    [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_FreePreparsedData(IntPtr ppd);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr ppd, ref HIDP_CAPS caps);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string fn, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public uint Size; public ushort VID; public ushort PID; public ushort Ver; }
    [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS {
        public ushort Usage, UsagePage; public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
    [DllImport("setupapi.dll", SetLastError=true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll", SetLastError=true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DID did);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DID did, IntPtr det, uint sz, out uint req, IntPtr d);
    [StructLayout(LayoutKind.Sequential)] public struct SP_DID { public int cbSize; public Guid g; public int f; public IntPtr r; }

    public static string Scan() {
        var sb = new StringBuilder();
        Guid hg; HidD_GetHidGuid(out hg);
        IntPtr ds = SetupDiGetClassDevsW(ref hg, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (ds == new IntPtr(-1)) return "SetupDi failed";
        for (uint idx = 0; idx < 128; idx++) {
            var did = new SP_DID { cbSize = Marshal.SizeOf(typeof(SP_DID)) };
            if (!SetupDiEnumDeviceInterfaces(ds, IntPtr.Zero, ref hg, idx, ref did)) break;
            uint req; SetupDiGetDeviceInterfaceDetailW(ds, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
            IntPtr det = Marshal.AllocHGlobal((int)req);
            Marshal.WriteInt32(det, 8);
            if (!SetupDiGetDeviceInterfaceDetailW(ds, ref did, det, req, out req, IntPtr.Zero)) { Marshal.FreeHGlobal(det); continue; }
            string path = Marshal.PtrToStringUni(det + 4);
            Marshal.FreeHGlobal(det);
            if (!path.ToLower().Contains("hidclass")) continue;

            var h = CreateFileW(path, 0x80000000|0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;

            var a = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
            HidD_GetAttributes(h, ref a);
            byte[] prodBuf = new byte[512];
            bool gotProd = HidD_GetProductString(h, prodBuf, 512);
            string prod = gotProd ? Encoding.Unicode.GetString(prodBuf).TrimEnd(new char[]{'\0'}) : "(failed)";

            IntPtr ppd;
            string capsStr = "";
            if (HidD_GetPreparsedData(h, out ppd)) {
                var caps = new HIDP_CAPS();
                HidP_GetCaps(ppd, ref caps);
                capsStr = string.Format(" UsagePage=0x{0:X4} Usage=0x{1:X4} InSize={2} OutSize={3} FeatSize={4} Btns={5} Vals={6}",
                    caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength,
                    caps.FeatureReportByteLength, caps.NumberInputButtonCaps, caps.NumberInputValueCaps);
                HidD_FreePreparsedData(ppd);
            }

            sb.AppendLine(string.Format("VID={0:X4} PID={1:X4} \"{2}\"{3}", a.VID, a.PID, prod, capsStr));
            h.Dispose();
        }
        SetupDiDestroyDeviceInfoList(ds);
        return sb.ToString();
    }
}
"@

Log "=== HID Gamepad Devices ==="
$result = [JoyFull]::Scan()
Log $result

# Also try WinMM with joyGetPosEx to see if any slot is active
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class JoyPos {
    [StructLayout(LayoutKind.Sequential)] public struct JOYINFOEX {
        public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2;
    }
    [DllImport("winmm.dll")] public static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);
    public static string CheckAll() {
        var sb = new System.Text.StringBuilder();
        for (uint i = 0; i < 16; i++) {
            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf(typeof(JOYINFOEX)), dwFlags = 0x7F };
            uint r = joyGetPosEx(i, ref info);
            if (r == 0) sb.AppendLine(string.Format("  Slot {0}: X={1} Y={2} Btns=0x{3:X} Hat={4}", i, info.dwXpos, info.dwYpos, info.dwButtons, info.dwPOV));
        }
        if (sb.Length == 0) sb.AppendLine("  No joysticks detected by WinMM");
        return sb.ToString();
    }
}
"@

Log "`n=== WinMM Joystick Slots ==="
Log ([JoyPos]::CheckAll())

Start-Sleep 3
