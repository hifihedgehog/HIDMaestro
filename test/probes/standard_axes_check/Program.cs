// standard_axes_check: HMGamepadStateHelpers.StandardAxes, and the report
// builder's StandardAxes, never write a trigger value onto an axis a stick
// holds. Through v2.2.0 a profile whose layout declares no triggers got its
// trigger values written at Z and Rz, the canonical trigger axes, which
// erased the right stick of every such profile whose right stick sits on
// Z and Rz: switch-pro-usb, dualshock-3, hori-horipad-switch, logitech-f310
// and logitech-f710.
//
//   offline            every catalog profile: each stick axis StandardAxes
//                      returns holds the stick's value, and the trigger
//                      values land only on axes no stick holds
//   live <backend>     the five, created on the backend: the right stick
//                      submitted hard right reads back hard right in the
//                      HID input report, on the presented descriptor's
//                      right stick X field, and the left stick at rest.
//                      switch-pro-usb sends no report before a host speaks
//                      its protocol, so it is read through SDL instead
//   sdl <backend> <dir> the three SDL drives (switch-pro-usb through its
//                      Switch driver, hori-horipad-switch through the same
//                      driver's input-only path, dualshock-3 through its PS3
//                      driver with SDL_JOYSTICK_HIDAPI_PS3 on): the right
//                      stick submitted hard right reads back on SDL's right
//                      stick X and the left stick at rest
//
// Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static readonly string[] Five = { "switch-pro-usb", "dualshock-3", "hori-horipad-switch", "logitech-f310", "logitech-f710" };

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "offline";
        Console.WriteLine($"=== standard_axes_check {mode} ===");
        try
        {
            switch (mode)
            {
                case "offline": Offline(); break;
                case "live" when args.Length >= 2 && Backend(args[1]) is { } b: Live(b); break;
                case "sdl" when args.Length >= 3 && Backend(args[1]) is { } b: Sdl(b, args[2]); break;
                default:
                    Console.WriteLine("usage: StandardAxesCheck offline | live <umdf2|usbip|auto> | sdl <umdf2|usbip|auto> <SDL3 dir>");
                    return 2;
            }
        }
        catch (Exception e) { Check("the run ends without an exception", false, e.ToString()); }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static HMBackend? Backend(string s) => s.ToLowerInvariant() switch
    {
        "umdf2" => HMBackend.Umdf2,
        "usbip" => HMBackend.Usbip,
        "auto" => HMContext.MachineBackend,
        _ => null,
    };

    // ── offline ──────────────────────────────────────────────────────────

    const float LX = 0.25f, LY = 0.75f, RX = 1.0f, RY = 0.125f, LT = 0.375f, RT = 0.625f;

    static void Offline()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var bad = new List<string>();
        var badBuilder = new List<string>();
        int profiles = 0, noTriggers = 0;
        foreach (var p in ctx.AllProfiles)
        {
            var sticks = p.Sticks;
            if (sticks.Count == 0) continue;
            profiles++;
            if (p.Triggers.Count == 0) noTriggers++;
            var axes = HMGamepadStateHelpers.StandardAxes(p, LX, LY, RX, RY, LT, RT);
            var want = new List<(HMAxis Axis, float Value)> { (sticks[0].XAxis, LX), (sticks[0].YAxis, LY) };
            if (sticks.Count > 1) { want.Add((sticks[1].XAxis, RX)); want.Add((sticks[1].YAxis, RY)); }
            foreach (var (axis, value) in want)
            {
                if (axis == HMAxis.None) continue;
                if (!axes.TryGetValue(axis, out float got) || got != value) { bad.Add($"{p.Id} {axis}={(axes.TryGetValue(axis, out got) ? got : float.NaN)}"); break; }
            }
            // The report builder's own helper, by the descriptor's slots.
            HidReportBuilder rb;
            try { rb = p.Inner.GetOrBuildReportBuilder(); } catch { continue; }
            var d = rb.StandardAxes(LX, LY, RX, RY, LT, RT);
            static HMAxis Key(HidReportBuilder.InputField f) => (HMAxis)((f.UsagePage << 8) | f.Usage);
            foreach (var (f, value) in new[] { (rb.LeftStickX, LX), (rb.LeftStickY, LY), (rb.RightStickX, RX), (rb.RightStickY, RY) })
            {
                if (f == null) continue;
                if (!d.TryGetValue(Key(f), out float got) || got != value) { badBuilder.Add($"{p.Id} {Key(f)}"); break; }
            }
        }
        Console.WriteLine($"  {profiles} profiles with a stick, {noTriggers} of them with no trigger in their layout");
        Check("HMGamepadStateHelpers.StandardAxes: every stick axis holds the stick's value", bad.Count == 0, string.Join("; ", bad));
        Check("HidReportBuilder.StandardAxes: every stick slot holds the stick's value", badBuilder.Count == 0, string.Join("; ", badBuilder));
        foreach (var id in Five)
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id}: in the catalog", false); continue; }
            var axes = HMGamepadStateHelpers.StandardAxes(p, LX, LY, RX, RY, LT, RT);
            var right = p.Sticks.Count > 1 ? p.Sticks[1] : null;
            Check($"{id}: no trigger in the layout, and the right stick on {right?.XAxis}/{right?.YAxis} keeps {RX}/{RY}",
                  p.Triggers.Count == 0 && right != null && axes.GetValueOrDefault(right.XAxis, -1) == RX && axes.GetValueOrDefault(right.YAxis, -1) == RY,
                  string.Join(" ", axes.Select(kv => $"{kv.Key}={kv.Value}")));
        }
    }

    // ── live, through the HID report ─────────────────────────────────────

    static void Live(HMBackend backend)
    {
        using var ctx = new HMContext(backend);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        foreach (var id in Five)
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id}: in the catalog", false); continue; }
            if (p.Inner.Protocol != null)
            {
                Console.WriteLine($"\n  {id}: sends no report before a host speaks its protocol, read through SDL (sdl mode)");
                continue;
            }
            Console.WriteLine($"\n  {id} on {backend}");
            var pre = Present(p.VendorId, p.ProductId);
            HMController? c = null;
            try
            {
                c = ctx.CreateController(p, null, backend);
                var form = p.FormOn(c.Backend);
                var rb = form.Inner.GetOrBuildReportBuilder();
                var dev = WaitForNew(p.VendorId, p.ProductId, pre, 15000);
                Check($"{id}: the HID interface appears", dev != null);
                if (dev == null || rb.RightStickX == null || rb.LeftStickX == null) { Check($"{id}: the descriptor has both sticks", rb.RightStickX != null && rb.LeftStickX != null); continue; }
                using var rd = new HidReader(dev.DevicePath, Math.Max((int)dev.InputReportByteLength, 64));
                Check($"{id}: the interface opens for reading", rd.IsOpen, rd.OpenError);
                if (!rd.IsOpen) continue;
                int idBits = rb.InputReportId != 0 ? 8 : 0;
                var st = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(p, 0.5f, 0.5f, 1.0f, 0.5f, 0f, 0f) };
                double Norm(byte[] r, HidReportBuilder.InputField f)
                {
                    long v = ReadBits(r, f.BitOffset + idBits, f.BitSize);
                    if (f.LogicalMin < 0 && (v & (1L << (f.BitSize - 1))) != 0) v -= 1L << f.BitSize;
                    return (double)(v - f.LogicalMin) / Math.Max(1, f.LogicalMax - f.LogicalMin);
                }
                byte[]? got = null;
                var sw = Stopwatch.StartNew();
                while (got == null && sw.ElapsedMilliseconds < 4000)
                {
                    c.SubmitState(st);
                    var r = rd.Read(60);
                    if (r != null && (rb.InputReportId == 0 || r[0] == rb.InputReportId) && Norm(r, rb.RightStickX) > 0.95) got = r;
                }
                string seen = got != null ? Convert.ToHexString(got.AsSpan(0, Math.Min(16, got.Length))) : "none";
                Check($"{id}: the right stick submitted hard right reads back hard right on {(HMAxis)((rb.RightStickX.UsagePage << 8) | rb.RightStickX.Usage)} in the HID report", got != null, seen);
                if (got != null)
                {
                    double lx = Norm(got, rb.LeftStickX), ry = rb.RightStickY != null ? Norm(got, rb.RightStickY) : 0.5;
                    Check($"{id}: the left stick X and the right stick Y read at rest", Math.Abs(lx - 0.5) < 0.02 && Math.Abs(ry - 0.5) < 0.02, $"LX {lx:F3} RY {ry:F3}");
                }
            }
            catch (Exception e) { Check($"{id}: ran without throwing", false, e.Message); }
            finally { c?.Dispose(); }
        }
        try { HMContext.RemoveAllVirtualControllers(preserveInstall: true); } catch { }
    }

    static long ReadBits(byte[] r, int bit, int count)
    {
        long v = 0;
        for (int i = 0; i < count; i++)
        {
            int b = bit + i;
            if ((b >> 3) < r.Length && ((r[b >> 3] >> (b & 7)) & 1) != 0) v |= 1L << i;
        }
        return v;
    }

    // ── live, through SDL ────────────────────────────────────────────────

    const string L = "SDL3";
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
    [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
    [DllImport(L)] static extern void SDL_Quit();
    [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetHint(string name, string value);
    [DllImport(L)] static extern void SDL_PumpEvents();
    [DllImport(L)] static extern IntPtr SDL_GetError();
    [DllImport(L)] static extern void SDL_free(IntPtr p);
    [DllImport(L)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(L)] static extern IntPtr SDL_GetJoystickPathForID(uint id);
    [DllImport(L)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(L)] static extern void SDL_CloseGamepad(IntPtr g);
    [DllImport(L)] static extern IntPtr SDL_GetGamepadName(IntPtr g);
    [DllImport(L)] static extern short SDL_GetGamepadAxis(IntPtr g, int axis);
    const uint SDL_INIT_GAMEPAD = 0x00002000;
    const int AXIS_LEFTX = 0, AXIS_LEFTY = 1, AXIS_RIGHTX = 2, AXIS_RIGHTY = 3;

    static readonly string[] SdlDrives = { "switch-pro-usb", "hori-horipad-switch", "dualshock-3" };

    static void Sdl(HMBackend backend, string dir)
    {
        dir = Path.GetFullPath(dir);
        if (!File.Exists(Path.Combine(dir, "SDL3.dll"))) { Check($"SDL3.dll in {dir}", false); return; }
        SetDllDirectoryW(dir);
        using var ctx = new HMContext(backend);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        // SDL's PS3 driver is off on Windows unless a program turns it on
        // (SDL_hidapi_ps3.c HIDAPI_DriverPS3_IsEnabled).
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS3", "1");
        foreach (var id in SdlDrives)
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id}: in the catalog", false); continue; }
            Console.WriteLine($"\n  {id} on {backend}");
            var pre = Present(p.VendorId, p.ProductId);
            HMController? c = null;
            var stop = false;
            var state = new HMGamepadState();
            var gate = new object();
            Thread? pump = null;
            bool inited = false;
            IntPtr pad = IntPtr.Zero;
            try
            {
                c = ctx.CreateController(p, null, backend);
                var ctrl = c;
                pump = new Thread(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        HMGamepadState s;
                        lock (gate) s = state;
                        try { ctrl.SubmitState(s); } catch { }
                        Thread.Sleep(8);
                    }
                }) { IsBackground = true };
                pump.Start();
                var dev = WaitForNew(p.VendorId, p.ProductId, pre, 15000);
                Check($"{id}: the HID interface appears", dev != null);
                if (dev == null) continue;
                inited = SDL_Init(SDL_INIT_GAMEPAD);
                Check($"{id}: SDL_Init(GAMEPAD)", inited);
                pad = OpenByPath(dev.DevicePath, 20000);
                Check($"{id}: SDL opens the pad", pad != IntPtr.Zero, pad == IntPtr.Zero ? Marshal.PtrToStringUTF8(SDL_GetError()) ?? "" : Marshal.PtrToStringUTF8(SDL_GetGamepadName(pad)) ?? "");
                if (pad == IntPtr.Zero) continue;
                lock (gate) state = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(p, 0.5f, 0.5f, 1.0f, 0.5f, 0f, 0f) };
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000) { SDL_PumpEvents(); if (SDL_GetGamepadAxis(pad, AXIS_RIGHTX) > 25000) break; Thread.Sleep(10); }
                SDL_PumpEvents();
                short rx = SDL_GetGamepadAxis(pad, AXIS_RIGHTX), ry = SDL_GetGamepadAxis(pad, AXIS_RIGHTY);
                short lx = SDL_GetGamepadAxis(pad, AXIS_LEFTX), ly = SDL_GetGamepadAxis(pad, AXIS_LEFTY);
                string now = $"LX {lx} LY {ly} RX {rx} RY {ry}";
                Check($"{id}: the right stick submitted hard right reads back on SDL's right stick X", rx > 25000, now);
                Check($"{id}: the right stick Y and the left stick read at rest", Math.Abs((int)ry) < 8000 && Math.Abs((int)lx) < 8000 && Math.Abs((int)ly) < 8000, now);
            }
            catch (Exception e) { Check($"{id}: ran without throwing", false, e.Message); }
            finally
            {
                if (pad != IntPtr.Zero) SDL_CloseGamepad(pad);
                if (inited) SDL_Quit();
                Volatile.Write(ref stop, true);
                pump?.Join(1000);
                c?.Dispose();
            }
        }
        try { HMContext.RemoveAllVirtualControllers(preserveInstall: true); } catch { }
    }

    static IntPtr OpenByPath(string path, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            SDL_PumpEvents();
            IntPtr list = SDL_GetJoysticks(out int n);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    uint id = (uint)Marshal.ReadInt32(list, i * 4);
                    if (string.Equals(Marshal.PtrToStringUTF8(SDL_GetJoystickPathForID(id)), path, StringComparison.OrdinalIgnoreCase))
                    {
                        IntPtr g = SDL_OpenGamepad(id);
                        if (g != IntPtr.Zero) return g;
                    }
                }
            }
            finally { if (list != IntPtr.Zero) SDL_free(list); }
            Thread.Sleep(50);
        }
        return IntPtr.Zero;
    }

    // ── HID helpers ──────────────────────────────────────────────────────

    static HashSet<string> Present(ushort vid, ushort pid)
        => new(HidDeviceEnumerator.Enumerate().Where(d => d.VendorId == vid && d.ProductId == pid).Select(d => d.DevicePath),
               StringComparer.OrdinalIgnoreCase);

    /// <summary>The first matching HID interface that was not present
    /// before the create, so a real pad of the same VID and PID is never
    /// opened.</summary>
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
    /// check instead of hanging the probe.</summary>
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
