// What Windows enumerated for a controller: the HID collections under its
// parent devnode, each read through the HID class's own calls.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal sealed record Collection(
    string Path, ushort Vid, ushort Pid, ushort Version, byte[] Caps,
    byte[] InputButtons, byte[] InputValues, byte[] OutputButtons, byte[] OutputValues,
    byte[] FeatureButtons, byte[] FeatureValues,
    string? Manufacturer, string? Product, string? Serial)
{
    public ushort Usage => BitConverter.ToUInt16(Caps, 0);
    public ushort UsagePage => BitConverter.ToUInt16(Caps, 2);
    public ushort InputLength => BitConverter.ToUInt16(Caps, 4);
    public ushort OutputLength => BitConverter.ToUInt16(Caps, 6);
    public ushort FeatureLength => BitConverter.ToUInt16(Caps, 8);

    /// <summary>Every field the HID class parses from the descriptor, the
    /// path and the strings aside.</summary>
    public string ParsedKey => $"{Vid:X4}:{Pid:X4}:{Version:X4} {FieldsKey}";

    /// <summary>The fields the HID class parses from the descriptor, without
    /// the attributes, which the parity part checks against what Windows
    /// gives each backend's device.</summary>
    public string FieldsKey =>
        $"caps {Convert.ToHexString(Caps)} ib {Convert.ToHexString(InputButtons)} iv {Convert.ToHexString(InputValues)} " +
        $"ob {Convert.ToHexString(OutputButtons)} ov {Convert.ToHexString(OutputValues)} fb {Convert.ToHexString(FeatureButtons)} fv {Convert.ToHexString(FeatureValues)}";
}

internal static partial class Hid
{
    static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>The instance ids of a devnode's direct children.</summary>
    public static List<string> Children(string parentId)
    {
        var list = new List<string>();
        if (CM_Locate_DevNodeW(out uint parent, parentId, 0) != 0) return list;
        if (CM_Get_Child(out uint child, parent, 0) != 0) return list;
        do
        {
            string? id = DeviceId(child);
            if (id != null) list.Add(id);
        } while (CM_Get_Sibling(out child, child, 0) == 0);
        return list;
    }

    static unsafe string? DeviceId(uint dev)
    {
        char* buf = stackalloc char[512];
        if (CM_Get_Device_IDW(dev, buf, 512, 0) != 0) return null;
        return new string(buf);
    }

