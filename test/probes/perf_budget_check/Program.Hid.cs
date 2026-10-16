// What the modes that act as a host need: a controller's own HID
// interfaces, found under its devnode and never by vendor and product ID
// (a physical pad with the same IDs may be paired), a reader that stamps
// each input report as ReadFile returns it, the report lengths a report
// descriptor declares, and the driver a devnode runs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    // ── A controller's HID interfaces ────────────────────────────────────

    /// <summary>The device paths of the HID interfaces of every child of a
    /// devnode: the top-level collections HID class made for it. Waits up
    /// to <paramref name="waitMs"/> for the first to appear.</summary>
    static List<string> ChildHidInterfaces(string parentInstanceId, int waitMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var paths = new List<string>();
            if (CM_Locate_DevNodeW(out uint parent, parentInstanceId, 0) == 0
                && CM_Get_Child(out uint child, parent, 0) == 0)
            {
                do
                {
                    var id = new char[MaxDeviceIdLen + 1];
                    if (CM_Get_Device_IDW(child, id, id.Length, 0) != 0) continue;
                    string childId = new string(id, 0, Array.IndexOf(id, '\0') is int n and >= 0 ? n : id.Length);
                    paths.AddRange(InterfacesOf(childId));
                }
                while (CM_Get_Sibling(out child, child, 0) == 0);
            }
            if (paths.Count > 0 || sw.ElapsedMilliseconds >= waitMs) return paths;
            Thread.Sleep(50);
        }
    }

    static List<string> InterfacesOf(string deviceId)
    {
        var list = new List<string>();
        var guid = GuidDevinterfaceHid;
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, deviceId, 0) != 0 || len <= 1) return list;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, deviceId, buf, len, 0) != 0) return list;
        foreach (string s in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)) list.Add(s);
        return list;
    }

    /// <summary>The first HID interface of the controller that
    /// <paramref name="want"/> accepts, opened, or null with the
    /// reason.</summary>
    static HidDevice? OpenOwnInterface(HMController c, Func<HidDevice, bool> want, out string why)
    {
        string? parent = c.InstanceId;
        if (parent == null) { why = "the controller has no devnode"; return null; }
        var paths = ChildHidInterfaces(parent);
        if (paths.Count == 0) { why = $"no HID interface under {parent} in 5 s"; return null; }
        foreach (string path in paths)
        {
            var d = HidDevice.Open(path);
            if (d == null) continue;
            if (want(d)) { why = ""; return d; }
            d.Dispose();
        }
        why = $"none of the {paths.Count} HID interfaces under {parent} fits";
        return null;
    }

    /// <summary>One opened HID interface and the report lengths HID class
    /// gives it (HIDP_CAPS).</summary>
    sealed class HidDevice : IDisposable
    {
        public readonly string Path;
        public readonly SafeFileHandle Handle;
        public readonly int InputLength, OutputLength, FeatureLength;

        HidDevice(string path, SafeFileHandle h, int input, int output, int feature)
        {
            Path = path; Handle = h; InputLength = input; OutputLength = output; FeatureLength = feature;
        }

        public static HidDevice? Open(string path)
        {
            var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) { h.Dispose(); return null; }
            try
            {
                var caps = new byte[64];
                if (HidP_GetCaps(pp, caps) != HIDP_STATUS_SUCCESS) { h.Dispose(); return null; }
                // HIDP_CAPS: Usage, UsagePage, then the input, output and
                // feature report byte lengths.
                return new HidDevice(path, h, BitConverter.ToUInt16(caps, 4), BitConverter.ToUInt16(caps, 6), BitConverter.ToUInt16(caps, 8));
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        public void Dispose() => Handle.Dispose();
    }

    /// <summary>Write one output report as hidapi does on Windows, through
    /// WriteFile, padded with zeros to the device's output report length,
    /// report ID first.</summary>
    static bool WriteOutput(HidDevice d, byte[] report, out int error)
    {
        var buf = new byte[Math.Max(d.OutputLength, report.Length)];
        report.CopyTo(buf, 0);
        bool ok = WriteFile(d.Handle, buf, buf.Length, out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    // ── A reader that stamps each report ─────────────────────────────────

    /// <summary>The bytes a report must carry: Expect at every position
    /// Care marks, and the length.</summary>
    sealed record ReportPattern(byte[] Expect, bool[] Care)
    {
        public bool Matches(byte[] report, int length)
        {
            if (length != Expect.Length) return false;
            for (int i = 0; i < length; i++)
                if (Care[i] && report[i] != Expect[i]) return false;
            return true;
        }
    }

    /// <summary>Reads one HID interface on its own thread, as
    /// coexist_check's HidCapture does, and stamps each input report with
    /// the time ReadFile returned it. While a pattern is armed, the first
    /// report that matches it sets Hit. Nothing is allocated per report.
    /// Dispose cancels the read, so the thread ends.</summary>
    sealed class HidReader : IDisposable
    {
        readonly HidDevice _dev;
        readonly Thread _thread;
        readonly byte[] _buf, _last;
        readonly object _lastLock = new();
        int _lastLength;
        long _count, _hit;
        ReportPattern? _armed;
        volatile bool _stop;
        public int ReadError { get; private set; }

        public HidReader(HidDevice dev)
        {
            _dev = dev;
            _buf = new byte[dev.InputLength];
            _last = new byte[dev.InputLength];
            _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "e2e reader" };
            _thread.Start();
        }

        void Loop()
        {
            while (!_stop)
            {
                if (!ReadFile(_dev.Handle, _buf, _buf.Length, out int read, IntPtr.Zero))
                {
                    if (!_stop) ReadError = Marshal.GetLastWin32Error();
                    return;
                }
                long t = Stopwatch.GetTimestamp();
                if (read <= 0) continue;
                var armed = Volatile.Read(ref _armed);
                if (armed != null && Interlocked.Read(ref _hit) == 0 && armed.Matches(_buf, read))
                    Interlocked.CompareExchange(ref _hit, t, 0);
                lock (_lastLock)
                {
                    Buffer.BlockCopy(_buf, 0, _last, 0, read);
                    _lastLength = read;
                    _count++;
                }
            }
        }

        /// <summary>Reports read so far.</summary>
        public long Count { get { lock (_lastLock) return _count; } }

        /// <summary>A copy of the last report read, and how many came
        /// before it.</summary>
        public (byte[] Report, long Count) Last()
        {
            lock (_lastLock) return (_last.AsSpan(0, _lastLength).ToArray(), _count);
        }

        public void Arm(ReportPattern p)
        {
            Interlocked.Exchange(ref _hit, 0);
            Volatile.Write(ref _armed, p);
        }

        public void Disarm() => Volatile.Write(ref _armed, null);

        /// <summary>The Stopwatch time of the first report that matched the
        /// armed pattern, or 0.</summary>
        public long Hit => Interlocked.Read(ref _hit);

        public void Dispose()
        {
            _stop = true;
            CancelIoEx(_dev.Handle, IntPtr.Zero);
            _thread.Join(2000);
        }
    }

    /// <summary>What a report looks like in each of two states: each state
    /// is submitted twice, the last report read 150 ms after each submit is
    /// kept, and the positions that hold the same byte in both reports of a
    /// state are the ones a pattern compares. A counter or a time stamp
    /// differs between the two and drops out. Null, with the reason, when a
    /// submit brought no report or no compared position tells the states
    /// apart.</summary>
    static (ReportPattern A, ReportPattern B)? Calibrate(HMController c, HidReader rd, HMGamepadState a, HMGamepadState b, out string why)
    {
        var got = new byte[4][];
        for (int k = 0; k < 4; k++)
        {
            long before = rd.Count;
            c.SubmitState(k % 2 == 0 ? a : b);
            Thread.Sleep(150);
            var (report, count) = rd.Last();
            if (count == before) { why = $"no report in 150 ms after submit {k + 1} of 4"; return null; }
            got[k] = report;
        }
        byte[] a0 = got[0], b0 = got[1], a1 = got[2], b1 = got[3];
        if (a0.Length != a1.Length || b0.Length != b1.Length || a0.Length != b0.Length)
        {
            why = $"report lengths {a0.Length}, {b0.Length}, {a1.Length} and {b1.Length} differ";
            return null;
        }
        var care = new bool[a0.Length];
        int telling = 0;
        for (int i = 0; i < care.Length; i++)
        {
            care[i] = a0[i] == a1[i] && b0[i] == b1[i];
            if (care[i] && a0[i] != b0[i]) telling++;
        }
        if (telling == 0) { why = $"no stable byte of the {a0.Length}-byte report tells the two states apart"; return null; }
        why = $"{care.Count(x => x)} of {care.Length} bytes compared, {telling} tell the states apart";
        return (new ReportPattern(a0, care), new ReportPattern(b0, care));
    }

    // ── Report lengths from a report descriptor ──────────────────────────

    /// <summary>The report IDs a report descriptor declares for one main
    /// item kind, each with its length in bytes on the wire: the report's
    /// bits rounded up, plus the ID byte when the descriptor declares IDs.
    /// ID 0 stands for the one report of a descriptor that declares none.
    /// Report Size, Report Count and Report ID are global items, carried by
    /// Push and Pop (HID 1.11 section 6.2.2.7), as coexist_check's
    /// HidFeatureLayout on lane/DD0 reads them. Constant padding counts,
    /// since it is on the wire.</summary>
    static SortedDictionary<byte, int> DeclaredReports(byte[] descriptor, ReportKind kind)
    {
        byte mainTag = kind switch { ReportKind.Input => 0x80, ReportKind.Output => 0x90, _ => 0xB0 };
        var bits = new SortedDictionary<byte, int>();
        int id = 0, size = 0, count = 0;
        bool ids = false;
        var stack = new Stack<(int Id, int Size, int Count)>();
        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i];
            if (prefix == 0xFE)
            {
                // A long item: data size, then the tag, then the data
                // (HID 1.11 section 6.2.2.3).
                if (i + 1 >= descriptor.Length) break;
                i += 3 + descriptor[i + 1];
                continue;
            }
            int n = prefix & 3;
            if (n == 3) n = 4;
            uint value = 0;
            for (int k = 0; k < n && i + 1 + k < descriptor.Length; k++)
                value |= (uint)descriptor[i + 1 + k] << (8 * k);
            i += 1 + n;
            switch (prefix & 0xFC)
            {
                case 0x84: id = (int)value; ids = true; break;   // Report ID
                case 0x74: size = (int)value; break;             // Report Size
                case 0x94: count = (int)value; break;            // Report Count
                case 0xA4: stack.Push((id, size, count)); break;  // Push
                case 0xB4: if (stack.Count > 0) (id, size, count) = stack.Pop(); break;
                default:
                    if ((prefix & 0xFC) == mainTag)
                        bits[(byte)id] = (bits.TryGetValue((byte)id, out int b) ? b : 0) + size * count;
                    break;
            }
        }
        var lengths = new SortedDictionary<byte, int>();
        foreach (var (rid, b) in bits) lengths[rid] = (b + 7) / 8 + (ids ? 1 : 0);
        return lengths;
    }

    enum ReportKind { Input, Output, Feature }

    // ── The driver a devnode runs ────────────────────────────────────────

    /// <summary>Whether the controller's devnode runs the driver this SDK
    /// embeds: its DriverVersion and DriverDate against the embedded INF's
    /// DriverVer. A newer driver kept by the never-downgrade policy would
    /// otherwise be measured as this tree's. A controller with no devnode
    /// of the HIDMaestro driver (USB/IP) passes with a note.</summary>
    static bool RunsEmbeddedDriver(HMController c, out string detail)
    {
        string? id = c.InstanceId;
        if (id == null || c.Profile.RequiresUsbipBackend)
        {
            detail = "USB/IP, no HIDMaestro driver on its path";
            return true;
        }
        var stamp = EmbeddedDriverVer();
        if (stamp == null) { detail = "the SDK embeds no hidmaestro.inf with a DriverVer line"; return false; }
        if (CM_Locate_DevNodeW(out uint dev, id, 0) != 0) { detail = $"{id} not found"; return false; }
        string? version = null;
        DateTime? date = null;
        var key = new DEVPROPKEY { fmtid = DriverPropertyGuid, pid = 3 };   // DEVPKEY_Device_DriverVersion
        var buf = new byte[512];
        uint size = (uint)buf.Length;
        if (CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref size, 0) == 0 && type == DEVPROP_TYPE_STRING)
            version = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        key.pid = 2;                                                         // DEVPKEY_Device_DriverDate
        size = (uint)buf.Length;
        long fileTime = 0;
        if (CM_Get_DevNode_PropertyW(dev, ref key, out type, buf, ref size, 0) == 0 && type == DEVPROP_TYPE_FILETIME && size == 8)
        {
            fileTime = BitConverter.ToInt64(buf, 0);
            date = DateTime.FromFileTimeUtc(fileTime).Date;
        }
        // The INF's date as a FILETIME. Taken as midnight UTC or local, it
        // reads as the same calendar day one way or the other.
        bool sameDay = fileTime != 0
            && (DateTime.FromFileTimeUtc(fileTime).Date == stamp.Value.Date || DateTime.FromFileTime(fileTime).Date == stamp.Value.Date);
        bool ok = version == stamp.Value.Version.ToString() && sameDay;
        detail = string.Create(CultureInfo.InvariantCulture,
            $"{id} runs {version ?? "(no version)"} of {date:yyyy-MM-dd}, the SDK embeds {stamp.Value.Version} of {stamp.Value.Date:yyyy-MM-dd}");
        return ok;
    }

    /// <summary>The DriverVer of the hidmaestro.inf the SDK embeds for this
    /// machine's architecture, read from the SDK's manifest resource
    /// (DriverBuilder.NativePrefixFor) with the pattern of
    /// DriverDeploy.DriverVerLine, so it reads any tree's SDK.</summary>
    static (DateTime Date, Version Version)? EmbeddedDriverVer()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        using var stream = typeof(HMContext).Assembly.GetManifestResourceStream($"HIDMaestro.Native.{arch}.hidmaestro.inf");
        if (stream == null) return null;
        string text = new System.IO.StreamReader(stream).ReadToEnd();
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"(?m)^\s*DriverVer\s*=\s*(\d{1,2})/(\d{1,2})/(\d{4})\s*,\s*(\d+)\.(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) return null;
        int I(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return (new DateTime(I(3), I(1), I(2)), new Version(I(4), I(5), I(6), I(7)));
    }

    // ── Native ───────────────────────────────────────────────────────────

    static readonly Guid GuidDevinterfaceHid = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    // devpkey.h: DEVPKEY_Device_DriverDate is pid 2 and DriverVersion pid 3
    // of this GUID.
    static readonly Guid DriverPropertyGuid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    const int MaxDeviceIdLen = 200;
    const uint DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_FILETIME = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string deviceId, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int length, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int length, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
}
