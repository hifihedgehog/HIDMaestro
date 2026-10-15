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

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr pp);
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

    // ── The version handshake ──────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);

    /// <summary>Global\HIDMaestroVersion&lt;N&gt; as HIDMAESTRO_VERSION_INFO
    /// (driver/driver.h) lays it out, or null when the section does not
    /// exist.</summary>
    internal static (int Layout, int SdkMin, int SdkMax, string Sdk, int DriverProtocol, string Driver)? VersionSection(int transport)
    {
        IntPtr h = OpenFileMappingW(0x0004 /*FILE_MAP_READ*/, false, $@"Global\HIDMaestroVersion{transport}");
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr v = MapViewOfFile(h, 0x0004, 0, 0, (UIntPtr)64);
            if (v == IntPtr.Zero) return null;
            try
            {
                string Ver(int at) => $"{(ushort)Marshal.ReadInt16(v, at)}.{(ushort)Marshal.ReadInt16(v, at + 2)}.{(ushort)Marshal.ReadInt16(v, at + 4)}";
                return (Marshal.ReadInt32(v, 0), Marshal.ReadInt32(v, 4), Marshal.ReadInt32(v, 8), Ver(12),
                        Marshal.ReadInt32(v, 20), Ver(24));
            }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The first <paramref name="size"/> bytes of a named section,
    /// or null when it does not exist.</summary>
    internal static byte[]? SectionBytes(string name, int size)
    {
        IntPtr h = OpenFileMappingW(0x0004 /*FILE_MAP_READ*/, false, name);
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr v = MapViewOfFile(h, 0x0004, 0, 0, (UIntPtr)size);
            if (v == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(v, bytes, 0, size);
                return bytes;
            }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Whether any process holds a handle to the named section.
    /// A section exists while one handle to it is open.</summary>
    internal static bool SectionExists(string name)
    {
        IntPtr h = OpenFileMappingW(0x0004 /*FILE_MAP_READ*/, false, name);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

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

    /// <summary>A record the way a program on this SDK writes it, with its
    /// staging and its controllers' entries. It is live while someone holds
    /// its owner claim pipe.</summary>
    internal static void WriteRecord(string ownerId, string? stagingTag, params (string Token, int Index)[] controllers)
    {
        using var r = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\HIDMaestroOwners\{ownerId}");
        r.SetValue("Pid", 0, RegistryValueKind.DWord);
        r.SetValue("Image", "coexist_check, a running program's record", RegistryValueKind.String);
        r.SetValue("StartTime", 0L, RegistryValueKind.QWord);
        r.SetValue("SdkVersion", "0.0.0", RegistryValueKind.String);
        if (stagingTag != null) r.SetValue("StagingTag", stagingTag, RegistryValueKind.String);
        foreach (var (token, index) in controllers)
        {
            using var c = r.CreateSubKey($@"Controllers\{token}");
            c.SetValue("TransportIndex", index, RegistryValueKind.DWord);
            c.SetValue("Backend", "umdf", RegistryValueKind.String);
            c.SetValue("Profile", "coexist-live", RegistryValueKind.String);
        }
    }

    /// <summary>A value of the record a process wrote, or null.</summary>
    internal static string? RecordValue(int pid, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (k == null) return null;
        foreach (var id in k.GetSubKeyNames())
        {
            using var r = k.OpenSubKey(id);
            if (r?.GetValue("Pid") is int p && p == pid) return r.GetValue(name)?.ToString();
        }
        return null;
    }

    /// <summary>Forget which payload is installed, so the next InstallDriver
    /// runs the deploy policy instead of its fast path.</summary>
    internal static void DropInstalledManifest()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro", writable: true);
        k?.DeleteValue("InstalledManifestSha256", throwOnMissingValue: false);
    }

    /// <summary>A record the way a killed program on this SDK leaves it:
    /// no ownership claim, and one controller entry.</summary>
    internal static void WriteDeadRecord(string ownerId, string token, int transportIndex)
    {
        using var r = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\HIDMaestroOwners\{ownerId}");
        r.SetValue("Pid", 0, RegistryValueKind.DWord);
        r.SetValue("Image", "coexist_check, a killed program's record", RegistryValueKind.String);
        r.SetValue("StartTime", 0L, RegistryValueKind.QWord);
        r.SetValue("SdkVersion", "1.13.0", RegistryValueKind.String);
        using var c = r.CreateSubKey($@"Controllers\{token}");
        c.SetValue("TransportIndex", transportIndex, RegistryValueKind.DWord);
        c.SetValue("Backend", "umdf", RegistryValueKind.String);
        c.SetValue("Profile", "coexist-dead", RegistryValueKind.String);
    }

    internal static void DeleteRecord(string ownerId) =>
        Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\HIDMaestroOwners\{ownerId}", throwOnMissingSubKey: false);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outSize, uint inSize, uint timeout, IntPtr sa);

    /// <summary>The pipe prefix every claim lives under, where only an
    /// elevated process can create a pipe.</summary>
    internal const string ClaimPrefix = @"ProtectedPrefix\Administrators\";

    /// <summary>A pipe the way a running program's claim holds its name:
    /// the first and only instance, under the claim prefix. Null when the
    /// name is taken.</summary>
    internal static Microsoft.Win32.SafeHandles.SafeFileHandle? HoldPipe(string name)
    {
        var h = CreateNamedPipeW(@"\\.\pipe\" + ClaimPrefix + name, 0x00000001 | 0x00080000, 0x00000008, 1, 0, 0, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>The first instance of a pipe under any name, with the
    /// default security of the calling thread's token. Null and the Win32
    /// error when it cannot be created.</summary>
    internal static Microsoft.Win32.SafeHandles.SafeFileHandle? CreatePipe(string name, out int error)
    {
        var h = CreateNamedPipeW(@"\\.\pipe\" + name, 0x00000001 | 0x00080000, 0x00000008, 1, 0, 0, 0, IntPtr.Zero);
        error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>Whether a claim, named without its prefix, is held.</summary>
    internal static bool ClaimExists(string name) => PipeExists(ClaimPrefix + name);

    // ── The SDK's private namespace ────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateBoundaryDescriptorW(string name, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AddSIDToBoundaryDescriptor(ref IntPtr boundary, byte[] sid);
    [DllImport("kernel32.dll")] static extern void DeleteBoundaryDescriptor(IntPtr boundary);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreatePrivateNamespaceW(IntPtr sa, IntPtr boundary, string alias);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenPrivateNamespaceW(IntPtr boundary, string alias);

    // The SDK's boundary (AdminNamespace.cs): the name HIDMaestro and the
    // Administrators SID. The alias is this process's own.
    const string AdminBoundary = "HIDMaestro";
    const string ProbeAlias = "CoexistCheck.Admin";
    static IntPtr s_adminNamespace;

    static IntPtr AdminBoundaryDescriptor()
    {
        IntPtr b = CreateBoundaryDescriptorW(AdminBoundary, 0);
        AddSIDToBoundaryDescriptor(ref b, SidBytes(SidAdmins));
        return b;
    }

    /// <summary>A mutex in the namespace the SDK keeps its locks in, the
    /// way another program on this SDK takes it. The probe keeps its
    /// namespace handle for its whole run, as the SDK does.</summary>
    internal static System.Threading.Mutex AdminMutex(string name)
    {
        if (s_adminNamespace == IntPtr.Zero)
        {
            IntPtr b = AdminBoundaryDescriptor();
            // The SDK's descriptor for it: SYSTEM and Administrators only.
            IntPtr sa = Sa("D:P(A;;GA;;;SY)(A;;GA;;;BA)");
            try
            {
                for (int i = 0; i < 8 && s_adminNamespace == IntPtr.Zero; i++)
                {
                    s_adminNamespace = CreatePrivateNamespaceW(sa, b, ProbeAlias);
                    if (s_adminNamespace == IntPtr.Zero) s_adminNamespace = OpenPrivateNamespaceW(b, ProbeAlias);
                }
            }
            finally
            {
                DeleteBoundaryDescriptor(b);
                FreeSa(sa);
            }
            if (s_adminNamespace == IntPtr.Zero) throw new InvalidOperationException("the SDK's namespace did not open");
        }
        return new System.Threading.Mutex(false, ProbeAlias + @"\" + name);
    }

    /// <summary>What a program that is not elevated gets when it tries to
    /// create, then open, the SDK's namespace: the two Win32 errors, 0 for
    /// success. Run on a thread that impersonates such a program.</summary>
    internal static (int Create, int Open) TryAdminNamespace()
    {
        IntPtr b = AdminBoundaryDescriptor();
        try
        {
            IntPtr h = CreatePrivateNamespaceW(IntPtr.Zero, b, "CoexistCheck.Squat");
            int create = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (h != IntPtr.Zero) ClosePrivateNamespace(h, 0);
            IntPtr o = OpenPrivateNamespaceW(b, "CoexistCheck.Squat");
            int open = o == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (o != IntPtr.Zero) ClosePrivateNamespace(o, 0);
            return (create, open);
        }
        finally { DeleteBoundaryDescriptor(b); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ClosePrivateNamespace(IntPtr h, uint flags);

    // ── A program that is not elevated, on one thread ──────────────────

    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CreateRestrictedToken(IntPtr token, uint flags, uint disableCount, SID_AND_ATTRIBUTES[] disable,
        uint deleteCount, IntPtr delete, uint restrictCount, IntPtr restrict, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int level, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int cls, IntPtr info, uint length);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetThreadToken(IntPtr thread, IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool RevertToSelf();
    [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);
    internal const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    static byte[] SidBytes(System.Security.Principal.SecurityIdentifier sid)
    {
        var b = new byte[sid.BinaryLength];
        sid.GetBinaryForm(b, 0);
        return b;
    }

    /// <summary>An impersonation token that keeps this elevated process's
    /// rights, Administrators enabled, but has the user as its default
    /// owner. That is the effect of the "System objects: Default owner for
    /// objects created by members of the Administrators group = Object
    /// creator" policy (DISA STIG V-3384): an object the token creates is
    /// owned by the user, not Administrators, unless the SDDL names an owner.</summary>
    internal static IntPtr UserOwnerToken()
    {
        const uint TOKEN_DUPLICATE = 0x2, TOKEN_QUERY = 0x8, TOKEN_ADJUST_DEFAULT = 0x80, MAXIMUM_ALLOWED = 0x02000000;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT, out IntPtr own))
            throw new System.ComponentModel.Win32Exception();
        try
        {
            if (!DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, 2, 2, out IntPtr imp))
                throw new System.ComponentModel.Win32Exception();
            byte[] user = SidBytes(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
            var h = System.Runtime.InteropServices.GCHandle.Alloc(user, System.Runtime.InteropServices.GCHandleType.Pinned);
            IntPtr owner = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(owner, h.AddrOfPinnedObject());
                if (!SetTokenInformation(imp, 4 /* TokenOwner */, owner, (uint)IntPtr.Size))
                    throw new System.ComponentModel.Win32Exception();
            }
            finally { Marshal.FreeHGlobal(owner); h.Free(); }
            return imp;
        }
        finally { CloseHandle(own); }
    }

    /// <summary>Create a mutex with <paramref name="sddl"/> and return its
    /// owner's SID, then close it. For checking what owner a security
    /// descriptor produces under the calling thread's token.</summary>
    internal static string CreateAndReadOwner(string sddl, string name)
    {
        IntPtr sa = Sa(sddl);
        IntPtr h = CreateMutexW(sa, false, name);
        int err = Marshal.GetLastWin32Error();
        FreeSa(sa);
        if (h == IntPtr.Zero) return $"create err {err}";
        try
        {
            if (GetSecurityInfo(h, 6, 1, out IntPtr owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr osd) != 0) return "unreadable";
            try { return new System.Security.Principal.SecurityIdentifier(owner).Value; }
            finally { LocalFree(osd); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>An impersonation token for this user as a program that is
    /// not elevated runs: Administrators deny-only, medium integrity, the
    /// user as default owner, and only the privileges such a program has.
    /// Nothing runs under it but a thread of this process, which
    /// SetThreadToken gives it and RevertToSelf takes back.</summary>
    internal static IntPtr MediumToken()
    {
        const uint TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_DUPLICATE = 0x2, TOKEN_QUERY = 0x8, TOKEN_ADJUST_DEFAULT = 0x80, MAXIMUM_ALLOWED = 0x02000000;
        const uint DISABLE_MAX_PRIVILEGE = 0x1;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT, out IntPtr own))
            throw new System.ComponentModel.Win32Exception();
        var pins = new List<System.Runtime.InteropServices.GCHandle>();
        try
        {
            byte[] admins = SidBytes(SidAdmins);
            var ha = GCHandle.Alloc(admins, GCHandleType.Pinned); pins.Add(ha);
            var disable = new[] { new SID_AND_ATTRIBUTES { Sid = ha.AddrOfPinnedObject() } };
            if (!CreateRestrictedToken(own, DISABLE_MAX_PRIVILEGE, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out IntPtr restricted))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                if (!DuplicateTokenEx(restricted, MAXIMUM_ALLOWED, IntPtr.Zero, 2 /* SecurityImpersonation */, 2 /* TokenImpersonation */, out IntPtr imp))
                    throw new System.ComponentModel.Win32Exception();

                byte[] medium = SidBytes(new System.Security.Principal.SecurityIdentifier("S-1-16-8192"));
                var hm = GCHandle.Alloc(medium, GCHandleType.Pinned); pins.Add(hm);
                IntPtr label = Marshal.AllocHGlobal(IntPtr.Size * 2);
                try
                {
                    Marshal.WriteIntPtr(label, hm.AddrOfPinnedObject());
                    Marshal.WriteInt32(label, IntPtr.Size, 0x20 /* SE_GROUP_INTEGRITY */);
                    if (!SetTokenInformation(imp, 25 /* TokenIntegrityLevel */, label, (uint)(IntPtr.Size * 2 + medium.Length)))
                        throw new System.ComponentModel.Win32Exception();
                }
                finally { Marshal.FreeHGlobal(label); }

                byte[] user = SidBytes(System.Security.Principal.WindowsIdentity.GetCurrent().User!);
                var hu = GCHandle.Alloc(user, GCHandleType.Pinned); pins.Add(hu);
                IntPtr owner = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(owner, hu.AddrOfPinnedObject());
                    if (!SetTokenInformation(imp, 4 /* TokenOwner */, owner, (uint)IntPtr.Size))
                        throw new System.ComponentModel.Win32Exception();
                }
                finally { Marshal.FreeHGlobal(owner); }
                return imp;
            }
            finally { CloseHandle(restricted); }
        }
        finally
        {
            foreach (var h in pins) h.Free();
            CloseHandle(own);
        }
    }

    // ── Named objects ──────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateMutexW(IntPtr sa, bool initialOwner, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateEventW(IntPtr sa, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenMutexW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReleaseMutex(IntPtr h);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(IntPtr h, int type, uint info, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr sd);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
    internal const uint SYNCHRONIZE = 0x00100000, READ_CONTROL = 0x00020000;

    /// <summary>SECURITY_ATTRIBUTES for an SDDL string, freed by the caller
    /// with <see cref="FreeSa"/>.</summary>
    internal static IntPtr Sa(string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();
        IntPtr sa = Marshal.AllocHGlobal(IntPtr.Size * 3);
        Marshal.WriteInt32(sa, 0, IntPtr.Size * 3);
        Marshal.WriteIntPtr(sa, IntPtr.Size, sd);
        Marshal.WriteInt32(sa, IntPtr.Size * 2, 0);
        return sa;
    }

    internal static void FreeSa(IntPtr sa)
    {
        LocalFree(Marshal.ReadIntPtr(sa, IntPtr.Size));
        Marshal.FreeHGlobal(sa);
    }

    /// <summary>The owner of a named event or section as a SID string, or
    /// "absent", or "error N" when it exists and does not open for
    /// READ_CONTROL as that kind of object.</summary>
    internal static string OwnerOf(string name, bool section)
    {
        IntPtr h = section ? OpenFileMappingW(READ_CONTROL, false, name) : OpenEventW(READ_CONTROL, false, name);
        if (h == IntPtr.Zero)
        {
            int e = Marshal.GetLastWin32Error();
            return e == ERROR_FILE_NOT_FOUND ? "absent" : $"error {e}";
        }
        try
        {
            if (GetSecurityInfo(h, 6, 1, out IntPtr owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd) != 0) return "unreadable";
            try { return new System.Security.Principal.SecurityIdentifier(owner).Value; }
            finally { LocalFree(sd); }
        }
        finally { CloseHandle(h); }
    }

    internal static bool AdministrativeSid(string sid) => sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-32-544";

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

    // ── Directory security ─────────────────────────────────────────────

    static readonly System.Security.Principal.SecurityIdentifier SidSystem =
        new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
    static readonly System.Security.Principal.SecurityIdentifier SidAdmins =
        new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

    const System.Security.AccessControl.FileSystemRights WriteRights =
        System.Security.AccessControl.FileSystemRights.WriteData |
        System.Security.AccessControl.FileSystemRights.AppendData |
        System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes |
        System.Security.AccessControl.FileSystemRights.WriteAttributes |
        System.Security.AccessControl.FileSystemRights.Delete |
        System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles |
        System.Security.AccessControl.FileSystemRights.ChangePermissions |
        System.Security.AccessControl.FileSystemRights.TakeOwnership;

    /// <summary>Why a directory is not Administrators/SYSTEM-write-only, or
    /// null when it is: owned by Administrators or SYSTEM, and no other
    /// principal holds an allow ACE with any write-class right, inherited or
    /// not. Read without the SDK, from the directory itself.</summary>
    internal static string? WhyWritableByOthers(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return "missing";
        var attrs = System.IO.File.GetAttributes(dir);
        if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0) return "a reparse point";
        var sec = System.IO.FileSystemAclExtensions.GetAccessControl(new System.IO.DirectoryInfo(dir));
        var owner = sec.GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
        if (owner == null || !(owner.Equals(SidAdmins) || owner.Equals(SidSystem)))
            return $"owner {Name(owner)}";
        foreach (System.Security.AccessControl.FileSystemAccessRule r in
                 sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (r.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
            if ((r.FileSystemRights & WriteRights) == 0) continue;
            var sid = r.IdentityReference as System.Security.Principal.SecurityIdentifier;
            if (sid != null && (sid.Equals(SidAdmins) || sid.Equals(SidSystem))) continue;
            return $"{Name(sid)} has {r.FileSystemRights}";
        }
        return null;
    }

    static string Name(System.Security.Principal.SecurityIdentifier? sid)
    {
        if (sid == null) return "(none)";
        try { return sid.Translate(typeof(System.Security.Principal.NTAccount)).Value; }
        catch { return sid.Value; }
    }

    /// <summary>Give the directory the descriptor the SDK gives its staging
    /// tree: owner Administrators, protected, full control for SYSTEM and
    /// Administrators and nobody else. Undoes <see cref="MakeUserWritable"/>
    /// when the SDK under test did not.</summary>
    internal static void Protect(string dir)
    {
        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        System.IO.FileSystemAclExtensions.SetAccessControl(new System.IO.DirectoryInfo(dir), sec);
    }

    /// <summary>Create the directory, or reset an existing one, so that
    /// Users have full control of it and everything under it. Stands in for
    /// a directory some other program made writable before the SDK ran.</summary>
    internal static void MakeUserWritable(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var sec = new System.Security.AccessControl.DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm("D:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)");
        System.IO.FileSystemAclExtensions.SetAccessControl(new System.IO.DirectoryInfo(dir), sec);
    }
}