    /// <summary>Present, started and without a problem.</summary>
    public static (bool Present, bool Started, uint Problem) Status(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, 0) != 0) return (false, false, 0);
        if (CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) != 0) return (false, false, 0);
        return (true, (status & 0x8) != 0, (status & 0x400) != 0 ? problem : 0);
    }

    /// <summary>The function driver Windows bound to a device, from its
    /// record: the service, and the INF and provider of its driver
    /// package. Empty strings for what the record does not hold.</summary>
    public static (string Service, string Inf, string Provider) Binding(string instanceId)
    {
        using var dev = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        string service = dev?.GetValue("Service") as string ?? "";
        string driver = dev?.GetValue("Driver") as string ?? "";
        using var cls = driver.Length > 0 ? Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}") : null;
        return (service, cls?.GetValue("InfPath") as string ?? "", cls?.GetValue("ProviderName") as string ?? "");
    }

    /// <summary>True when a record of the instance id exists, present or
    /// not.</summary>
    public static bool RecordExists(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, 1) == 0;

    /// <summary>The HID interface paths of a device.</summary>
    public static List<string> Interfaces(string instanceId)
    {
        var g = HidInterface;
        if (CM_Get_Device_Interface_List_SizeW(out uint size, ref g, instanceId, 0) != 0 || size <= 1) return new();
        var buf = new char[size];
        unsafe
        {
            fixed (char* b = buf)
                if (CM_Get_Device_Interface_ListW(ref g, instanceId, b, size, 0) != 0) return new();
        }
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The instance ids of every devnode under a parent, children
    /// first, then theirs. A HID child can sit one level down (a HID
    /// interface, the UMDF2 parent's child) or two (Windows' GIP driver
    /// makes the device's XInput child, and the HID device sits under
    /// that).</summary>
    public static List<string> Descendants(string parentId)
    {
        var all = new List<string>();
        var level = Children(parentId);
        while (level.Count > 0)
        {
            all.AddRange(level);
            level = level.SelectMany(Children).ToList();
        }
        return all;
    }

    /// <summary>Every HID collection under a parent, at any depth, read.
    /// Null for a path that does not open.</summary>
    public static List<Collection> Read(string parentId, out List<string> errors)
    {
        errors = new();
        var list = new List<Collection>();
        foreach (string child in Descendants(parentId))
            foreach (string path in Interfaces(child))
            {
                var c = ReadOne(path, out string? error);
                if (c != null) list.Add(c);
                else errors.Add($"{path}: {error}");
            }
        return list.OrderBy(c => c.UsagePage).ThenBy(c => c.Usage).ThenBy(c => c.InputLength).ToList();
    }

    public static Collection? ReadOne(string path, out string? error)
    {
        error = null;
        using var h = Open(path, 0);
        if (h.IsInvalid) { error = $"CreateFile {Marshal.GetLastPInvokeError()}"; return null; }
        var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) { error = "HidD_GetAttributes"; return null; }
        if (!HidD_GetPreparsedData(h, out IntPtr ppd)) { error = "HidD_GetPreparsedData"; return null; }
        try
        {
            var caps = new byte[70];
            if (HidP_GetCaps(ppd, caps) != HIDP_STATUS_SUCCESS) { error = "HidP_GetCaps"; return null; }
            ushort C(int at) => BitConverter.ToUInt16(caps, at);
            // HIDP_CAPS: five USHORTs, Reserved[17], then the counts:
            // link collection nodes, then input, output and feature, each
            // button caps, value caps, data indices.
            byte[] Get(bool buttons, int type, ushort count)
            {
                if (count == 0) return Array.Empty<byte>();
                var buf = new byte[72 * count];
                ushort n = count;
                int s = buttons ? HidP_GetButtonCaps(type, buf, ref n, ppd) : HidP_GetValueCaps(type, buf, ref n, ppd);
                return s == HIDP_STATUS_SUCCESS ? buf.AsSpan(0, 72 * n).ToArray() : Encoding.ASCII.GetBytes($"status {s:X8}");
            }
            return new Collection(path, attr.VendorID, attr.ProductID, attr.VersionNumber, caps,
                Get(true, 0, C(46)), Get(false, 0, C(48)), Get(true, 1, C(52)), Get(false, 1, C(54)),
                Get(true, 2, C(58)), Get(false, 2, C(60)),
                Str(h, HidD_GetManufacturerString), Str(h, HidD_GetProductString), Str(h, HidD_GetSerialNumberString));
        }
        finally { HidD_FreePreparsedData(ppd); }
    }

    delegate bool StringCall(SafeFileHandle h, byte[] buffer, uint length);

    static string? Str(SafeFileHandle h, StringCall call)
    {
        var buf = new byte[512];
        if (!call(h, buf, (uint)buf.Length)) return null;
        string s = Encoding.Unicode.GetString(buf);
        int z = s.IndexOf('\0');
        return z >= 0 ? s[..z] : s;
    }

    public static SafeFileHandle Open(string path, uint access, bool overlapped = true) =>
        CreateFileW(path, access, 3 /* read and write sharing */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                    access != 0 && overlapped ? 0x40000000u /* FILE_FLAG_OVERLAPPED */ : 0u, IntPtr.Zero);

    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID, ProductID, VersionNumber; }

    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr ppd);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(IntPtr ppd);
    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr ppd, [Out] byte[] caps);
    [LibraryImport("hid.dll")]
    private static partial int HidP_GetButtonCaps(int type, [Out] byte[] caps, ref ushort length, IntPtr ppd);
    [LibraryImport("hid.dll")]
    private static partial int HidP_GetValueCaps(int type, [Out] byte[] caps, ref ushort length, IntPtr ppd);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetManufacturerString(SafeFileHandle h, [Out] byte[] buffer, uint length);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetProductString(SafeFileHandle h, [Out] byte[] buffer, uint length);
    [LibraryImport("hid.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetSerialNumberString(SafeFileHandle h, [Out] byte[] buffer, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Child(out uint child, uint parent, uint flags);
    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Sibling(out uint sibling, uint dev, uint flags);
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static unsafe partial int CM_Get_Device_IDW(uint dev, char* buffer, uint length, uint flags);
    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint dev, uint flags);
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid g, string? id, uint flags);
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int CM_Get_Device_Interface_ListW(ref Guid g, string? id, char* buffer, uint length, uint flags);
}
