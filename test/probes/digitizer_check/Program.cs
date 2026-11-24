// digitizer_check: touchscreens, pens and touchpads.
//
//   offline              Offline.cs: the packer over real descriptors.
//   spike <umdf2|usbip>  A two-finger touchscreen and a pen, each created as
//                        a custom profile on the backend named, with no
//                        contacts sent. The touchscreen takes vmulti's finger
//                        collection (djpnewton/vmulti sys/vmulti.h,
//                        MT_TOUCH_COLLECTION) and its device configuration
//                        collection, with the contact count maximum on a
//                        feature report of its own, as Microsoft's sample
//                        touchscreen descriptor has it. The pen is
//                        Microsoft's sample pen descriptor without its
//                        latency and certification reports. For each, the
//                        HID children Windows builds with their IDs, class,
//                        service and problem code, the feature reads and
//                        writes the program sees for five seconds, then the
//                        probe's own read of the contact count maximum as the
//                        positive control for that path, and session 0's
//                        digitizer metrics.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using HIDMaestro;

internal static unsafe partial class Program
{
    // Report 1: two fingers of 10 bytes and a contact count, 22 bytes with
    // the ID. Report 2: the contact count maximum. Report 3: device mode and
    // device identifier.
    const string FingerCollection =
        "0922a102" + "0942150025017501950181020932810209478102950581037508" + "0951950181020501" +
        "26ff7f7510550065003500460000" + "0930810209318102" + "050d0948810209498102" + "c0";
    static readonly string TouchDescriptor =
        "050d0904a1018501" + FingerCollection + FingerCollection +
        "050d0954950175081500250881028502" + "0955b102" + "c0" +
        "090ea1018503" + "0923a102" + "09520953150025" + "0a" + "75089502b102" + "c0" + "c0";

    // Report 1: tip, barrel, invert, eraser, in range, X, Y, pressure and
    // tilts, 10 bytes with the ID (Microsoft, Sample Report Descriptors,
    // pen-sample-report-descriptors).
    const string PenDescriptor =
        "050d0902a1018501" + "0920a100" +
        "0942094409" + "3c" + "0945150025017501950481029501810309328102950281" + "03" +
        "0501093075109501a4550d651335004" + "63a2026f8528102" + "0931462c18266c3e8102" + "b4" +
        "050d093026ff008102" + "7508093d1581257f8102" + "093e1581257f8102" + "c0c0";

