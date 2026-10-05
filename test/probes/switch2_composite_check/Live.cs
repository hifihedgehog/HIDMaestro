// Parts B and D of the Switch 2 Pro composite persona check: the persona
// attached for real and driven through Windows' own drivers.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using HIDMaestro;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    static readonly Guid WinUsbInterface = new(InterfaceGuid);

    /// <summary>Another live SDK consumer shares the named sections, and its
    /// controllers would write over the frames this probe submits. Same
    /// test as usbip_e2e_check.</summary>
    static string? FindLiveSdkConsumer()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self) { p.Dispose(); continue; }
            try
            {
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                        return p.ProcessName;
            }
            catch { /* protected, or exited mid-enumeration */ }
            finally { p.Dispose(); }
        }
        for (int i = 0; i < 16; i++)
        {
            IntPtr h = OpenFileMappingW(0x0004, false, $@"Global\HIDMaestroInput{i}");
            if (h != IntPtr.Zero)
            {
                CloseHandle(h);
                return $"another SDK consumer (Global\\HIDMaestroInput{i} is already mapped)";
            }
        }
        return null;
    }

    static bool IsEnvironmental(Exception ex)
        => ex is InvalidOperationException or NotSupportedException
           && (ex.Message.Contains("usbip-win2", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("host controller", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("installer", StringComparison.OrdinalIgnoreCase));

    static byte[]? ReadOsvc()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\usbflags\057E20690101");
        return k?.GetValue("osvc") as byte[];
    }

    // ── An attached persona and the Windows devices it became ───────────

    sealed class Attached : IDisposable
    {
        public HMController Pad = null!;
        public string Serial = "";
        public string UsbId = "";
        public uint UsbNode;
        public string? HidInterfaceId, VendorInterfaceId, HidDeviceId;
        public uint HidInterfaceNode, VendorInterfaceNode, HidDeviceNode;
        public string? HidPath, WinUsbPath;
        public long CreateMs, ReadyMs;

        // What a consumer does: one state, submitted over and over.
        readonly object _stateLock = new();
        HMGamepadState _state;
        volatile bool _stop;
        Thread? _pump;

        public readonly ConcurrentQueue<(byte Left, byte Right, byte[] Raw)> Decoded = new();
        public readonly ConcurrentQueue<(byte Source, byte ReportId, int Size)> Received = new();

        public void Stage(HMGamepadState s) { lock (_stateLock) _state = s; }

        public static Attached? Create(HMContext ctx, HMProfile profile, string? identityKey, out string? skipReason)
        {
            skipReason = null;
            var a = new Attached();
            var sw = Stopwatch.StartNew();
            try { a.Pad = ctx.CreateController(profile, identityKey); }
            catch (Exception ex) when (IsEnvironmental(ex))
            {
                skipReason = ex.Message;
                return null;
            }
            a.CreateMs = sw.ElapsedMilliseconds;
            a.Serial = a.Pad.UsbipHandle!.Device.Descriptors.SerialString ?? "";
            a.UsbId = $@"USB\VID_057E&PID_2069\{a.Serial}";
            a.Pad.OutputDecoded += (_, e) =>
            {
                if (e.Fields.TryGetValue("leftMotor", out var l) && e.Fields.TryGetValue("rightMotor", out var r))
                    a.Decoded.Enqueue(((byte)l, (byte)r, e.RawBytes.ToArray()));
            };
            a.Pad.OutputReceived += (_, p) => a.Received.Enqueue(((byte)p.Source, p.ReportId, p.Data.Length));

            a._pump = new Thread(() =>
            {
                while (!a._stop)
                {
                    HMGamepadState s;
                    lock (a._stateLock) s = a._state;
                    try { a.Pad.SubmitState(s); } catch { }
                    Thread.Sleep(8);
                }
            }) { IsBackground = true, Name = "ProbeSubmit" };
            a._pump.Start();

            // Wait for Windows to finish: the composite parent, both
            // function devices started, the HID collection and the WinUSB
            // interface registered.
            long deadline = Environment.TickCount64 + 30000;
            while (Environment.TickCount64 < deadline)
            {
                a.Discover();
                if (a.HidPath != null && a.WinUsbPath != null) break;
                Thread.Sleep(100);
            }
            a.ReadyMs = sw.ElapsedMilliseconds;
            return a;
        }

        public void Discover()
        {
            UsbNode = Locate(UsbId);
            if (UsbNode == 0) return;
            foreach (var (node, id) in Children(UsbNode))
            {
                if (id.Contains("&MI_00", StringComparison.OrdinalIgnoreCase)) { HidInterfaceNode = node; HidInterfaceId = id; }
                if (id.Contains("&MI_01", StringComparison.OrdinalIgnoreCase)) { VendorInterfaceNode = node; VendorInterfaceId = id; }
            }
            if (HidInterfaceNode != 0)
            {
                foreach (var (node, id) in Children(HidInterfaceNode))
                    if (id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) { HidDeviceNode = node; HidDeviceId = id; }
            }
            if (HidDeviceId != null && HidPath == null)
            {
                HidD_GetHidGuid(out Guid hid);
                HidPath = InterfacePath(hid, HidDeviceId);
            }
            if (VendorInterfaceId != null && WinUsbPath == null)
                WinUsbPath = InterfacePath(WinUsbInterface, VendorInterfaceId);
        }

        public void Dispose()
        {
            _stop = true;
            try { _pump?.Join(500); } catch { }
            try { Pad.Dispose(); } catch { }
        }

        /// <summary>True once the USB device node is gone.</summary>
        public bool WaitGone(int ms)
        {
            long deadline = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < deadline)
            {
                if (Locate(UsbId) == 0) return true;
                Thread.Sleep(100);
            }
            return false;
        }
    }

    // ── HID reader: one report per read, as SDL and every HID client reads ─

    sealed class HidReader : IDisposable
    {
        readonly SafeFileHandle _handle;
        readonly FileStream _stream;
        readonly Thread _thread;
        readonly string _path;
        readonly BlockingCollection<(long Ticks, byte[] Data)> _reports = new();
        volatile bool _stop;

        public HidReader(string path)
        {
            _path = path;
            _handle = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (_handle.IsInvalid) throw new IOException($"open {path}: error {Marshal.GetLastWin32Error()}");
            _stream = new FileStream(_handle, FileAccess.ReadWrite, 0, isAsync: true);
            _thread = new Thread(Loop) { IsBackground = true, Name = "ProbeHidRead" };
            _thread.Start();
        }

        public SafeFileHandle Handle => _handle;

        void Loop()
        {
            var buf = new byte[64];
            while (!_stop)
            {
                try
                {
                    int n = _stream.ReadAsync(buf, 0, buf.Length).GetAwaiter().GetResult();
                    if (n <= 0) continue;
                    _reports.Add((Stopwatch.GetTimestamp(), buf.AsSpan(0, n).ToArray()));
                }
                catch { if (_stop) return; Thread.Sleep(5); }
            }
        }

        public int Pending => _reports.Count;

        public (long Ticks, byte[] Data)? Next(int timeoutMs)
            => _reports.TryTake(out var r, timeoutMs) ? r : null;

        public List<(long Ticks, byte[] Data)> Take(int count, int timeoutMs)
        {
            var got = new List<(long, byte[])>(count);
            long deadline = Environment.TickCount64 + timeoutMs;
            while (got.Count < count)
            {
                int left = (int)(deadline - Environment.TickCount64);
                if (left <= 0 || !_reports.TryTake(out var r, left)) break;
                got.Add(r);
            }
            return got;
        }

        /// <summary>Drop everything read so far and whatever arrives in the
        /// next <paramref name="settleMs"/>.</summary>
        public void Flush(int settleMs)
        {
            long deadline = Environment.TickCount64 + settleMs;
            do { while (_reports.TryTake(out _)) { } Thread.Sleep(2); }
            while (Environment.TickCount64 < deadline);
            while (_reports.TryTake(out _)) { }
        }

        /// <summary>Write one output report, on a handle of its own so the
        /// pending read is left alone.</summary>
        public void Write(byte[] report)
        {
            using var h = CreateFileW(_path, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException($"open for write: error {Marshal.GetLastWin32Error()}");
            if (!WriteFile(h, report, report.Length, out int n, IntPtr.Zero) || n != report.Length)
                throw new IOException($"WriteFile: error {Marshal.GetLastWin32Error()}, wrote {n}");
        }

        public void Dispose()
        {
            _stop = true;
            try { _stream.Dispose(); } catch { }
            try { _thread.Join(500); } catch { }
        }
    }

    // ── WinUSB: interface 1's bulk pair, as libusb drives it ─────────────

    sealed class Bulk : IDisposable
    {
        readonly SafeFileHandle _file;
        IntPtr _usb;
        public const byte Out = 0x02, In = 0x82;

        public Bulk(string path)
        {
            _file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (_file.IsInvalid) throw new IOException($"open {path}: error {Marshal.GetLastWin32Error()}");
            if (!WinUsb_Initialize(_file, out _usb))
                throw new IOException($"WinUsb_Initialize: error {Marshal.GetLastWin32Error()}");
            SetTimeout(In, 250);
            SetTimeout(Out, 1000);
        }

        public void SetTimeout(byte pipe, uint ms)
            => WinUsb_SetPipePolicy(_usb, pipe, 0x03 /* PIPE_TRANSFER_TIMEOUT */, 4, ref ms);

        public (ushort MaxPacket, int Type)? Pipe(byte index)
            => WinUsb_QueryPipe(_usb, 0, index, out var info) ? (info.MaximumPacketSize, info.PipeType) : null;

        public string? StringDescriptor(byte index)
        {
            var b = new byte[255];
            return WinUsb_GetDescriptor(_usb, 0x03, index, 0x0409, b, (uint)b.Length, out uint n) && n >= 2
                ? Encoding.Unicode.GetString(b, 2, (int)n - 2) : null;
        }

        public bool Write(byte[] data) => WinUsb_WritePipe(_usb, Out, data, (uint)data.Length, out uint n, IntPtr.Zero) && n == data.Length;

        /// <summary>One read. Null when nothing arrived before the pipe's
        /// timeout.</summary>
        public byte[]? Read(int length)
        {
            var b = new byte[length];
            return WinUsb_ReadPipe(_usb, In, b, (uint)length, out uint n, IntPtr.Zero) ? b.AsSpan(0, (int)n).ToArray() : null;
        }

        /// <summary>A command and its reply, read as SDL's RecvBulkData
        /// reads it: 64 bytes at a time until a short read.</summary>
        public byte[] Command(byte[] command, int size = 64)
        {
            if (!Write(command)) throw new IOException($"bulk write: error {Marshal.GetLastWin32Error()}");
            var all = new List<byte>();
            while (size > 0)
            {
                int ask = Math.Min(size, 64);
                var part = Read(ask);
                if (part == null) break;
                all.AddRange(part);
                size -= part.Length;
                if (part.Length < ask) break;
            }
            return all.ToArray();
        }

        public void Dispose()
        {
            if (_usb != IntPtr.Zero) { WinUsb_Free(_usb); _usb = IntPtr.Zero; }
            _file.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Part B
    // ══════════════════════════════════════════════════════════════════════

    static bool s_liveSkipped;

    static void PartB(HMContext ctx)
    {
        Console.WriteLine("\n== Part B: attached, through Windows ==");
        var profile = ctx.GetProfile(ProfileId);
        if (profile == null) return;

        string? conflict = FindLiveSdkConsumer();
        if (conflict != null)
        {
            Console.WriteLine($"  [SKIP] parts B, C and D: {conflict} is running and shares the SDK's memory sections.");
            s_liveSkipped = true;
            return;
        }

        foreach (var n in new[] { "steam", "steamwebhelper" })
            if (Process.GetProcessesByName(n).Length > 0)
                Console.WriteLine($"  [WARN] {n}.exe is running. Steam opens this pad's vendor interface, which one host holds at a time.");

        var before = ReadOsvc();
        Console.WriteLine($"  usbflags\\057E20690101 osvc before attach: {(before == null ? "absent" : X(before))}");
        Check("this PC has not recorded revision 0101 as a device without Microsoft OS descriptors",
              before == null || !(before.Length >= 1 && before[0] == 0x00), before == null ? "no key yet" : X(before));

        var a = Attached.Create(ctx, profile, null, out string? skip);
        if (a == null)
        {
            Console.WriteLine($"  [SKIP] parts B, C and D: {skip}");
            s_liveSkipped = true;
            return;
        }
        try
        {
            Console.WriteLine($"  created in {a.CreateMs} ms, Windows ready after {a.ReadyMs} ms, serial {a.Serial}");
            LiveBinding(a);
            if (a.HidPath != null && a.WinUsbPath != null) LiveProtocol(a);
        }
        finally
        {
            a.Dispose();
            Check("teardown removes the USB device", a.WaitGone(15000));
        }
    }

    static void LiveBinding(Attached a)
    {
        Console.WriteLine("\n-- B1 what Windows bound --");
        Check("the USB device enumerates under its serial", a.UsbNode != 0, a.UsbId);
        Check("it is a composite device on usbccgp", string.Equals(Service(a.UsbNode), "usbccgp", StringComparison.OrdinalIgnoreCase), Service(a.UsbNode) ?? "no service");
        Check("interface 0 is on HidUsb", a.HidInterfaceNode != 0 && string.Equals(Service(a.HidInterfaceNode), "HidUsb", StringComparison.OrdinalIgnoreCase),
              $"{a.HidInterfaceId} -> {Service(a.HidInterfaceNode) ?? "no driver"}");
        Check("interface 1 is on WinUSB, from the device's own descriptors", a.VendorInterfaceNode != 0 && string.Equals(Service(a.VendorInterfaceNode), "WINUSB", StringComparison.OrdinalIgnoreCase),
              $"{a.VendorInterfaceId} -> {Service(a.VendorInterfaceNode) ?? "no driver"}");

        string? guid = null;
        if (a.VendorInterfaceId != null)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{a.VendorInterfaceId}\Device Parameters");
            guid = k?.GetValue("DeviceInterfaceGUID") as string;
        }
        Check("DeviceInterfaceGUID is under interface 1's device parameters, which is where libusb reads it",
              string.Equals(guid, InterfaceGuid, StringComparison.OrdinalIgnoreCase), guid ?? "absent");
        Check("WinUSB registered a device interface under that GUID", a.WinUsbPath != null, a.WinUsbPath ?? "none");
        Check("the HID collection is present", a.HidPath != null, a.HidDeviceId ?? "none");

        var osvc = ReadOsvc();
        Check("Windows recorded that the device answered string 0xEE: usbflags osvc is 01 and a vendor code",
              osvc != null && osvc.Length == 2 && osvc[0] == 0x01, osvc == null ? "absent" : X(osvc));
    }

    static void LiveProtocol(Attached a)
    {
        var s2 = a.Pad.UsbipHandle!.Device.Switch2!;
        using var hid = new HidReader(a.HidPath!);
        using var bulk = new Bulk(a.WinUsbPath!);

        Console.WriteLine("\n-- B2 silence before start, and the serial in three places --");
        var p0 = bulk.Pipe(0);
        var p1 = bulk.Pipe(1);
        Console.WriteLine($"  WinUSB pipes: {(p0.HasValue ? $"type {p0.Value.Type}, {p0.Value.MaxPacket} bytes" : "?")} and {(p1.HasValue ? $"type {p1.Value.Type}, {p1.Value.MaxPacket} bytes" : "?")}");
        var sw = Stopwatch.StartNew();
        var r0701 = bulk.Command(Cmd(0x07, 0x01));
        Check("07/01 over WinUSB gets 07 01 00 01 00 f8 00 00 00", r0701.SequenceEqual(Reply(0x07, 0x01, "00")), X(r0701));
        int wait = 200 - (int)sw.ElapsedMilliseconds;
        if (wait > 0) Thread.Sleep(wait);
        Check("no input report reaches the HID handle in the same 200 ms", hid.Pending == 0 && !s2.ReportsOn, $"{hid.Pending} reports");

        var sb = new byte[256];
        string hidSerial = HidD_GetSerialNumberString(hid.Handle, sb, sb.Length)
            ? Encoding.Unicode.GetString(sb).Split('\0')[0] : "<failed>";
        string? usbSerial = bulk.StringDescriptor(3);
        Check("HID's serial string and USB string 3 are the persona's serial, the pair SDL's fork matches on",
              hidSerial == a.Serial && usbSerial == a.Serial, $"{hidSerial} / {usbSerial}");
        Check("USB strings 1 and 2 read Nintendo and Switch 2 Pro Controller",
              bulk.StringDescriptor(1) == "Nintendo" && bulk.StringDescriptor(2) == "Switch 2 Pro Controller");

        Check("the flash read is written", bulk.Write(FlashBlockCmd(0x13000)));
        var first = bulk.Read(64);
        var second = bulk.Read(16);
        Check("02/01 at 0x13000 reads back as 64 bytes then 16", first != null && second != null && first.Length == 64 && second.Length == 16,
              $"{first?.Length}/{second?.Length}");
        if (first != null && second != null && first.Length + second.Length == 80)
        {
            var block = first.Concat(second).ToArray();
            Check("the serial at reply offset 0x12 equals USB string 3",
                  Encoding.ASCII.GetString(block, 0x12, 16).TrimEnd('\0') == a.Serial, Encoding.ASCII.GetString(block, 0x12, 16).TrimEnd('\0'));
        }
        // No read is left to time out here. usbip-win2 completes a canceled
        // transfer without resetting its length (drivers/ude/request_list.cpp
        // in 0.9.8.1 calls UdecxUrbCompleteWithNtStatus and never
        // UdecxUrbSetBytesCompleted), and WinUSB takes that length as bytes
        // received. Measured here: a timed-out 64-byte read came back as
        // success with the previous reply's bytes, and WinUSB served later
        // reads from the same stale buffer without asking the device. The
        // device's own handling of a parked and unlinked read is part A.

        Console.WriteLine("\n-- B3 start --");
        bool threw = false;
        try { a.Pad.SubmitRawReport(new byte[63]); } catch (NotSupportedException) { threw = true; }
        bool threwExtended = false;
        try { a.Pad.SubmitRawExtendedReport(new byte[64]); } catch (NotSupportedException) { threwExtended = true; }
        Check("the raw submit methods refuse: the device builds its own reports", threw && threwExtended);

        long tStart = Stopwatch.GetTimestamp();
        var started = bulk.Command(Cmd(0x03, 0x0D, H("0100ffffffffffff")));
        Check("03/0D answers 03 01 00 0d 00 f8 00 00 01 00 00 00", started.SequenceEqual(Reply(0x03, 0x0D, "01000000")), X(started));
        var firstReport = hid.Next(2000);
        Check("reports follow on the HID handle, 0x09 first with counter 0",
              firstReport.HasValue && firstReport.Value.Data.Length == 64 && firstReport.Value.Data[0] == 0x09 && firstReport.Value.Data[1] == 0,
              firstReport.HasValue ? $"{X(firstReport.Value.Data.AsSpan(0, 13))} after {(firstReport.Value.Ticks - tStart) * 1000.0 / Stopwatch.Frequency:F1} ms" : "none");
        var run = hid.Take(500, 5000);
        Check("500 more reports arrive", run.Count == 500, $"{run.Count}");
        if (run.Count == 500)
        {
            bool counted = true;
            for (int i = 0; i < run.Count; i++) if (run[i].Data[0] != 0x09 || run[i].Data[1] != (byte)(1 + i)) counted = false;
            Check("every one is 0x09 and the counter rises by 1 through Windows' HID stack", counted);
            double spanMs = (run[^1].Ticks - run[0].Ticks) * 1000.0 / Stopwatch.Frequency;
            double mean = spanMs / (run.Count - 1);
            Check("one report every 4 ms while the consumer submits every 8 ms", mean > 3.96 && mean < 4.20, $"mean {mean:F4} ms over {spanMs:F0} ms");
        }

        Console.WriteLine("\n-- B4 report 0x05 and motion, from SubmitState to ReadFile --");
        var selected = bulk.Command(Cmd(0x03, 0x0A, 0x05, 0, 0, 0));
        Check("03/0A with 0x05 is answered", selected.SequenceEqual(Reply(0x03, 0x0A)), X(selected));
        hid.Flush(40);   // reports Windows had already queued
        var common = hid.Take(60, 2000);
        Check("after it, every report is 0x05", common.Count == 60 && common.All(r => r.Data[0] == 0x05));
        Check("with no feature enabled, bytes 0x2B to 0x3C are 0", common.Count == 60 && common.All(r => r.Data.AsSpan(0x2B, 0x12).ToArray().All(b => b == 0)));

        bulk.Command(Cmd(0x0C, 0x02, 0x27, 0, 0, 0));
        bulk.Command(Cmd(0x0C, 0x04, 0x27, 0, 0, 0));
        a.Stage(new HMGamepadState { AccelGY = 1.0f, GyroDpsX = 100f, Buttons = HMButton.B, Hat = HMHat.North });
        hid.Flush(60);
        var moving = hid.Take(120, 3000);
        Check("with 0x27 enabled, 120 reports", moving.Count == 120, $"{moving.Count}");
        if (moving.Count == 120)
        {
            bool step = true;
            for (int i = 1; i < moving.Count; i++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(moving[i].Data.AsSpan(0x2B)) - BinaryPrimitives.ReadUInt32LittleEndian(moving[i - 1].Data.AsSpan(0x2B)) != 4000) step = false;
            Check("the timestamp at 0x2B rises by 4000 a report", step);
            var d = moving[^1].Data;
            short ay = (short)(d[0x35] | (d[0x36] << 8)), gx = (short)(d[0x37] | (d[0x38] << 8));
            Check("a submitted AccelGY of 1.0 and 100 deg/s on X arrive as 4096 and 1643", ay == 4096 && gx == 1643, $"{ay}/{gx}");
            Check("a submitted B and D-pad up arrive as 0x04 in byte 5 and 0x02 in byte 7", d[5] == 0x04 && d[6] == 0 && d[7] == 0x02 && d[8] == 0, X(d.AsSpan(5, 4)));
        }
        bulk.Command(Cmd(0x0C, 0x03, 0, 0, 0, 0));
        hid.Flush(40);
        var cleared = hid.Take(40, 2000);
        Check("with the mask cleared by 0C/03, bytes 0x2B to 0x3C are 0 again",
              cleared.Count == 40 && cleared.All(r => r.Data[0] == 0x05 && r.Data.AsSpan(0x2B, 0x12).ToArray().All(b => b == 0)));
        a.Stage(new HMGamepadState());

        Console.WriteLine("\n-- B5 rumble --");
        while (a.Decoded.TryDequeue(out _)) { }
        while (a.Received.TryDequeue(out _)) { }
        hid.Write(SdlRumbleReport(0x8000, 0x4000, 1));
        bool half = WaitFor(() => a.Decoded.Any(d => Math.Abs(d.Left - 127) <= 1 && Math.Abs(d.Right - 64) <= 1), 2000);
        Check("SDL's report for (0x8000, 0x4000), written to the HID handle, raises OutputDecoded with leftMotor 127 and rightMotor 64",
              half, string.Join(" ", a.Decoded.Select(d => $"{d.Left}/{d.Right}")));
        Check("OutputReceived carries the same report raw: output report 0x02, 63 bytes after its ID",
              a.Received.Any(r => r.Source == 0 && r.ReportId == 0x02 && r.Size == 63), string.Join(" ", a.Received.Select(r => $"{r.Source}:{r.ReportId:X2}:{r.Size}")));
        Check("the decoded event carries the whole report", a.Decoded.Any(d => d.Raw.Length == 64 && d.Raw[0] == 0x02));
        while (a.Decoded.TryDequeue(out _)) { }
        hid.Write(SdlRumbleReport(0, 0, 2));
        Check("a stop raises 0 and 0", WaitFor(() => a.Decoded.Any(d => d.Left == 0 && d.Right == 0), 2000));
        while (a.Decoded.TryDequeue(out _)) { }
        while (a.Received.TryDequeue(out _)) { }
        var notRumble = new byte[64];
        notRumble[0] = 0x02;
        SdlRumbleReport(0xFFFF, 0xFFFF).AsSpan(1, 20).CopyTo(notRumble.AsSpan(1));
        hid.Write(notRumble);   // control: a second report 0x02, so the window is known to be live
        Check("a later report decodes too, so the lane is live for the next check",
              WaitFor(() => a.Decoded.Any(d => d.Left == 255 && d.Right == 255), 2000));

        Console.WriteLine("\n-- B6 the bulk log --");
        var log = s2.SnapshotBulkLog();
        Check("the log holds what WinUSB sent, the start among it",
              log.Any(p => p.SequenceEqual(Cmd(0x03, 0x0D, H("0100ffffffffffff")))) && log.Any(p => p.SequenceEqual(Cmd(0x07, 0x01))),
              $"{log.Count} payloads");
    }

    static bool WaitFor(Func<bool> condition, int ms)
    {
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Part D: two personas, each on its own handle
    // ══════════════════════════════════════════════════════════════════════

    static void PartD(HMContext ctx)
    {
        Console.WriteLine("\n== Part D: two personas on one PC ==");
        if (s_liveSkipped) { Console.WriteLine("  [SKIP] see part B"); return; }
        var profile = ctx.GetProfile(ProfileId)!;

        var one = Attached.Create(ctx, profile, "switch2-check:one", out _);
        var two = one == null ? null : Attached.Create(ctx, profile, "switch2-check:two", out _);
        try
        {
            if (one == null || two == null)
            {
                Check("two personas attach", false);
                return;
            }
            Check("two personas attach with different serials, so Windows keeps two device instances",
                  one.Serial != two.Serial && one.UsbNode != 0 && two.UsbNode != 0 && one.UsbNode != two.UsbNode,
                  $"{one.Serial} / {two.Serial}");
            Check("each has its own HID collection and its own WinUSB interface",
                  one.HidPath != null && two.HidPath != null && one.WinUsbPath != null && two.WinUsbPath != null
                  && one.HidPath != two.HidPath && one.WinUsbPath != two.WinUsbPath);
            if (one.WinUsbPath == null || two.WinUsbPath == null || one.HidPath == null || two.HidPath == null) return;

            using (var b1 = new Bulk(one.WinUsbPath))
            using (var b2 = new Bulk(two.WinUsbPath))
            using (var h1 = new HidReader(one.HidPath))
            using (var h2 = new HidReader(two.HidPath))
            {
                string SerialOf(Bulk b)
                {
                    var r = b.Command(FlashBlockCmd(0x13000), 0x50);
                    return r.Length == 80 ? Encoding.ASCII.GetString(r, 0x12, 16).TrimEnd('\0') : "<short>";
                }
                string s1 = SerialOf(b1), s2 = SerialOf(b2);
                Check("each WinUSB handle reads its own persona's serial from flash", s1 == one.Serial && s2 == two.Serial, $"{s1} / {s2}");

                b1.Command(Cmd(0x03, 0x0D, H("0100ffffffffffff")));
                Check("starting the first leaves the second silent",
                      h1.Take(20, 1000).Count == 20 && h2.Pending == 0
                      && one.Pad.UsbipHandle!.Device.Switch2!.ReportsOn && !two.Pad.UsbipHandle!.Device.Switch2!.ReportsOn);
                b2.Command(Cmd(0x03, 0x0A, 0x05, 0, 0, 0));
                b2.Command(Cmd(0x03, 0x0D, H("0100ffffffffffff")));
                h1.Flush(30);
                Check("and a report selection on the second leaves the first on 0x09",
                      h2.Take(20, 1000) is { Count: 20 } r2 && r2.All(r => r.Data[0] == 0x05)
                      && h1.Take(20, 1000) is { Count: 20 } r1 && r1.All(r => r.Data[0] == 0x09));
            }

            SdlTwoPads(one, two);
        }
        finally
        {
            two?.Dispose();
            one?.Dispose();
            bool gone = (one?.WaitGone(15000) ?? true) & (two?.WaitGone(15000) ?? true);
            Check("teardown removes both", gone);
        }
        Console.WriteLine("  [NOTE] a real wired pad beside the persona was not run: no Pro Controller 2 is attached to this PC.");
    }

    // ── Native ──────────────────────────────────────────────────────────

    static uint Locate(string instanceId)
        => CM_Locate_DevNodeW(out uint node, instanceId, 0) == 0 ? node : 0;

    static List<(uint Node, string Id)> Children(uint parent)
    {
        var list = new List<(uint, string)>();
        if (CM_Get_Child(out uint child, parent, 0) != 0) return list;
        while (true)
        {
            var sb = new char[512];
            if (CM_Get_Device_IDW(child, sb, sb.Length, 0) == 0)
                list.Add((child, new string(sb).Split('\0')[0]));
            if (CM_Get_Sibling(out uint next, child, 0) != 0) break;
            child = next;
        }
        return list;
    }

    static string? Service(uint node)
    {
        if (node == 0) return null;
        var buf = new byte[512];
        int len = buf.Length;
        // CM_DRP_SERVICE is 5: the CM_DRP numbers run one above SPDRP's.
        return CM_Get_DevNode_Registry_PropertyW(node, 0x00000005, out _, buf, ref len, 0) == 0
            ? Encoding.Unicode.GetString(buf, 0, Math.Max(0, len - 2)) : null;
    }

    static string? InterfacePath(Guid guid, string instanceId)
    {
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref guid, instanceId, 0) != 0 || len <= 1) return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, instanceId, buf, len, 0) != 0) return null;
        string first = new string(buf).Split('\0')[0];
        return first.Length > 0 ? first : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINUSB_PIPE_INFORMATION
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int length, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string path);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string path);

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Child(out uint child, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, [Out] byte[] buffer, ref int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid guid, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string? deviceId, [Out] char[] buffer, int length, uint flags);

    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Free(IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte index, out WINUSB_PIPE_INFORMATION info);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, [Out] byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    static extern bool WinUsb_GetDescriptor(IntPtr handle, byte type, byte index, ushort language, [Out] byte[] buffer, uint length, out uint transferred);
}
