// The live part of ReportLengthCheck: what the installed driver sends,
// read through HID class and, for a keyboard, Raw Input (see Program.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32.SafeHandles;

internal static class Live
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    internal static int Run()
    {
        Console.WriteLine("=== Report IDs and lengths from the live driver ===");
        bool elevated;
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            elevated = new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!elevated)
        {
            Console.WriteLine("  [SKIP] live creates controllers and needs elevation");
            return 2;
        }
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        using var sink = new RawInputSink((0x01, 0x06));
        try
        {
            SameWindow(ctx, sink);
            // wingman has no report IDs, so its check is the SDK's length
            // (5 bytes, Y whole at byte 2) rather than the driver's ID.
            Persona(ctx, "logitech-wingman-formula-gp", (p, c) =>
            {
                var st = new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.2f, [HMAxis.Y] = 0.8f } };
                // Y is 8 bits at bit 16 of the 5-byte report: 0.8 of 255.
                return (st, r => r.Length >= 5 && r[2] == 204, "the 5-byte report with Y 204 at byte 2");
            });
        }
        catch (Exception ex) { Check("the live part ran without throwing", false, ex.Message); }
        finally
        {
            try { HMContext.RemoveAllVirtualControllers(); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures > 0 ? 1 : 0;
    }

    /// <summary>Three controllers up at once, read in one window. The
    /// dualshock-4-v2 control's report did not change. genesis-nso's 0x3F
    /// goes out only under the InputReportId the SDK wrote, where the
    /// driver scanned 0x21 before. microsoft-designer-keyboard's data-only
    /// frame goes out as its dataReportId 0x01, the keyboard report, where
    /// its first input report is the vendor 0x27. Keyboard collections are
    /// Windows' own, so the keyboard is
    /// read through Raw Input: F24 (usage 0x73) arrives as VK_F24 only if
    /// the frame went out as report 0x01.</summary>
    static void SameWindow(HMContext ctx, RawInputSink sink)
    {
        var ds4p = ctx.GetProfile("dualshock-4-v2")!;
        var nsop = ctx.GetProfile("genesis-nso")!;
        var kbp = ctx.GetProfile("microsoft-designer-keyboard")!;
        HMController? ds4 = null, nso = null, kb = null;
        HidReader? ds4r = null, nsor = null;
        try
        {
            ds4 = Open(ctx, ds4p, out ds4r);
            nso = Open(ctx, nsop, out nsor);
            var keyboardsBefore = new HashSet<IntPtr>(RawInputSink.Devices()
                .Where(d => d.Type == RawInputSink.RIM_TYPEKEYBOARD).Select(d => d.Handle));
            kb = ctx.CreateController(kbp);
            IntPtr keyboard = IntPtr.Zero;
            var wait = Stopwatch.StartNew();
            while (keyboard == IntPtr.Zero && wait.ElapsedMilliseconds < 15000)
            {
                keyboard = RawInputSink.Devices()
                    .Where(d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && !keyboardsBefore.Contains(d.Handle)
                                && d.Name.Contains("VID_045E&PID_0806", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.Handle).FirstOrDefault();
                if (keyboard == IntPtr.Zero) Thread.Sleep(100);
            }
            Check("microsoft-designer-keyboard: its keyboard collection reaches Raw Input", keyboard != IntPtr.Zero);
            if (ds4r == null || nsor == null || keyboard == IntPtr.Zero) return;
            Console.WriteLine($"  [INFO] driver {ds4.DriverVersion?.ToString() ?? "(not reported)"}");
            Thread.Sleep(500);

            var ds4State = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(ds4p, 1.0f, 0.5f, 0.5f, 0.5f, 0f, 0f) };
            var nsoState = new HMGamepadState { Buttons = HMButton.A };
            // Report 0x01's data: the modifier byte, then the ten key slots.
            var f24 = new byte[11];
            f24[1] = 0x73;
            var none = new byte[11];
            sink.Clear();
            bool ds4Ok = false, nsoOk = false, keyOk = false;
            int seen21 = 0;
            long nextKey = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 4000 && !(ds4Ok && nsoOk && keyOk))
            {
                ds4.SubmitState(ds4State);
                nso.SubmitState(nsoState);
                if (!keyOk && sw.ElapsedMilliseconds >= nextKey)
                {
                    kb.SubmitRawReport(f24);
                    kb.SubmitRawReport(none);
                    nextKey = sw.ElapsedMilliseconds + 500;
                }
                for (int i = 0; i < 4; i++)
                {
                    var r = ds4r.Read(10);
                    if (r != null && r.Length >= 2 && r[0] == 0x01 && r[1] == 0xFF) ds4Ok = true;
                    r = nsor.Read(10);
                    if (r != null && r.Length > 0 && r[0] == 0x21) seen21++;
                    if (r != null && r.Length >= 3 && r[0] == 0x3F && (r[1] | r[2]) != 0) nsoOk = true;
                }
                keyOk |= sink.Keys.Any(k => k.Device == keyboard && k.VKey == 0x87 && (k.Flags & 1) == 0);
            }
            Check("dualshock-4-v2, the control, in the same window: report 0x01 with left stick X 0xFF", ds4Ok);
            Check("genesis-nso: report 0x3F with the button pressed", nsoOk);
            Check("genesis-nso: no report 0x21 arrived, which the driver sent before InputReportId", seen21 == 0, $"{seen21} seen");
            Check("microsoft-designer-keyboard: a data-only frame arrives as its keyboard report 0x01 (F24 pressed)", keyOk);
        }
        finally
        {
            ds4r?.Dispose();
            nsor?.Dispose();
            kb?.Dispose();
            nso?.Dispose();
            ds4?.Dispose();
            Thread.Sleep(500);
        }
    }

    /// <summary>Create a controller and open a reader on its new HID
    /// interface, or leave the reader null after a failed check.</summary>
    static HMController Open(HMContext ctx, HMProfile prof, out HidReader? reader)
    {
        reader = null;
        var before = Present(prof.VendorId, prof.ProductId);
        var c = ctx.CreateController(prof);
        var dev = WaitForNew(prof.VendorId, prof.ProductId, before, 15000);
        Check($"{prof.Id}: its HID interface appears", dev != null, dev?.DevicePath ?? "");
        if (dev == null) return c;
        var rd = new HidReader(dev.DevicePath, Math.Max((int)dev.InputReportByteLength, 64));
        Check($"{prof.Id}: the interface opens for reading", rd.IsOpen, rd.OpenError);
        if (rd.IsOpen) reader = rd;
        else rd.Dispose();
        return c;
    }

    delegate (HMGamepadState State, Func<byte[], bool> Match, string What) Plan(HMProfile p, HMController c);

    static void Persona(HMContext ctx, string id, Plan plan)
    {
        var prof = ctx.GetProfile(id) ?? throw new InvalidOperationException($"no profile {id}");
        ushort vid = prof.VendorId, pid = prof.ProductId;
        var before = Present(vid, pid);
        HMController? c = null;
        try
        {
            c = ctx.CreateController(prof);
            var dev = WaitForNew(vid, pid, before, 15000);
            Check($"{id}: its HID interface appears", dev != null, dev?.DevicePath ?? "");
            if (dev == null) return;
            Console.WriteLine($"  [INFO] {id}: driver {c.DriverVersion?.ToString() ?? "(not reported)"}, " +
                              $"InputReportByteLength {dev.InputReportByteLength}");
            using var rd = new HidReader(dev.DevicePath, Math.Max((int)dev.InputReportByteLength, 64));
            Check($"{id}: the interface opens for reading", rd.IsOpen, rd.OpenError);
            if (!rd.IsOpen) return;
            var (st, match, what) = plan(prof, c);
            byte[]? last = null;
            var got = DriveUntil(c, rd, st, r => { last = r; return match(r); }, 3000);
            Check($"{id}: {what}", got != null,
                  got != null ? Convert.ToHexString(got.AsSpan(0, Math.Min(got.Length, 16)))
                              : last == null ? "no report" : "last " + Convert.ToHexString(last.AsSpan(0, Math.Min(last.Length, 16))) + $" ({last.Length} bytes)");
        }
        finally
        {
            c?.Dispose();
            Thread.Sleep(500);
        }
    }

    static byte[]? DriveUntil(HMController c, HidReader rd, HMGamepadState st, Func<byte[], bool> match, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            c.SubmitState(st);
            long until = sw.ElapsedMilliseconds + 80;
            while (sw.ElapsedMilliseconds < until)
            {
                var r = rd.Read(40);
                if (r == null) break;
                if (match(r)) return r;
            }
        }
        return null;
    }

    static HashSet<string> Present(ushort vid, ushort pid)
        => new(HidDeviceEnumerator.Enumerate()
                   .Where(d => d.VendorId == vid && d.ProductId == pid)
                   .Select(d => d.DevicePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>The first matching HID interface that was not present
    /// before the create. A real pad shares the VID and PID, so taking the
    /// first match could open the bench's own hardware.</summary>
    static HidDeviceEnumerator.RawHidDevice? WaitForNew(ushort vid, ushort pid, HashSet<string> before, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var d = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(x => x.VendorId == vid && x.ProductId == pid && !before.Contains(x.DevicePath));
            if (d != null) return d;
            Thread.Sleep(100);
        }
        return null;
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
    const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    /// <summary>Overlapped reads with a timeout, so a silent device fails a
    /// check instead of hanging the probe (ds4_report_check's reader).</summary>
    sealed class HidReader : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEventW(IntPtr sec, bool manualReset, bool initialState, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        const int ERROR_IO_PENDING = 997;
        readonly SafeFileHandle _h;
        readonly IntPtr _buf, _ov, _event;
        readonly int _len;

        public bool IsOpen => !_h.IsInvalid;
        public string OpenError { get; } = "";

        public HidReader(string path, int len)
        {
            _len = len;
            _h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING,
                             FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (_h.IsInvalid) OpenError = $"error {Marshal.GetLastWin32Error()}";
            _buf = Marshal.AllocHGlobal(len);
            _ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _event = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        }

        public byte[]? Read(int timeoutMs)
        {
            if (!IsOpen) return null;
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _event }, _ov, false);
            ResetEvent(_event);
            if (!ReadFile(_h, _buf, _len, IntPtr.Zero, _ov))
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                if (WaitForSingleObject(_event, (uint)Math.Max(1, timeoutMs)) != 0)
                {
                    CancelIoEx(_h, _ov);
                    GetOverlappedResult(_h, _ov, out _, true);
                    return null;
                }
            }
            if (!GetOverlappedResult(_h, _ov, out int n, false) || n <= 0) return null;
            var r = new byte[n];
            Marshal.Copy(_buf, r, 0, n);
            return r;
        }

        public void Dispose()
        {
            _h.Dispose();
            Marshal.FreeHGlobal(_buf);
            Marshal.FreeHGlobal(_ov);
            if (_event != IntPtr.Zero) CloseHandle(_event);
        }
    }
}