    static string Profile(string id, string name, string pid, string descriptor, string? stubs) =>
        "{\n" +
        $"  \"id\": \"{id}\",\n  \"name\": \"{name}\",\n  \"vendor\": \"HIDMaestro\",\n" +
        $"  \"vid\": \"0x1209\",\n  \"pid\": \"{pid}\",\n" +
        $"  \"productString\": \"{name}\",\n  \"manufacturerString\": \"HIDMaestro\",\n" +
        "  \"type\": \"other\",\n  \"connection\": \"usb\",\n" +
        $"  \"descriptor\": \"{descriptor}\"" +
        (stubs == null ? "\n" : $",\n  \"featureStubs\": {stubs}\n") +
        "}\n";

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "offline") return Offline();
        if (args.Length < 2 || args[0] != "spike" || !Enum.TryParse<HMBackend>(args[1], true, out var backend))
        {
            Console.WriteLine("usage: DigitizerCheck offline | spike <umdf2|usbip>");
            return 2;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("  refused: the spike needs elevation");
            return 3;
        }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded.");
            return 2;
        }
        Console.WriteLine($"=== digitizer spike on {backend}: Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}, session {Process.GetCurrentProcess().SessionId} ===");
        Metrics("before any create");

        string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro_digitizer_spike");
        Directory.CreateDirectory(dir);
        string touchStubs = "{ \"reports\": [ { \"id\": \"0x02\", \"size\": 2, \"data\": \"0202\", \"notify\": true }, " +
                            "{ \"id\": \"0x03\", \"size\": 3, \"data\": \"030000\", \"notify\": true } ] }";
        File.WriteAllText(Path.Combine(dir, "spike-touchscreen.json"),
            Profile("spike-touchscreen", "HIDMaestro spike touchscreen", "0x0007", TouchDescriptor, touchStubs));
        File.WriteAllText(Path.Combine(dir, "spike-pen.json"),
            Profile("spike-pen", "HIDMaestro spike pen", "0x0008", PenDescriptor, null));

        using var ctx = new HMContext(backend);
        ctx.InstallDriver();
        int loaded = ctx.LoadProfilesFromDirectory(dir);
        Console.WriteLine($"  loaded {loaded} spike profile(s) from {dir}");
        foreach (var (id, pid, feature) in new[] { ("spike-touchscreen", "PID_0007", (byte)0x02), ("spike-pen", "PID_0008", (byte)0) })
        {
            var profile = ctx.GetProfile(id);
            if (profile == null) { Console.WriteLine($"  FAIL: {id} did not load"); continue; }
            One(ctx, profile, backend, "VID_1209&" + pid, feature);
        }
        Metrics("after every dispose");
        return 0;
    }

    static void One(HMContext ctx, HMProfile profile, HMBackend backend, string vidPid, byte feature)
    {
        var events = new List<string>();
        var clock = Stopwatch.StartNew();
        HMController c;
        try { c = ctx.CreateController(profile, null, backend); }
        catch (Exception e) { Console.WriteLine($"  FAIL: {profile.Id} create: {e.GetType().Name}: {e.Message}"); return; }
        long created = clock.ElapsedMilliseconds;
        void On(HMController _, HMOutputPacket p)
        {
            lock (events) events.Add($"{clock.ElapsedMilliseconds,6} ms {p.Source} id 0x{p.ReportId:X2} [{Convert.ToHexString(p.Data.Span)}]");
        }
        c.OutputReceived += On;
        try
        {
            Console.WriteLine($"-- {profile.Id} on {backend}: created in {created} ms --");
            Thread.Sleep(5000);
            var children = HidChildren(vidPid);
            if (children.Count == 0) Console.WriteLine($"  no present HID devnode carries {vidPid}");
            foreach (var n in children)
            {
                Console.WriteLine($"  {n.Id}");
                Console.WriteLine($"    hardware ids: {string.Join(" ", n.HardwareIds.Take(4))}");
                Console.WriteLine($"    class {n.Class ?? "none"}, service {n.Service ?? "none"}, status 0x{n.Status:X8}, problem {n.Problem}");
            }
            lock (events)
            {
                Console.WriteLine($"  outputs in the first five seconds: {events.Count}");
                foreach (string e in events) Console.WriteLine("    " + e);
                events.Clear();
            }
            if (feature != 0)
            {
                // The positive control: this probe's own read of the report,
                // which the program must see as a feature read.
                foreach (var n in children)
                {
                    string? path = InterfacePath(n.Id);
                    if (path == null) continue;
                    var (ok, detail) = ReadFeature(path, feature);
                    Console.WriteLine($"  own GET_FEATURE 0x{feature:X2} on {n.Id}: {(ok ? "answered" : "failed")} {detail}");
                }
                Thread.Sleep(500);
                lock (events)
                {
                    Console.WriteLine($"  outputs after the own read: {events.Count}");
                    foreach (string e in events) Console.WriteLine("    " + e);
                }
            }
            Metrics($"with {profile.Id}");
        }
        finally
        {
            c.OutputReceived -= On;
            c.Dispose();
        }
    }

    // ── Session 0's digitizer metrics ────────────────────────────────────

    const int SM_DIGITIZER = 94, SM_MAXIMUMTOUCHES = 95;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPointerDevices(ref uint count, IntPtr devices);

    static void Metrics(string when)
    {
        uint count = 0;
        bool ok = GetPointerDevices(ref count, IntPtr.Zero);
        Console.WriteLine($"  metrics {when}: SM_DIGITIZER 0x{GetSystemMetrics(SM_DIGITIZER):X2}, SM_MAXIMUMTOUCHES {GetSystemMetrics(SM_MAXIMUMTOUCHES)}, " +
                          $"GetPointerDevices {(ok ? count.ToString() : "failed " + Marshal.GetLastPInvokeError())}");
    }

    // ── The HID children ─────────────────────────────────────────────────

    record Node(string Id, string[] HardwareIds, string? Class, string? Service, uint Status, uint Problem);

    const uint CR_SUCCESS = 0, CM_GETIDLIST_FILTER_ENUMERATOR = 1, CR_BUFFER_SMALL = 0x1A;
    const uint DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_STRING_LIST = 0x2012;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // devpkey.h: DEVPKEY_Device_HardwareIds {a45c254e-...},3,
    // DEVPKEY_Device_Service {a45c254e-...},6, DEVPKEY_Device_Class
    // {a45c254e-...},9.
    static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    static List<Node> HidChildren(string vidPid)
    {
        var found = new List<Node>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint length, "HID", CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS) return found;
            var list = new char[length];
            uint cr = CM_Get_Device_ID_ListW("HID", list, length, CM_GETIDLIST_FILTER_ENUMERATOR);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) return found;
            foreach (string id in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CM_Locate_DevNodeW(out uint dev, id, 0) != CR_SUCCESS) continue;
                string[] hardwareIds = StringList(dev, 3);
                if (!hardwareIds.Any(h => h.Contains(vidPid, StringComparison.OrdinalIgnoreCase))) continue;
                CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0);
                found.Add(new Node(id, hardwareIds, Str(dev, 9), Str(dev, 6), status, problem));
            }
            break;
        }
        return found;
    }

    static byte[]? Prop(uint dev, uint pid, uint want)
    {
        var key = new DEVPROPKEY { fmtid = DeviceProps, pid = pid };
        uint size = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != want) return null;
        return buf;
    }

    static string? Str(uint dev, uint pid) =>
        Prop(dev, pid, DEVPROP_TYPE_STRING) is { } b ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;

    static string[] StringList(uint dev, uint pid) =>
        Prop(dev, pid, DEVPROP_TYPE_STRING_LIST) is { } b
            ? Encoding.Unicode.GetString(b).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

    // ── The probe's own feature read ─────────────────────────────────────

    // hidclass.h: GUID_DEVINTERFACE_HID.
    static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string id, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_ListW(ref Guid guid, string id, [Out] char[] buffer, uint length, uint flags);

    static string? InterfacePath(string instanceId)
    {
        var guid = HidInterface;
        if (CM_Get_Device_Interface_List_SizeW(out uint length, ref guid, instanceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || length <= 1) return null;
        var buf = new char[length];
        if (CM_Get_Device_Interface_ListW(ref guid, instanceId, buf, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS) return null;
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetFeature(IntPtr h, byte[] buffer, uint length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(IntPtr h, out IntPtr data);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    static (bool Ok, string Detail) ReadFeature(string path, byte reportId)
    {
        IntPtr h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        string how = "read/write";
        if (h == new IntPtr(-1))
        {
            h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            how = "no access";
        }
        if (h == new IntPtr(-1)) return (false, $"open failed, Win32 {Marshal.GetLastPInvokeError()}");
        try
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return (false, $"{how} handle, no preparsed data, Win32 {Marshal.GetLastPInvokeError()}");
            HIDP_CAPS caps;
            try { HidP_GetCaps(pp, out caps); } finally { HidD_FreePreparsedData(pp); }
            string tlc = $"collection 0x{caps.UsagePage:X2}/0x{caps.Usage:X2}, input {caps.InputReportByteLength}, feature {caps.FeatureReportByteLength}";
            if (caps.FeatureReportByteLength == 0) return (false, $"{how} handle, {tlc}: no feature report");
            var buf = new byte[caps.FeatureReportByteLength];
            buf[0] = reportId;
            if (!HidD_GetFeature(h, buf, (uint)buf.Length)) return (false, $"{how} handle, {tlc}: Win32 {Marshal.GetLastPInvokeError()}");
            return (true, $"{how} handle, {tlc}: [{Convert.ToHexString(buf)}]");
        }
        finally { CloseHandle(h); }
    }
}
