// Windows calls the coexistence probe reads the machine with. None of them
// goes through the SDK: the probe judges what two programs did from
// outside both.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

internal static class Native
{
    internal static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    internal static readonly IntPtr INVALID = new(-1);
    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    internal const uint DN_STARTED = 0x00000008, DN_HAS_PROBLEM = 0x00000400;
    const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    // ── HID ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [DllImport("hid.dll")] internal static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] internal static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort collection,
        [Out] ushort[] usages, ref uint usageLength, IntPtr pp, byte[] report, uint reportLength);
    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort collection,
        ushort usage, out uint value, IntPtr pp, byte[] report, uint reportLength);
    internal const int HIDP_STATUS_SUCCESS = 0x00110000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(IntPtr h, IntPtr overlapped);

    internal static (ushort Vid, ushort Pid)? HidAttributes(string path)
    {
        IntPtr h = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) return null;
        try
        {
            var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            return HidD_GetAttributes(h, ref a) ? (a.VendorID, a.ProductID) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Present HID interfaces of this VID and PID.</summary>
    internal static List<string> HidPaths(ushort vid, ushort pid) =>
        InterfaceList(HidGuid, null).Where(p => HidAttributes(p) is { } a && a.Vid == vid && a.Pid == pid).ToList();

    // ── Configuration manager ──────────────────────────────────────────

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);
    [DllImport("CfgMgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("CfgMgr32.dll")] internal static extern uint CM_Get_Child(out uint child, uint devInst, uint flags);
    [DllImport("CfgMgr32.dll")] internal static extern uint CM_Get_Sibling(out uint sibling, uint devInst, uint flags);
    [DllImport("CfgMgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, uint len, uint flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? deviceId, uint flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_ListW(ref Guid g, string? deviceId, char[] buffer, uint len, uint flags);
    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);
    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    static DEVPROPKEY Key(string g, uint pid) => new() { fmtid = new Guid(g), pid = pid };
    static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = Key("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion = Key("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 3);

    internal static IEnumerable<string> InterfaceList(Guid guid, string? deviceId)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, deviceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || len <= 1)
                return Array.Empty<string>();
            var buf = new char[len];
            uint cr = CM_Get_Device_Interface_ListW(ref guid, deviceId, buf, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr == 0x1A) continue;   // CR_BUFFER_SMALL: the list grew between the two calls
            if (cr != 0) return Array.Empty<string>();
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return Array.Empty<string>();
    }

    /// <summary>The devnode an interface path belongs to.</summary>
    internal static string? InstanceOfInterface(string path)
    {
        var key = DEVPKEY_Device_InstanceId;
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, buf, ref size, 0) != 0) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    internal static string DeviceIdOf(uint devInst)
    {
        var buf = new char[512];
        if (CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) != 0) return "";
        int z = Array.IndexOf(buf, '\0');
        return new string(buf, 0, z < 0 ? buf.Length : z);
    }

    internal static bool IsPresent(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, 0) == 0;

    /// <summary>Neither live nor phantom: what DeviceManager.RemoveDevice ends with.</summary>
    internal static bool IsGone(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, 0) != 0 && CM_Locate_DevNodeW(out _, instanceId, 1) != 0;

    internal static bool IsStarted(string instanceId) =>
        CM_Locate_DevNodeW(out uint di, instanceId, 0) == 0
        && CM_Get_DevNode_Status(out uint st, out _, di, 0) == 0 && (st & DN_STARTED) != 0;

    /// <summary>The instance and its whole subtree, live nodes only.</summary>
    internal static List<string> Subtree(string instanceId)
    {
        var list = new List<string>();
        if (CM_Locate_DevNodeW(out uint di, instanceId, 0) != 0) return list;
        list.Add(DeviceIdOf(di));
        void Walk(uint node)
        {
            if (CM_Get_Child(out uint child, node, 0) != 0) return;
            uint cur = child;
            do { list.Add(DeviceIdOf(cur)); Walk(cur); } while (CM_Get_Sibling(out cur, cur, 0) == 0);
        }
        Walk(di);
        return list;
    }

    /// <summary>The parent of a devnode, or null at the root.</summary>
    internal static string? ParentOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint di, instanceId, 0) != 0) return null;
        return CM_Get_Parent(out uint parent, di, 0) == 0 ? DeviceIdOf(parent) : null;
    }

    internal static string? DriverVersionOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint di, instanceId, 0) != 0) return null;
        var key = DEVPKEY_Device_DriverVersion;
        uint size = 0;
        CM_Get_DevNode_PropertyW(di, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(di, ref key, out _, buf, ref size, 0) != 0) return null;
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    // ── Named pipes ────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WaitNamedPipeW(string name, uint timeout);
    internal const int ERROR_FILE_NOT_FOUND = 2, ERROR_SEM_TIMEOUT = 121;

    /// <summary>Whether a pipe name has a live server instance, without
    /// connecting to it. A timeout of zero means the server's default wait,
    /// so the probe waits one millisecond.</summary>
    internal static bool PipeExists(string name)
    {
        bool ok = WaitNamedPipeW(@"\\.\pipe\" + name, 1);
        if (ok) return true;
        return Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;
    }

    // ── Raw Input ──────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, char[]? data, ref uint size);
    const uint RIDI_DEVICENAME = 0x20000007;

    /// <summary>Raw Input's device handles with their names.</summary>
    internal static List<(IntPtr Handle, string Name)> RawInputDevices()
    {
        var result = new List<(IntPtr, string)>();
        uint count = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return result;
        var list = new RAWINPUTDEVICELIST[count + 8];
        count = (uint)list.Length;
        uint n = GetRawInputDeviceList(list, ref count, size);
        if (n == uint.MaxValue) return result;
        for (int i = 0; i < n; i++)
        {
            uint chars = 0;
            GetRawInputDeviceInfoW(list[i].hDevice, RIDI_DEVICENAME, null, ref chars);
            if (chars == 0) continue;
            var name = new char[chars];
            if (GetRawInputDeviceInfoW(list[i].hDevice, RIDI_DEVICENAME, name, ref chars) == uint.MaxValue) continue;
            result.Add((list[i].hDevice, new string(name).TrimEnd('\0')));
        }
        return result;
    }

    // ── XInput ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [DllImport("xinput1_4.dll")] internal static extern uint XInputGetState(uint idx, out XINPUT_STATE st);

    // ── Registry ───────────────────────────────────────────────────────

    /// <summary>Owner records of programs running the SDK under test,
    /// keyed by owner id, with the process id each one names.</summary>
    internal static Dictionary<string, int> OwnerRecords()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (k == null) return d;
        foreach (var id in k.GetSubKeyNames())
        {
            using var r = k.OpenSubKey(id);
            if (r?.GetValue("Pid") is int pid) d[id] = pid;
        }
        return d;
    }

    internal static bool LedgerHolds(string kind, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestroRegistryLedger\{kind}\{name}");
        return k != null;
    }

    internal static List<string> LedgerHolders(string kind, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestroRegistryLedger\{kind}\{name}\Holders");
        return k?.GetValueNames().ToList() ?? new List<string>();
    }
}
