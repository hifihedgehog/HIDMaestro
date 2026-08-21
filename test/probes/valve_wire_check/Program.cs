// Valve persona wire check (issue #56).
//
// For each of the three Valve personas: create it, submit a known state
// through SubmitState - the same call any consumer makes - then read the
// frame back off the real HID stack and assert the bytes land where that
// device's wire format says they do.
//
// This touches nothing outside the device: no window, no cursor, no keys.
//
// Exit 0 when every persona's frame is correct.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

static class Program
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    static readonly IntPtr INVALID = new IntPtr(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ov);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid c, string? d, uint f);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid c, string? d, char[] b, uint bl, uint f);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(IntPtr h, byte[] a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);

    static Guid HidGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
    static int s_fail;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static IEnumerable<string> HidPaths()
    {
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref HidGuid, null, 0) != 0 || len == 0)
            yield break;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref HidGuid, null, buf, len, 0) != 0) yield break;
        int i = 0;
        while (i < buf.Length)
        {
            int e = Array.IndexOf(buf, '\0', i);
            if (e < 0 || e == i) yield break;
            yield return new string(buf, i, e - i);
            i = e + 1;
        }
    }

    /// <summary>The interface whose input report is the size this persona
    /// declares. The lizard keyboard and mouse share the VID/PID, so match
    /// on report length rather than on the path text.</summary>
    static (string? path, int inLen) FindController(ushort vid, ushort pid, int wantLen)
    {
        foreach (var p in HidPaths())
        {
            if (p.IndexOf($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (p.IndexOf($"pid_{pid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
            IntPtr h = CreateFileW(p, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                                   IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID) continue;
            try
            {
                if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                var caps = new byte[64];
                int st = HidP_GetCaps(pp, caps);
                HidD_FreePreparsedData(pp);
                if (st != 0x00110000) continue;
                int inLen = BitConverter.ToUInt16(caps, 4);
                if (inLen == wantLen) return (p, inLen);
            }
            finally { CloseHandle(h); }
        }
        return (null, 0);
    }

    static byte[]? ReadFrame(string path, int inLen, HMController c, HMGamepadState state)
    {
        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                               IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) return null;
        using var stop = new ManualResetEventSlim(false);
        var pump = new Thread(() =>
        {
            try { while (!stop.IsSet) { c.SubmitState(state); stop.Wait(4); } }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        pump.Start();
        try
        {
            var buf = new byte[inLen];
            for (int i = 0; i < 6; i++)
                if (ReadFile(h, buf, buf.Length, out int n, IntPtr.Zero) && n > 0)
                    return buf;
            return null;
        }
        finally { stop.Set(); Thread.Sleep(40); CloseHandle(h); }
    }

    static short S16(byte[] f, int o) => (short)(f[o] | (f[o + 1] << 8));
    static ushort U16(byte[] f, int o) => (ushort)(f[o] | (f[o + 1] << 8));

    static float RemapClamped(float v, float lo, float hi, float olo, float ohi)
    {
        if (v < lo) v = lo;
        if (v > hi) v = hi;
        return olo + (ohi - olo) * (v - lo) / (hi - lo);
    }

    /// <summary>Decode a frame the way SDL does, so the check is against the
    /// reference consumer's arithmetic rather than against our own encoder.
    ///   Deck   SDL_hidapi_steamdeck.c:  LEFTX = sLeftStickX,
    ///          trigger = raw * 2 - 32768.
    ///   2015   SDL_hidapi_steam.c FormatStatePacketUntilGyro: with
    ///          STEAM_LEFTPAD_FINGERDOWN clear, sLeftStickX = sLeftPadX;
    ///          trigger = Remap((n &lt;&lt; 7) | n, 0, 26000, 0, 32767).
    ///   Triton SDL_hidapi_steam_triton.c: LEFTX = sLeftStickX,
    ///          trigger = raw * 2 - 32768.</summary>
    static (int leftX, int rightTrigger) SdlDecode(string id, byte[] f)
    {
        switch (id)
        {
            case "steam-deck-composite":
                return (S16(f, 48), (int)U16(f, 46) * 2 - 32768);
            case "steam-controller-2":
                return (S16(f, 10), (int)U16(f, 8) * 2 - 32768);
            default:
            {
                ulong buttons = 0;
                for (int i = 0; i < 8; i++) buttons |= (ulong)f[8 + i] << (8 * i);
                const ulong LEFTPAD_FINGERDOWN = 1UL << 19;
                // Finger-down clear means sLeftPad IS the joystick.
                int lx = (buttons & LEFTPAD_FINGERDOWN) != 0 ? 0 : S16(f, 16);
                int n = f[12];                      // nRight, the 8-bit trigger
                int rt = (int)RemapClamped((n << 7) | n, 0, 26000, 0, 32767);
                return (lx, rt);
            }
        }
    }

    const string SteamLog = "C:/Program Files (x86)/Steam/logs/controller.txt";

    static long SteamLogLength()
    {
        try { return System.IO.File.Exists(SteamLog) ? new System.IO.FileInfo(SteamLog).Length : 0; }
        catch { return 0; }
    }

    /// <summary>Wait for Steam to log a fresh claim of this VID/PID.</summary>
    static bool SteamClaimed(ushort vid, ushort pid, long from)
    {
        string want = $"{vid:x4} {pid:x4}";
        for (int i = 0; i < 45; i++)
        {
            Thread.Sleep(1000);
            try
            {
                using var fs = new System.IO.FileStream(SteamLog, System.IO.FileMode.Open,
                    System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                fs.Seek(Math.Max(0, from - 2048), System.IO.SeekOrigin.Begin);
                using var sr = new System.IO.StreamReader(fs);
                string fresh = sr.ReadToEnd();
                if (fresh.Contains(want, StringComparison.OrdinalIgnoreCase)
                    && fresh.Contains("device opened for index", StringComparison.Ordinal))
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Live monitor: create one persona, sweep it, and print what
    /// SDL's own driver would decode. Steam is never consulted, and the
    /// personas never emit keyboard or mouse reports, so this is the pads
    /// working with no Steam and no lizard mode.</summary>
    static int Monitor(string id)
    {
        var map = new System.Collections.Generic.Dictionary<string,(ushort v,ushort p,int len)>
        {
            ["steam-deck-composite"]       = (0x28DE, 0x1205, 65),
            ["steam-controller-composite"] = (0x28DE, 0x1102, 65),
            ["steam-controller-2"]         = (0x28DE, 0x1302, 54),
        };
        if (!map.TryGetValue(id, out var t)) { Console.WriteLine("unknown persona"); return 1; }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var prof = ctx.GetProfile(id);
        if (prof == null) { Console.WriteLine("no profile"); return 1; }
        bool steam = System.Diagnostics.Process.GetProcessesByName("steam").Length > 0;
        Console.WriteLine($"=== {id} live monitor (SDL decode; Steam running: {steam}) ===");

        using var c = ctx.CreateController(prof);
        string? path = null; int inLen = 0;
        for (int i = 0; i < 120 && path == null; i++) { Thread.Sleep(100); (path, inLen) = FindController(t.v, t.p, t.len); }
        if (path == null) { Console.WriteLine("device never enumerated"); return 1; }
        Console.WriteLine($"  {path}");
        Console.WriteLine("  sweeping the left stick; values are what an SDL app reads");
        Console.WriteLine();

        IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                               IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID) { Console.WriteLine("cannot open"); return 1; }
        using var stop = new ManualResetEventSlim(false);
        var pump = new Thread(() =>
        {
            float x = 0f; int dir = 1;
            try
            {
                while (!stop.IsSet)
                {
                    x += dir * 0.02f;
                    if (x >= 1f) { x = 1f; dir = -1; }
                    if (x <= 0f) { x = 0f; dir = 1; }
                    c.SubmitState(new HMGamepadState {
                        Buttons = 0,
                        Axes = HMGamepadStateHelpers.StandardAxes(prof, x, 0.5f, 0.5f, 0.5f, 0f, 1f) });
                    stop.Wait(8);
                }
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true };
        pump.Start();
        try
        {
            var buf = new byte[inLen];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int frames = 0; int lo = int.MaxValue, hi = int.MinValue;
            while (sw.Elapsed.TotalSeconds < 12)
            {
                if (!ReadFile(h, buf, buf.Length, out int n, IntPtr.Zero) || n <= 0) continue;
                var f = buf[0] == 0x00 && id != "steam-controller-2" ? buf[1..] : buf;
                var (lx, rt) = SdlDecode(id, f);
                frames++;
                if (lx < lo) lo = lx;
                if (lx > hi) hi = lx;
                if (frames % 40 == 0) Console.WriteLine($"    LEFTX {lx,7}   RIGHT_TRIGGER {rt,7}");
            }
            Console.WriteLine();
            Console.WriteLine($"  {frames} frames read; LEFTX swept {lo} .. {hi}");
            bool ok = frames > 100 && lo <= -30000 && hi >= 30000;
            Console.WriteLine(ok
                ? "  WORKS WITHOUT STEAM: an SDL app reads a full-scale stick sweep"
                : "  FAILED: the sweep did not come through");
            return ok ? 0 : 1;
        }
        finally { stop.Set(); Thread.Sleep(50); CloseHandle(h); }
    }

    static int Main()
    {
        var argv = Environment.GetCommandLineArgs();
        int mi = Array.IndexOf(argv, "--monitor");
        if (mi >= 0 && mi + 1 < argv.Length) return Monitor(argv[mi + 1]);

        Console.WriteLine("=== Valve persona wire check (SubmitState -> real HID stack) ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // Left stick hard left, right stick centred, right trigger fully
        // pulled. No buttons: nothing can reach the desktop.
        HMGamepadState State(HMProfile pr) => new()
        {
            Buttons = 0,
            Axes = HMGamepadStateHelpers.StandardAxes(pr, 0.0f, 0.5f, 0.5f, 0.5f, 0.0f, 1.0f),
        };

        // id, vid, pid, wire length, header check, stick offset, trigger offset
        // Pad, pressure, touch-bit and IMU offsets come from the same
        // structs SDL parses: SteamDeckStatePacket_t at report byte 4,
        // ValveControllerStatePacket_t likewise, TritonMTUFull_t at byte 1.
        // PressL of -1 means the device carries no pressure field (the 2015
        // controller derives it from the finger-down and click bits).
        var cases = new (string Id, ushort Vid, ushort Pid, int Len, byte[] Head, int Lsx, int TrigR,
                         int Btn, int BtnBytes, int TouchL, int TouchR,
                         int LPad, int RPad, int PressL, int PressR, int Accel, int Gyro)[]
        {
            ("steam-deck-composite",       0x28DE, 0x1205, 65, new byte[]{0x01,0x00,0x09,0x40}, 48, 46,
             8, 8, 19, 20, 16, 20, 56, 58, 24, 30),
            ("steam-controller-composite", 0x28DE, 0x1102, 65, new byte[]{0x01,0x00,0x01,0x3C}, 16, 26,
             8, 8, 19, 20, 16, 20, -1, -1, 28, 34),
            ("steam-controller-2",         0x28DE, 0x1302, 54, new byte[]{0x42},                10,  8,
             2, 4, 25, 21, 18, 24, 22, 28, 34, 40),
        };

        foreach (var t in cases)
        {
            Console.WriteLine();
            Console.WriteLine($"-- {t.Id} --");
            var prof = ctx.GetProfile(t.Id);
            Check("profile is in the catalog", prof != null);
            if (prof == null) continue;

            HMController? c = null;
            long steamBase = SteamLogLength();
            try
            {
                c = ctx.CreateController(prof);
                string? path = null; int inLen = 0;
                for (int i = 0; i < 120 && path == null; i++)
                {
                    Thread.Sleep(100);
                    (path, inLen) = FindController(t.Vid, t.Pid, t.Len);
                }
                Check($"controller interface enumerated ({t.Len}-byte input report)", path != null);
                if (path == null) continue;

                var raw = ReadFrame(path, inLen, c, State(prof));
                Check("an input report arrives on the interrupt endpoint", raw != null);
                if (raw == null) continue;

                // Windows prepends a report-id byte; for a descriptor with no
                // report ids it is 0 and the payload follows.
                var f = raw[0] == 0x00 && t.Head[0] != 0x00 ? raw[1..] : raw;

                bool head = true;
                for (int i = 0; i < t.Head.Length; i++) if (f[i] != t.Head[i]) head = false;
                Check("header matches this device's wire format", head,
                      string.Join(" ", Array.ConvertAll(f[..t.Head.Length], b => b.ToString("X2"))));

                Check("frame is not all zeros", Array.Exists(f, b => b != 0));

                short lsx = S16(f, t.Lsx);
                Check("left stick full left reaches the wire at full scale",
                      lsx <= -32000, $"lsx@{t.Lsx}={lsx}");

                ushort tr = (ushort)(f[t.TrigR] | (f[t.TrigR + 1] << 8));
                Check("right trigger full pull reaches the wire",
                      tr >= 32000, $"trigR@{t.TrigR}={tr}");

                // Non-disruptive end-to-end: does Steam claim it? Reads
                // Steam's own log; touches nothing on the desktop.
                // Decode through SDL's own arithmetic: this is what any
                // consumer built on the reference implementation recovers.
                var (sdlX, sdlRt) = SdlDecode(t.Id, f);
                Check("SDL's parser recovers full-left on the left stick",
                      sdlX <= -32000, $"LEFTX={sdlX}");
                Check("SDL's parser recovers a fully pulled right trigger",
                      sdlRt >= 32000, $"RIGHT_TRIGGER={sdlRt}");

                // Both extremes, so an axis frozen at one value cannot pass.
                var opposite = new HMGamepadState
                {
                    Buttons = 0,
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 1.0f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                };
                var raw2 = ReadFrame(path, inLen, c, opposite);
                Check("a second frame arrives after the state changes", raw2 != null);
                if (raw2 != null)
                {
                    var f2 = raw2[0] == 0x00 && t.Head[0] != 0x00 ? raw2[1..] : raw2;
                    var (sdlX2, sdlRt2) = SdlDecode(t.Id, f2);
                    Check("SDL's parser recovers full-RIGHT on the same axis",
                          sdlX2 >= 32000, $"LEFTX={sdlX2}");
                    Check("the stick actually swept rather than sticking",
                          sdlX2 - sdlX >= 60000, $"span={sdlX2 - sdlX}");
                    Check("SDL's parser recovers a released right trigger",
                          sdlRt2 <= -32000 || sdlRt2 <= 100, $"RIGHT_TRIGGER={sdlRt2}");
                }

                // ── trackpads and IMU ────────────────────────────────
                // One contact on each pad, at opposite corners so an
                // encoder that wrote a constant cannot pass, plus six
                // distinct IMU values. Read back through SDL's own
                // arithmetic: pads are x = padX / 65536 + 0.5 and
                // y = -padY / 65536 + 0.5 in every one of the three
                // drivers, and the whole pad lane is gated on the
                // finger-down bit, so the bit is asserted too.
                var touch = new HMGamepadState
                {
                    Buttons = 0,
                    Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                    TouchpadFinger0Active = true,
                    TouchpadFinger0X = 1919, TouchpadFinger0Y = 0,      // right edge, top
                    TouchpadFinger1Active = true,
                    TouchpadFinger1X = 0, TouchpadFinger1Y = 1079,      // left edge, bottom
                    AccelX = 1000, AccelY = -2000, AccelZ = 3000,
                    GyroPitch = -4000, GyroYaw = 5000, GyroRoll = -6000,
                };
                var raw3 = ReadFrame(path, inLen, c, touch);
                Check("a frame arrives with both pads touched", raw3 != null);
                if (raw3 != null)
                {
                    var f3 = raw3[0] == 0x00 && t.Head[0] != 0x00 ? raw3[1..] : raw3;

                    ulong bits = 0;
                    for (int i = 0; i < t.BtnBytes; i++) bits |= (ulong)f3[t.Btn + i] << (8 * i);
                    Check("left pad reports finger down where SDL gates the lane",
                          (bits & (1UL << t.TouchL)) != 0, $"bit {t.TouchL}");
                    Check("right pad reports finger down",
                          (bits & (1UL << t.TouchR)) != 0, $"bit {t.TouchR}");

                    float lx = S16(f3, t.LPad)     / 65536.0f + 0.5f;
                    float ly = -S16(f3, t.LPad + 2) / 65536.0f + 0.5f;
                    float rx = S16(f3, t.RPad)     / 65536.0f + 0.5f;
                    float ry = -S16(f3, t.RPad + 2) / 65536.0f + 0.5f;
                    Check("SDL reads the left contact at the pad's right edge, top",
                          lx > 0.95f && ly < 0.05f, $"({lx:F3}, {ly:F3})");
                    Check("SDL reads the right contact at the opposite corner",
                          rx < 0.05f && ry > 0.95f, $"({rx:F3}, {ry:F3})");

                    if (t.PressL >= 0)
                    {
                        Check("both pads report pressure while touched",
                              U16(f3, t.PressL) >= 32000 && U16(f3, t.PressR) >= 32000,
                              $"L={U16(f3, t.PressL)} R={U16(f3, t.PressR)}");
                    }

                    Check("the accelerometer triple reaches the wire",
                          S16(f3, t.Accel) == 1000 && S16(f3, t.Accel + 2) == -2000
                                                   && S16(f3, t.Accel + 4) == 3000,
                          $"{S16(f3, t.Accel)},{S16(f3, t.Accel + 2)},{S16(f3, t.Accel + 4)}");
                    Check("the gyroscope triple reaches the wire",
                          S16(f3, t.Gyro) == -4000 && S16(f3, t.Gyro + 2) == 5000
                                                   && S16(f3, t.Gyro + 4) == -6000,
                          $"{S16(f3, t.Gyro)},{S16(f3, t.Gyro + 2)},{S16(f3, t.Gyro + 4)}");

                    // Lifting has to clear the gate, or a consumer sees a
                    // contact stuck on the pad forever.
                    var lift = new HMGamepadState
                    {
                        Buttons = 0,
                        Axes = HMGamepadStateHelpers.StandardAxes(prof, 0.5f, 0.5f, 0.5f, 0.5f, 0.0f, 0.0f),
                    };
                    var raw4 = ReadFrame(path, inLen, c, lift);
                    if (raw4 != null)
                    {
                        var f4 = raw4[0] == 0x00 && t.Head[0] != 0x00 ? raw4[1..] : raw4;
                        ulong b4 = 0;
                        for (int i = 0; i < t.BtnBytes; i++) b4 |= (ulong)f4[t.Btn + i] << (8 * i);
                        Check("both finger-down bits clear when the contacts lift",
                              (b4 & (1UL << t.TouchL)) == 0 && (b4 & (1UL << t.TouchR)) == 0);
                    }
                }

                if (System.Diagnostics.Process.GetProcessesByName("steam").Length > 0)
                    Check("Steam claims the device (fresh entry in controller.txt)",
                          SteamClaimed(t.Vid, t.Pid, steamBase));
                else
                    Console.WriteLine("  [skip] Steam not running; claim check needs a live client");
            }
            catch (Exception ex) { Check("persona created without throwing", false, ex.Message); }
            finally { c?.Dispose(); Thread.Sleep(400); }
        }

        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== ALL THREE VALVE PERSONAS EMIT CORRECT FRAMES ==="
            : $"=== {s_fail} check(s) FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }
}
