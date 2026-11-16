// SInput persona check (issue #75): the sinput and joypad-steam-input
// profiles answer SDL's features command and decode the host's output
// report 0x03.
//
// Phase 1, raw HID, for each profile: SDL's RetrieveSDLFeatures exchange
// (SDL_hidapi_sinput.c:559-609 at PadForge's fork, 91e1a5afa3): the 48-byte
// output report 03 02, then reads until a 64-byte input report that starts
// 02 02, while a pump submits frames the way PadForge does. Every byte of
// the reply is checked against what the profile's references build: the
// sinput reply against SINPUT-LIB-HID 8dd1e5a (sinput_lib_protocol.c
// :271-366) and SDL's parser (SDL_hidapi_sinput.c:437-557), with the
// issue's flags (rumble, motion, sticks, triggers and touchpads, player LEDs
// and RGB clear), the joypad-steam-input reply against joypad-os 9fa79f45
// with no pad attached (sinput_mode.c:709-830). Then each output command
// against OutputDecoded: type 2 haptics (SDL_hidapi_sinput.c:766-788), type 1
// haptics on sinput (sinput_lib_protocol.c:216-261, reduced as OpenPuck's
// mode_sinput.cpp:292-299 reduces it) and, on joypad-steam-input, the RGB
// write joypad-os handles (sinput_mode.c:621-631). The features command, a
// player LED write, an RGB write on sinput and a type 1 write on
// joypad-steam-input decode nothing. Each write carries a tag in its last
// byte, which no rule or decoder reads, so a decode names the write behind
// it, and a silence is read once a later write's decode has arrived.
// Decodes without a tag, from another program writing to the pad, are
// skipped.
//
// Phase 2, SDL: the unfiltered SDL3.dll built from PadForge's fork at
// 91e1a5afa3 (SDL3-build/build-unfiltered-91e1a5a/Release) opens two
// sinput pads through its HIDAPI SInput driver. Checks their name, type,
// serials, sensors, touchpads and capabilities, a raw frame's South button
// and left stick, SDL_RumbleGamepad decoded on its own pad, SDL_SetGamepadLED
// refused, and every output report SDL wrote: features and haptics only,
// never a player LED or a color.
//
// SKIPs phase 2 when that SDL3.dll is absent, and everything when not
// elevated. Exit 0 PASS, 1 FAIL.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;

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

    // ── raw HID I/O, overlapped with hang bounds ───────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, uint n);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint n, IntPtr read, ref NativeOverlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint n, IntPtr written, ref NativeOverlapped ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResultEx(SafeFileHandle h, ref NativeOverlapped ov,
        out uint transferred, uint timeoutMs, bool alertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attrs, bool manual, bool initial, IntPtr name);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    // Both descriptors declare output report 0x03 as 47 bytes after its ID,
    // the 48 SDL writes (SINPUT_DEVICE_REPORT_COMMAND_SIZE).
    const int CommandSize = 48;

    static SafeFileHandle s_hid = null!;
    static IntPtr s_readEvent, s_writeEvent;
    static int s_lastReadLen;

    // Writes the 48-byte report with `tag` in its last byte.
    static bool HidWrite(byte[] report, byte tag)
    {
        var buf = GC.AllocateArray<byte>(CommandSize, pinned: true);
        Array.Copy(report, buf, Math.Min(report.Length, CommandSize));
        buf[CommandSize - 1] = tag;
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, buf, CommandSize, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return false;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 2000, false))
        {
            CancelAndWait(ref ov);
            return false;
        }
        return written == CommandSize;
    }

    // A canceled read or write still completes into its OVERLAPPED and
    // buffer, so this waits for that before the caller's frame returns.
    static void CancelAndWait(ref NativeOverlapped ov)
    {
        CancelIoEx(s_hid, IntPtr.Zero);
        GetOverlappedResultEx(s_hid, ref ov, out _, 0xFFFFFFFF, false);
    }

    static byte[]? HidRead(int timeoutMs)
    {
        var buf = GC.AllocateArray<byte>(512, pinned: true);
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, 512, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelAndWait(ref ov);
            return null;
        }
        s_lastReadLen = (int)read;
        return read > 0 ? buf : null;
    }

    static string Hex(byte[] b, int n) => string.Join(" ", b.Take(n).Select(x => x.ToString("X2")));

    // ── decoded output ─────────────────────────────────────────────────

    // Tag is the report's last byte: this probe's writes carry 0xA1 to 0xAF
    // there, SDL's carry 0.
    sealed record Decoded(byte ReportId, byte Tag, Dictionary<string, byte> Fields)
    {
        public override string ToString() =>
            $"report {ReportId:X2} tag {Tag:X2}: " + string.Join(", ", Fields.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
    }

    static bool Mine(byte tag) => tag >= 0xA1 && tag <= 0xAF;

    static void Watch(HMController c, ConcurrentQueue<Decoded> q) =>
        c.OutputDecoded += (_, e) => q.Enqueue(new Decoded(e.ReportId,
            e.RawBytes.Length >= CommandSize ? e.RawBytes.Span[CommandSize - 1] : (byte)0,
            e.Fields.ToDictionary(f => f.Key, f => Convert.ToByte(f.Value))));

    // The decode of the write tagged `tag`, or null when none came within
    // the hang bound. Decodes of this probe's other writes that came first
    // go to `early`. Decodes of other programs' writes are skipped.
    static Decoded? DecodeOf(ConcurrentQueue<Decoded> q, byte tag, List<Decoded> early, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!q.TryDequeue(out var d)) { Thread.Sleep(2); continue; }
            if (d.Tag == tag) return d;
            if (Mine(d.Tag)) early.Add(d);
        }
        return null;
    }

    static bool Same(Decoded? d, params (string Key, byte Value)[] want) =>
        d != null && d.ReportId == 0x03 && d.Fields.Count == want.Length
        && want.All(w => d.Fields.TryGetValue(w.Key, out byte v) && v == w.Value);

    static string Detail(Decoded? d, List<Decoded> early) =>
        (d?.ToString() ?? "no decode") + (early.Count > 0 ? "; decoded first: " + string.Join("; ", early) : "");

    // ── the expected replies ───────────────────────────────────────────

    // sinput: SINPUT-LIB-HID's builder with the issue's configuration.
    // Bytes 0-19, then the serial: 2E 8A 10 C6 00 and the transport index.
    static readonly byte[] SinputHead =
    {
        0x02, 0x02, 0x01, 0x00, 0xFD, 0x01, 0x01, 0x20, 0xE8, 0x03,
        0x08, 0x00, 0xD0, 0x07, 0xFF, 0xFF, 0xFF, 0x00, 0x02, 0x01,
        0x2E, 0x8A, 0x10, 0xC6, 0x00,
    };

    // joypad-steam-input: joypad-os with its defaults, the version written
    // 00 01. The serial: 4A 4F 59 00 00 and the transport index.
    static readonly byte[] JoypadHead =
    {
        0x02, 0x02, 0x00, 0x01, 0xF3, 0x02, 0x01, 0x20, 0xE8, 0x03,
        0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x0F, 0x06, 0x00, 0x00,
        0x4A, 0x4F, 0x59, 0x00, 0x00,
    };

    static byte[] Expected(byte[] head, int index)
    {
        var r = new byte[64];
        Array.Copy(head, r, head.Length);
        r[25] = (byte)index;
        return r;
    }

    // ── the pad's input frame ──────────────────────────────────────────

    // Report 0x01 after its ID, 63 bytes (SDL_hidapi_sinput.c:66-88, which
    // counts the ID as byte 0): buttons at 3-6, sticks and triggers as int16
    // at 7-18, the IMU timestamp at 19-22. South is bit 0 of byte 3
    // (sinput_lib_protocol.c:89), which SDL sends as joystick button 0
    // (SDL_hidapi_sinput.c:848-868) and maps to "a" (SDL_gamepad.c:967).
    static byte[] Frame(bool south, short leftX, uint timestamp)
    {
        var d = new byte[63];
        if (south) d[3 - 1] |= 1 << 0;
        BitConverter.TryWriteBytes(d.AsSpan(7 - 1), leftX);
        BitConverter.TryWriteBytes(d.AsSpan(15 - 1), (short)-32768);   // triggers at rest
        BitConverter.TryWriteBytes(d.AsSpan(17 - 1), (short)-32768);
        BitConverter.TryWriteBytes(d.AsSpan(19 - 1), timestamp);
        return d;
    }

    // A pump per controller submits the current frame every 4 ms with the
    // IMU timestamp advancing 4000 us, as a consumer's engine tick does.
    sealed class Pump : IDisposable
    {
        readonly Thread _t;
        volatile bool _stop;
        public volatile bool South;
        public short LeftX;

        public Pump(HMController c)
        {
            _t = new Thread(() =>
            {
                uint ts = 1;
                while (!_stop)
                {
                    try { c.SubmitRawReport(Frame(South, Volatile.Read(ref LeftX), ts)); } catch (ObjectDisposedException) { return; }
                    ts += 4000;
                    Thread.Sleep(4);
                }
            }) { IsBackground = true };
            _t.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _t.Join(2000);
        }
    }

    static string? PathFor(HMController c)
    {
        string serial = $"HM-CTL-{c.TransportIndex:D4}";
        for (int i = 0; i < 300; i++)
        {
            string? p = HMDeviceExtractor.ListDevices()
                .FirstOrDefault(d => d.VendorId == 0x2E8A && d.ProductId == 0x10C6 && d.SerialNumberString == serial)?.DevicePath;
            if (p != null) return p;
            Thread.Sleep(100);
        }
        return null;
    }

    static int Main()
    {
        Console.WriteLine("=== SInput persona check (issue #75) ===");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (requires elevation, as CreateController does)");
                return 0;
            }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);

        RawPhase(ctx, "sinput", SinputHead, type1: true, rgb: false);
        RawPhase(ctx, "joypad-steam-input", JoypadHead, type1: false, rgb: true);
        SdlPhase(ctx);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── phase 1 ────────────────────────────────────────────────────────

    static void RawPhase(HMContext ctx, string id, byte[] head, bool type1, bool rgb)
    {
        Console.WriteLine($"\n-- Phase 1, {id}: SDL's features exchange and the output decode, over raw HID --");
        var profile = ctx.GetProfile(id);
        Check($"{id} is in the catalog", profile != null);
        if (profile == null) return;
        using var c = ctx.CreateController(profile);
        var q = new ConcurrentQueue<Decoded>();
        Watch(c, q);
        using var pump = new Pump(c);

        string? path = PathFor(c);
        Check($"HID interface enumerates (2E8A:10C6, serial HM-CTL-{c.TransportIndex:D4})", path != null, path ?? "");
        if (path == null) return;
        s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        Check("CreateFile read/write", !s_hid.IsInvalid);
        if (s_hid.IsInvalid) return;
        using var hid = s_hid;
        HidD_SetNumInputBuffers(s_hid, 512);

        // SDL's write: report 0x03, command 0x02, zeros to 48 bytes.
        var features = new byte[CommandSize];
        features[0] = 0x03; features[1] = 0x02;
        var sw = Stopwatch.StartNew();
        Check("features command written (48 bytes)", HidWrite(features, 0xA1));
        // SDL takes the first 64-byte report that starts 02 02 and skips the
        // rest, the pump's frames among them.
        byte[]? reply = null;
        int skipped = 0;
        var other = new List<string>();
        while (reply == null && sw.ElapsedMilliseconds < 3000)
        {
            var r = HidRead(Math.Max(1, (int)(3000 - sw.ElapsedMilliseconds)));
            if (r == null) continue;
            if (s_lastReadLen == 64 && r[0] == 0x02 && r[1] == 0x02) { reply = r; continue; }
            skipped++;
            if (s_lastReadLen != 64 || r[0] != 0x01) other.Add($"{r[0]:X2}/{s_lastReadLen}");
        }
        Check("features reply arrives (64 bytes, 02 02)", reply != null,
              reply == null ? "" : $"{sw.ElapsedMilliseconds} ms after the write, {skipped} pump frames skipped");
        // The pump's data-only frames go out under the report the layout
        // builder fills, the first with a Generic Desktop, Simulation or
        // Button field (HidReportBuilder.cs:620-648): the pad's report 0x01,
        // which SDL reads as state (SDL_hidapi_sinput.c:1076), on both
        // descriptors, joypad-os's listing vendor report 0x02 first.
        Check("the pump's frames arrive as input report 0x01, 64 bytes", skipped > 0 && other.Count == 0,
              other.Count == 0 ? $"{skipped} frames" : "also " + string.Join(", ", other.Distinct()));
        if (reply == null) return;
        var want = Expected(head, c.TransportIndex);
        var diffs = Enumerable.Range(0, 64).Where(i => reply[i] != want[i]).ToList();
        Check($"reply bytes 0-63 are the reference's, byte 25 the transport index {c.TransportIndex}", diffs.Count == 0,
              diffs.Count == 0 ? Hex(reply, 26) : string.Join(", ", diffs.Select(i => $"byte {i}: {reply[i]:X2} where {want[i]:X2}")));

        var early = new List<Decoded>();
        var erm = new byte[CommandSize];
        erm[0] = 0x03; erm[1] = 0x01; erm[2] = 0x02;
        erm[3] = 0x80; erm[4] = 0x01; erm[5] = 0x40; erm[6] = 0x00;
        Check("type 2 haptics written", HidWrite(erm, 0xA2));
        var d = DecodeOf(q, 0xA2, early);
        Check("the features command decodes nothing, type 2 decodes leftMotor 128 and rightMotor 64 (bytes 3 and 5)",
              Same(d, ("leftMotor", 0x80), ("rightMotor", 0x40)) && early.Count == 0, Detail(d, early));

        var amp = new byte[CommandSize];
        amp[0] = 0x03; amp[1] = 0x01; amp[2] = 0x01;
        void U16(int at, ushort v) => BitConverter.TryWriteBytes(amp.AsSpan(at), v);
        U16(3, 160); U16(5, 0x1234); U16(7, 320); U16(9, 0xAB00);    // left: frequency, amplitude, frequency, amplitude
        U16(11, 160); U16(13, 0x7F00); U16(15, 320); U16(17, 0x0100); // right
        Check("type 1 haptics written", HidWrite(amp, 0xA3));
        if (type1)
        {
            early.Clear();
            d = DecodeOf(q, 0xA3, early);
            Check("type 1 decodes the louder amplitude's high byte per side (leftMotor 0xAB, rightMotor 0x7F)",
                  Same(d, ("leftMotor", 0xAB), ("rightMotor", 0x7F)) && early.Count == 0, Detail(d, early));
        }

        var color = new byte[CommandSize];
        color[0] = 0x03; color[1] = 0x04; color[2] = 10; color[3] = 20; color[4] = 30;
        Check("RGB written", HidWrite(color, 0xA4));
        if (rgb)
        {
            // joypad-os handles type 2 only (sinput_mode.c:586-603), so the
            // type 1 write decodes nothing and the RGB write's decode is next.
            early.Clear();
            d = DecodeOf(q, 0xA4, early);
            Check("type 1 decodes nothing, RGB decodes lightbarRed 10, lightbarGreen 20 and lightbarBlue 30 (bytes 2 to 4)",
                  Same(d, ("lightbarRed", 10), ("lightbarGreen", 20), ("lightbarBlue", 30)) && early.Count == 0, Detail(d, early));
        }

        var led = new byte[CommandSize];
        led[0] = 0x03; led[1] = 0x03; led[2] = 0x01;
        Check("player LED written", HidWrite(led, 0xA5));
        erm[3] = 0x11; erm[5] = 0x22;
        Check("type 2 haptics written again", HidWrite(erm, 0xA6));
        early.Clear();
        d = DecodeOf(q, 0xA6, early);
        Check(rgb ? "the player LED decodes nothing, the next type 2 decodes leftMotor 17 and rightMotor 34"
                  : "RGB and the player LED decode nothing, the next type 2 decodes leftMotor 17 and rightMotor 34",
              Same(d, ("leftMotor", 0x11), ("rightMotor", 0x22)) && early.Count == 0, Detail(d, early));
        var late = q.Where(x => Mine(x.Tag)).ToList();
        Check("no other decode of this probe's writes", late.Count == 0, string.Join("; ", late));
    }

    // ── phase 2 ────────────────────────────────────────────────────────

    const string SDL = "SDL3.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectoryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);

    // SDL3's bool is one byte (SDL_stdinc.h), and the x64 convention leaves
    // the rest of the return register undefined, so each bool is marshaled
    // as U1.
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern void SDL_PumpEvents();
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint instanceId);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadSerial(IntPtr gamepad);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadPath(IntPtr gamepad);
    [DllImport(SDL)] static extern int SDL_GetGamepadType(IntPtr gamepad);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GamepadHasSensor(IntPtr gamepad, int type);
    [DllImport(SDL)] static extern int SDL_GetNumGamepadTouchpads(IntPtr gamepad);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_RumbleGamepad(IntPtr gamepad, ushort low, ushort high, uint ms);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetGamepadLED(IntPtr gamepad, byte r, byte g, byte b);
    [DllImport(SDL)] static extern uint SDL_GetGamepadProperties(IntPtr gamepad);
    [DllImport(SDL)] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SDL_GetBooleanProperty(uint props, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.U1)] bool defaultValue);
    [DllImport(SDL)] static extern IntPtr SDL_GetError();

    const uint SDL_INIT_GAMEPAD = 0x00002000;
    const int SDL_GAMEPAD_TYPE_STANDARD = 1, SDL_GAMEPAD_BUTTON_SOUTH = 0, SDL_GAMEPAD_BUTTON_EAST = 1, SDL_GAMEPAD_AXIS_LEFTX = 0;
    const int SDL_SENSOR_ACCEL = 1, SDL_SENSOR_GYRO = 2;

    static string Utf8(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";

    // The command byte and the byte after it of every output report 0x03 a
    // controller receives, from OutputReceived.
    static void Capture(HMController c, ConcurrentQueue<(byte Command, byte Sub)> wire) =>
        c.OutputReceived += (_, p) =>
        {
            if (p.Source == HMOutputSource.HidOutput && p.ReportId == 0x03 && p.Data.Length >= 2)
                wire.Enqueue((p.Data.Span[0], p.Data.Span[1]));
        };

    // Pumps SDL until the queue yields a decode with these motor values,
    // skipping any other, or returns null at the hang bound.
    static Decoded? MotorDecode(ConcurrentQueue<Decoded> q, byte left, byte right)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            SDL_PumpEvents();
            if (q.TryDequeue(out var x))
            {
                if (Same(x, ("leftMotor", left), ("rightMotor", right))) return x;
                continue;
            }
            Thread.Sleep(5);
        }
        return null;
    }

    static void SdlPhase(HMContext ctx)
    {
        Console.WriteLine("\n-- Phase 2: PadForge's SDL fork (91e1a5afa3, unfiltered) opens two sinput pads --");
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string sdlDir = Path.GetFullPath(Path.Combine(repoRoot, "..", "SDL3-build", "build-unfiltered-91e1a5a", "Release"));
        if (!File.Exists(Path.Combine(sdlDir, SDL)))
        {
            Console.WriteLine($"  [SKIP] phase 2: {sdlDir}\\{SDL} not found");
            return;
        }
        string libusb = Path.Combine(sdlDir, "libusb-1.0.dll");
        bool libusbLoaded = File.Exists(libusb) && LoadLibraryW(libusb) != IntPtr.Zero;
        SetDllDirectoryW(sdlDir);
        Console.WriteLine($"  SDL3: {sdlDir}\\{SDL} (libusb {(libusbLoaded ? "preloaded" : "MISSING")})");

        var profile = ctx.GetProfile("sinput")!;
        using var a = ctx.CreateController(profile);
        using var b = ctx.CreateController(profile);
        var qa = new ConcurrentQueue<Decoded>();
        var qb = new ConcurrentQueue<Decoded>();
        var wireA = new ConcurrentQueue<(byte Command, byte Sub)>();
        var wireB = new ConcurrentQueue<(byte Command, byte Sub)>();
        Watch(a, qa);
        Watch(b, qb);
        Capture(a, wireA);
        Capture(b, wireB);
        using var pa = new Pump(a);
        using var pb = new Pump(b);
        string? pathA = PathFor(a), pathB = PathFor(b);
        Check("both pads' HID interfaces enumerate", pathA != null && pathB != null);
        if (pathA == null || pathB == null) return;

        Check("SDL_Init(GAMEPAD)", SDL_Init(SDL_INIT_GAMEPAD), Utf8(SDL_GetError()));
        IntPtr padA = IntPtr.Zero, padB = IntPtr.Zero;
        try
        {
            // SDL opens a pad only after RetrieveSDLFeatures succeeds
            // (SDL_hidapi_sinput.c:659-661). The bound only detects a hang.
            var seen = new HashSet<uint>();
            var sw = Stopwatch.StartNew();
            while ((padA == IntPtr.Zero || padB == IntPtr.Zero) && sw.ElapsedMilliseconds < 60000)
            {
                SDL_PumpEvents();
                IntPtr list = SDL_GetGamepads(out int n);
                if (list != IntPtr.Zero)
                {
                    for (int i = 0; i < n; i++)
                    {
                        uint id = (uint)Marshal.ReadInt32(list, i * 4);
                        if (!seen.Add(id)) continue;
                        IntPtr cand = SDL_OpenGamepad(id);
                        if (cand == IntPtr.Zero) continue;
                        string p = Utf8(SDL_GetGamepadPath(cand));
                        if (p.Equals(pathA, StringComparison.OrdinalIgnoreCase)) padA = cand;
                        else if (p.Equals(pathB, StringComparison.OrdinalIgnoreCase)) padB = cand;
                        else SDL_CloseGamepad(cand);
                    }
                    SDL_free(list);
                }
                if (padA == IntPtr.Zero || padB == IntPtr.Zero) Thread.Sleep(50);
            }
            Check("SDL opens both pads (its SInput driver's init answered)", padA != IntPtr.Zero && padB != IntPtr.Zero,
                  $"after {sw.ElapsedMilliseconds} ms");
            if (padA == IntPtr.Zero || padB == IntPtr.Zero) return;

            string nameA = Utf8(SDL_GetGamepadName(padA));
            Check("name is the product string", nameA.Contains("SInput Gamepad", StringComparison.Ordinal), $"'{nameA}'");
            Check("type is SDL_GAMEPAD_TYPE_STANDARD (reply byte 6)", SDL_GetGamepadType(padA) == SDL_GAMEPAD_TYPE_STANDARD,
                  $"{SDL_GetGamepadType(padA)}");
            string serialA = Utf8(SDL_GetGamepadSerial(padA)), serialB = Utf8(SDL_GetGamepadSerial(padB));
            Check("serials are the reply's bytes 20-25 with each pad's index",
                  serialA == $"2e-8a-10-c6-00-{a.TransportIndex:x2}" && serialB == $"2e-8a-10-c6-00-{b.TransportIndex:x2}",
                  $"'{serialA}' and '{serialB}'");
            Check("accelerometer and gyroscope (reply byte 4)",
                  SDL_GamepadHasSensor(padA, SDL_SENSOR_ACCEL) && SDL_GamepadHasSensor(padA, SDL_SENSOR_GYRO));
            Check("two touchpads (reply bytes 18-19)", SDL_GetNumGamepadTouchpads(padA) == 2, $"{SDL_GetNumGamepadTouchpads(padA)}");
            // SDL fills these from the driver's flags at open
            // (SDL_hidapijoystick.c:1040-1070, SDL_hidapi_sinput.c:790-808).
            uint props = SDL_GetGamepadProperties(padA);
            bool rumbleCap = SDL_GetBooleanProperty(props, "SDL.joystick.cap.rumble", false);
            bool playerLedCap = SDL_GetBooleanProperty(props, "SDL.joystick.cap.player_led", true);
            bool rgbCap = SDL_GetBooleanProperty(props, "SDL.joystick.cap.rgb_led", true);
            Check("capabilities: rumble, no player LED, no RGB LED (reply bytes 4 and 5)", rumbleCap && !playerLedCap && !rgbCap,
                  $"rumble {rumbleCap}, player LED {playerLedCap}, RGB LED {rgbCap}");

            // A raw frame on pad A: South held, left stick full right.
            Volatile.Write(ref pa.LeftX, (short)32767);
            pa.South = true;
            bool south = false, axis = false;
            sw.Restart();
            while ((!south || !axis) && sw.ElapsedMilliseconds < 3000)
            {
                SDL_PumpEvents();
                south = SDL_GetGamepadButton(padA, SDL_GAMEPAD_BUTTON_SOUTH);
                axis = SDL_GetGamepadAxis(padA, SDL_GAMEPAD_AXIS_LEFTX) > 30000;
                if (!south || !axis) Thread.Sleep(5);
            }
            Check("pad A reads South and the left stick from the submitted frame", south && axis,
                  $"South {south}, left X {SDL_GetGamepadAxis(padA, SDL_GAMEPAD_AXIS_LEFTX)}");
            SDL_PumpEvents();
            Check("pad A's East stays up, so the bit reads as South alone", !SDL_GetGamepadButton(padA, SDL_GAMEPAD_BUTTON_EAST));
            Check("pad B's South stays up", !SDL_GetGamepadButton(padB, SDL_GAMEPAD_BUTTON_SOUTH));

            Check("SDL_RumbleGamepad", SDL_RumbleGamepad(padA, 0x8000, 0x4000, 1000), Utf8(SDL_GetError()));
            var d = MotorDecode(qa, 0x80, 0x40);
            Check("rumble decodes on pad A: leftMotor 128, rightMotor 64", d != null, d?.ToString() ?? "no decode");

            // With RGB clear in the reply, SDL refuses the color and writes
            // nothing (SDL_hidapi_sinput.c:810-826).
            bool ledAccepted = SDL_SetGamepadLED(padA, 10, 20, 30);
            Check("SDL_SetGamepadLED is refused", !ledAccepted, ledAccepted ? "accepted" : Utf8(SDL_GetError()));
            Check("SDL_RumbleGamepad again", SDL_RumbleGamepad(padA, 0x1100, 0x2200, 1000), Utf8(SDL_GetError()));
            d = MotorDecode(qa, 0x11, 0x22);
            Check("the second rumble decodes on pad A: leftMotor 17, rightMotor 34", d != null, d?.ToString() ?? "no decode");

            // Every output report 0x03 each pad received through the second
            // rumble, which is in the same capture.
            var onA = wireA.ToArray();
            var onB = wireB.ToArray();
            string Wire((byte Command, byte Sub)[] w) => string.Join(" ", w.Select(x => $"{x.Command:X2}{x.Sub:X2}"));
            Check("pad A received SDL's features command and type 2 haptics only, no player LED and no color",
                  onA.Any(x => x.Command == 0x02) && onA.Count(x => x.Command == 0x01) >= 2
                  && onA.All(x => x.Command == 0x02 || (x.Command == 0x01 && x.Sub == 0x02)), Wire(onA));
            Check("pad B received the features command and no player LED or color",
                  onB.Any(x => x.Command == 0x02) && onB.All(x => x.Command == 0x02 || x.Command == 0x01), Wire(onB));
            var crossed = qb.Where(x => Same(x, ("leftMotor", 0x80), ("rightMotor", 0x40)) || Same(x, ("leftMotor", 0x11), ("rightMotor", 0x22))).ToList();
            Check("pad A's rumble never decoded on pad B", crossed.Count == 0, string.Join("; ", crossed));
        }
        finally
        {
            if (padA != IntPtr.Zero) SDL_CloseGamepad(padA);
            if (padB != IntPtr.Zero) SDL_CloseGamepad(padB);
            SDL_Quit();
        }
    }
}
