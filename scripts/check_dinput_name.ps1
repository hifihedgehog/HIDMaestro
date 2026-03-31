Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public class DICheck {
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError=true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string fn, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public uint Size; public ushort VID; public ushort PID; public ushort Ver; }
    [DllImport("setupapi.dll", SetLastError=true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll", SetLastError=true)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DID did);
    [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DID did, IntPtr det, uint sz, out uint req, IntPtr d);
    [StructLayout(LayoutKind.Sequential)] public struct SP_DID { public int cbSize; public Guid g; public int f; public IntPtr r; }
    public static string Check() {
        var sb = new StringBuilder();
        Guid hg; HidD_GetHidGuid(out hg);
        IntPtr ds = SetupDiGetClassDevsW(ref hg, IntPtr.Zero, IntPtr.Zero, 0x12);
        for (uint idx = 0; idx < 128; idx++) {
            var did = new SP_DID { cbSize = Marshal.SizeOf(typeof(SP_DID)) };
            if (!SetupDiEnumDeviceInterfaces(ds, IntPtr.Zero, ref hg, idx, ref did)) break;
            uint req; SetupDiGetDeviceInterfaceDetailW(ds, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
            IntPtr det = Marshal.AllocHGlobal((int)req);
            Marshal.WriteInt32(det, 8);
            if (!SetupDiGetDeviceInterfaceDetailW(ds, ref did, det, req, out req, IntPtr.Zero)) { Marshal.FreeHGlobal(det); continue; }
            string path = Marshal.PtrToStringUni(det + 4);
            Marshal.FreeHGlobal(det);
            // Look for game controllers
            if (!path.Contains("col01") && !path.Contains("hidclass")) continue;
            var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var a = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
            if (HidD_GetAttributes(h, ref a)) {
                byte[] prodBuf = new byte[512];
                bool got = HidD_GetProductString(h, prodBuf, 512);
                string prod = got ? Encoding.Unicode.GetString(prodBuf).TrimEnd(new char[]{'\0'}) : "(n/a)";
                if (a.VID == 0x045E) // Microsoft devices
                    sb.AppendLine(String.Format("  VID={0:X4} PID={1:X4} Product=\"{2}\" Path={3}", a.VID, a.PID, prod, path));
            }
            h.Dispose();
        }
        SetupDiDestroyDeviceInfoList(ds);
        return sb.Length > 0 ? sb.ToString() : "  No Microsoft HID devices found";
    }
}
"@

Write-Host "=== HID Product Strings (Microsoft devices) ==="
Write-Host ([DICheck]::Check())
