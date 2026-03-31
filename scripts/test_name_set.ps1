$log = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\name_set.log"
$exe = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\test\HIDMaestroTest\publish_out\HIDMaestroTest.exe"

# Just set the name on the existing device - don't recreate
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class NameSet {
    [DllImport("CfgMgr32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError=true)]
    public static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [DllImport("CfgMgr32.dll", SetLastError=true)]
    public static extern uint CM_Set_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY key, uint type, byte[] buf, uint sz, uint flags);
    [DllImport("CfgMgr32.dll", SetLastError=true)]
    public static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY key, out uint type, byte[] buf, ref uint sz, uint flags);

    public static string SetName(string instanceId, string name) {
        var sb = new StringBuilder();
        // DEVPKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
        var fnKey = new DEVPROPKEY {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 14
        };
        // DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
        var bdKey = new DEVPROPKEY {
            fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
            pid = 4
        };
        byte[] strBytes = Encoding.Unicode.GetBytes(name + "\0");

        uint locRes = CM_Locate_DevNodeW(out uint devInst, instanceId, 0);
        sb.AppendLine("Locate root: " + locRes + " inst=" + devInst);
        if (locRes != 0) return sb.ToString();

        uint r1 = CM_Set_DevNode_PropertyW(devInst, ref fnKey, 0x12, strBytes, (uint)strBytes.Length, 0);
        sb.AppendLine("Set FriendlyName on root: " + r1);
        uint r2 = CM_Set_DevNode_PropertyW(devInst, ref bdKey, 0x12, strBytes, (uint)strBytes.Length, 0);
        sb.AppendLine("Set BusReportedDeviceDesc on root: " + r2);

        uint childRes = CM_Get_Child(out uint childInst, devInst, 0);
        sb.AppendLine("Get child: " + childRes + " inst=" + childInst);
        if (childRes == 0) {
            uint r3 = CM_Set_DevNode_PropertyW(childInst, ref fnKey, 0x12, strBytes, (uint)strBytes.Length, 0);
            sb.AppendLine("Set FriendlyName on child: " + r3);
            uint r4 = CM_Set_DevNode_PropertyW(childInst, ref bdKey, 0x12, strBytes, (uint)strBytes.Length, 0);
            sb.AppendLine("Set BusReportedDeviceDesc on child: " + r4);
        }

        // Read back to verify
        byte[] readBuf = new byte[512]; uint readSz = 512; uint readType;
        CM_Get_DevNode_PropertyW(devInst, ref fnKey, out readType, readBuf, ref readSz, 0);
        string readVal = Encoding.Unicode.GetString(readBuf, 0, (int)readSz).TrimEnd('\0');
        sb.AppendLine("Read back root FriendlyName: '" + readVal + "'");

        return sb.ToString();
    }
}
"@

$result = [NameSet]::SetName("ROOT\HIDCLASS\0000", "Controller")
$result | Out-File -Encoding ASCII $log
Write-Host $result
