// Switch Pro pre-handshake simple-mode conformance check (issue #37,
// superseding the #35 synthetic-0x30 shape).
//
// Under the real BLUETOOTH descriptor (extracted from a live Pro's SDP
// cache), report 0x3F is the only report DirectInput can parse: 16
// buttons, null-state hat, X/Y/Rx/Ry 16-bit. The full-mode family is
// vendor-blob, invisible to joy.cpl. Before the handshake the pad sends
// the 12-byte 0x3F simple-mode report when its bytes change, as a real
// pad pushes it on a press or release (dekuNukem bluetooth_hid_notes.md,
// "INPUT 0x3F"). Through v1.12.0 it streamed every 15 ms instead, and
// v2.0.0 changed that on purpose (docs/design/data-driven-devices.md,
// decision (e)), and protocol_engine_check's Deliberate.Change holds the
// engine to it. The first 0x01 subcommand arms full mode and the stream
// becomes the 49-byte BT 0x30 on the 15 ms clock.
//
// Phases:
//   1. Nothing changes, nothing streams: no 0x3F stream at rest, a press
//      and a release each send one 12-byte 0x3F, and the one at rest has
//      buttons 0, hat nibble 8 (null) and all four axes 0x8000.
//   2. Submitted state lands in simple-mode fields: A -> byte1 bit1,
//      ZR -> byte1 bit7, dpad up -> hat 0, X full right / Y full up.
//   3. Held pad answers a 0x01 subcommand (0x21 reply, 49 bytes), and a
//      change after it still sends 0x3F, no 0x30.
//   4. Unheld pad flips to 49-byte 0x30 full-mode on the first
//      subcommand (timer advancing, battery byte, A at byte3 bit3).
//
// The held pad of phases 1 to 3 is a copy of switch-pro.json, loaded from
// a directory of the probe's own, whose rules set no protocolSeen: it
// answers every subcommand and never leaves the 0x3F stream. A Chromium
// browser handshakes every new Switch Pro within milliseconds, as it does
// real hardware, so the catalog profile could leave 0x3F before phase 1
// reads it. Before v2.0.0 a registry value the driver read at its add
// held the pad.
//
// Headless, elevation-required, exit 0 PASS / 1 FAIL.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

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

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FlushQueue(SafeFileHandle h);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    static SafeFileHandle s_hid = null!;
    static IntPtr s_readEvent, s_writeEvent;

    static int s_lastReadLen;

    // BT descriptor: InputReportByteLength is 362 (the 0x31-0x33 family).
    // HidClass zero-pads every completed read to that length regardless
    // of the report's wire size (0x3F = 12, 0x21/0x30 = 49); a live BT
    // Pro reads identically (2026-07-22 capture). Track the completed
    // length so shape asserts can guard descriptor drift.
    static byte[]? HidRead(int timeoutMs)
    {
        var buf = GC.AllocateArray<byte>(362, pinned: true);
        var ov = new NativeOverlapped { EventHandle = s_readEvent };
        if (!ReadFile(s_hid, buf, 362, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997 /* ERROR_IO_PENDING */)
            return null;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint read, (uint)timeoutMs, false))
        {
            CancelAndWait(ref ov);
            return null;
        }
        s_lastReadLen = (int)read;
        return read > 0 ? buf : null;
    }

    // BT descriptor: OutputReportByteLength is 49 (48 vendor bytes + ID).
    static bool HidWrite(byte[] report)
    {
        var buf = GC.AllocateArray<byte>(49, pinned: true);
        Array.Copy(report, buf, Math.Min(report.Length, 49));
        var ov = new NativeOverlapped { EventHandle = s_writeEvent };
        if (!WriteFile(s_hid, buf, 49, IntPtr.Zero, ref ov)
            && Marshal.GetLastWin32Error() != 997)
            return false;
        if (!GetOverlappedResultEx(s_hid, ref ov, out uint written, 1000, false))
        {
            CancelAndWait(ref ov);
            return false;
        }
        return written == 49;
    }

    // A canceled transfer still completes, and the system writes its status
    // into the OVERLAPPED and its bytes into the buffer when it does
    // (CancelIoEx on Microsoft Learn: the OVERLAPPED must not be freed or
    // reused until the canceled operation has completed). The OVERLAPPED
    // lives in the caller's frame and the buffers are pinned, so this waits
    // for the completion before the frame returns, as switch_pro_check does.
    static void CancelAndWait(ref NativeOverlapped ov)
    {
        CancelIoEx(s_hid, IntPtr.Zero);
        GetOverlappedResultEx(s_hid, ref ov, out _, 0xFFFFFFFF /* INFINITE */, false);
    }

    /// <summary>Every report that arrives within <paramref name="ms"/>.</summary>
    static System.Collections.Generic.List<byte[]> Collect(int ms)
    {
        var got = new System.Collections.Generic.List<byte[]>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var r = HidRead((int)Math.Max(1, ms - sw.ElapsedMilliseconds));
            if (r != null) got.Add(r);
        }
        return got;
    }

    /// <summary>Reports for at least <paramref name="minMs"/>, and past that
    /// until one <paramref name="want"/> accepts has arrived or
    /// <paramref name="maxMs"/> is up.</summary>
    static System.Collections.Generic.List<byte[]> CollectUntil(Func<byte[], bool> want, int minMs, int maxMs)
    {
        var got = new System.Collections.Generic.List<byte[]>();
        bool found = false;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < (found ? minMs : maxMs))
        {
            var r = HidRead((int)Math.Max(1, (found ? minMs : maxMs) - sw.ElapsedMilliseconds));
            if (r == null) continue;
            got.Add(r);
            if (want(r)) found = true;
        }
        return got;
    }

    /// <summary>The reports the pad sends on its own. A 0x21 answers a
    /// subcommand, and before phase 3 only another program (a browser or
    /// Steam handshaking the new pad) writes one.</summary>
    static System.Collections.Generic.List<byte[]> Own(System.Collections.Generic.List<byte[]> frames)
        => frames.Where(f => f[0] != 0x21).ToList();

    static string Ids(System.Collections.Generic.List<byte[]> frames)
        => frames.Count == 0 ? "none" : string.Join(",", frames.Select(f => f[0].ToString("X2")));

    static byte[]? ReadUntil(byte reportId, int timeoutMs, Func<byte[], bool>? accept = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var r = HidRead((int)Math.Max(1, timeoutMs - sw.ElapsedMilliseconds));
            if (r == null) continue;
            if (r[0] != reportId) continue;
            if (accept != null && !accept(r)) continue;
            return r;
        }
        return null;
    }

    static int Main()
    {
        Console.WriteLine("=== Switch Pro 0x3F simple-mode idle stream (issue #37) ===");

        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] all phases (requires elevation, same as CreateController)");
                return 0;
            }
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        var profile = ctx.GetProfile("switch-pro")
            ?? throw new InvalidOperationException("switch-pro profile missing");

        // The held pad: switch-pro.json with no rule setting protocolSeen.
        string heldDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm-switch-held-{Environment.ProcessId}");
        System.IO.Directory.CreateDirectory(heldDir);
        try
        {
        var asm = typeof(HMContext).Assembly;
        string? text = null;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            string t = new System.IO.StreamReader(st, System.Text.Encoding.UTF8).ReadToEnd();
            if (System.Text.Json.Nodes.JsonNode.Parse(t) is System.Text.Json.Nodes.JsonObject o && (string?)o["id"] == "switch-pro") { text = t; break; }
        }
        var held = System.Text.Json.Nodes.JsonNode.Parse(text ?? throw new InvalidOperationException("switch-pro.json is not embedded"))!.AsObject();
        held["id"] = "switch-pro-held";
        foreach (var rule in held["protocol"]!["commands"]!["rules"]!.AsArray()) rule!.AsObject().Remove("set");
        System.IO.File.WriteAllText(System.IO.Path.Combine(heldDir, "switch-pro-held.json"), held.ToJsonString());
        ctx.LoadProfilesFromDirectory(heldDir);
        var heldProfile = ctx.GetProfile("switch-pro-held")
            ?? throw new InvalidOperationException("the held copy of switch-pro did not load");

        Console.WriteLine("  Creating the held copy of switch-pro...");
        using var ctrl = ctx.CreateController(heldProfile);

        // Neutral pump keeps the shared body populated. NO protocol traffic
        // is written until phase 3, so the stream must stay in descriptor
        // layout throughout phases 1-2.
        var pumpState = new HMGamepadState();
        var pumpLock = new object();
        var pumpStop = false;
        var pump = new Thread(() =>
        {
            while (!Volatile.Read(ref pumpStop))
            {
                lock (pumpLock) ctrl.SubmitState(pumpState);
                Thread.Sleep(8);
            }
        })
        { IsBackground = true };
        pump.Start();

        // THIS pad only: a real Pro paired to this box shares VID/PID
        // (2026-07-22), and another program's virtual Switch Pro shares the
        // HM-CTL- prefix. This one serves HM-CTL- and its own transport
        // index (DeviceOrchestrator.DriverSerial). A real one serves its MAC.
        string serial = DeviceOrchestrator.DriverSerial(ctrl.TransportIndex);
        string? path = null;
        for (int i = 0; i < 300 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009
                    && d.SerialNumberString == serial)?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        Check($"virtual HID interface enumerates (VID 057E PID 2009, serial {serial})", path != null, path ?? "");
        if (path == null) { Volatile.Write(ref pumpStop, true); return 1; }

        s_readEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_writeEvent = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        s_hid = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        Check("HID device opens", !s_hid.IsInvalid);
        if (s_hid.IsInvalid) { Volatile.Write(ref pumpStop, true); return 1; }

        // ── Phase 1: 0x3F is sent on a change, not streamed ─────────────
        Console.WriteLine("\n-- Phase 1: 0x3F on change, no stream at rest (no protocol traffic yet) --");

        // The pad sent its state at rest once, at its start, and the pump
        // has submitted that same state every 8 ms since. Whatever the HID
        // class queued before this handle opened is dropped, and then 600
        // ms at rest must bring no stream: a 15 ms stream would bring 40.
        // One report is allowed, the one the pad sends at its start, which
        // lands here when the pump's first submit runs late under load.
        HidD_FlushQueue(s_hid);
        var atRest = Own(Collect(600));
        Check("no report streams while nothing changes", atRest.Count <= 1, Ids(atRest));

        // A press sends one 0x3F, holding it sends no more, and the release
        // sends one more: the pad's state at rest, read for its shape.
        lock (pumpLock) pumpState = new HMGamepadState { Buttons = (HMButton)(1u << 1) };
        var pressed = ReadUntil(0x3F, 2000, r => (r[1] & 0x02) != 0);
        Check("a press sends a 0x3F", pressed != null);
        var holding = Own(Collect(300));
        Check("holding the press sends no more", holding.Count == 0, Ids(holding));
        lock (pumpLock) pumpState = new HMGamepadState();
        var released = ReadUntil(0x3F, 2000, r => (r[1] & 0x02) == 0);
        int frameLen = s_lastReadLen;
        Check("the release sends a 0x3F", released != null);
        var after = Own(Collect(300));
        Check("and nothing after it", after.Count == 0, Ids(after));
        if (released != null)
        {
            // HidClass completes every read at InputReportByteLength
            // (362 under the BT descriptor), zero-padded. Verified
            // identical on a live BT Pro (2026-07-22 capture). Guards
            // descriptor drift.
            Check("reads complete at 362 (InputReportByteLength)", frameLen == 362, $"{frameLen}");
            Check("byte1 at 0 at rest", released[1] == 0, released[1].ToString("X2"));
            Check("byte2 at 0 at rest", released[2] == 0, released[2].ToString("X2"));

            var f0 = released;
            int x  = f0[4] | (f0[5] << 8);
            int y  = f0[6] | (f0[7] << 8);
            int rx = f0[8] | (f0[9] << 8);
            int ry = f0[10] | (f0[11] << 8);
            Check("X centered (~0x8000)",  Math.Abs(x - 0x8000) <= 0x200, $"0x{x:X4}");
            Check("Y centered (~0x8000)",  Math.Abs(y - 0x8000) <= 0x200, $"0x{y:X4}");
            Check("Rx centered (~0x8000)", Math.Abs(rx - 0x8000) <= 0x200, $"0x{rx:X4}");
            Check("Ry centered (~0x8000)", Math.Abs(ry - 0x8000) <= 0x200, $"0x{ry:X4}");
            Check("hat null (nibble 8)", (f0[3] & 0x0F) == 8, $"0x{f0[3]:X2}");
            Check("pad nibble zero", (f0[3] & 0xF0) == 0, $"0x{f0[3]:X2}");
        }

        // ── Phase 2: submitted state lands in simple-mode fields ────────
        Console.WriteLine("\n-- Phase 2: submitted state in simple-mode fields --");

        // A = layout buttonIndex 1 -> simple-mode bit1 (byte1);
        // ZR = index 7 -> simple-mode bit7 (byte1); dpad up -> hat 0;
        // left stick full right (X~65535) and full up (Y~0, up-positive
        // SDL frame inverts to descriptor low).
        lock (pumpLock)
        {
            pumpState = new HMGamepadState
            {
                Buttons = (HMButton)((1u << 1) | (1u << 7)),
                HatDegrees = 0f,
                Axes = new System.Collections.Generic.Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 1.0f,
                    [HMAxis.Y] = 0.0f,
                },
            };
        }
        Thread.Sleep(60);

        var fp = ReadUntil(0x3F, 2000, r => (r[1] & 0x02) != 0);
        Check("A press lands on simple-mode byte1 bit1", fp != null);
        if (fp != null)
        {
            Check("ZR on simple-mode byte1 bit7", (fp[1] & 0x80) != 0, $"0x{fp[1]:X2}");
            int x = fp[4] | (fp[5] << 8);
            int y = fp[6] | (fp[7] << 8);
            Check("X full right (>= 0xF000)", x >= 0xF000, $"0x{x:X4}");
            Check("Y full up (<= 0x0FFF)", y <= 0x0FFF, $"0x{y:X4}");
            Check("hat up (nibble 0)", (fp[3] & 0x0F) == 0, $"0x{fp[3]:X2}");
        }

        // Back to neutral before the handshake.
        lock (pumpLock) pumpState = new HMGamepadState();
        Thread.Sleep(40);

        // ── Phase 3: held pad answers a subcommand but stays 0x3F ──────
        Console.WriteLine("\n-- Phase 3: held pad answers 0x01 subcommand but stays 0x3F --");

        // Bare 0x01 subcommand, SDL BT framing: counter + neutral rumble
        // + subcommand 0x02 (device info). The 0x80 USB-init family does
        // not exist in the BT descriptor, exactly like real BT hardware.
        var cmd = new byte[49];
        cmd[0] = 0x01; cmd[1] = 0x00;
        cmd[2] = 0x00; cmd[3] = 0x01; cmd[4] = 0x40; cmd[5] = 0x40;
        cmd[6] = 0x00; cmd[7] = 0x01; cmd[8] = 0x40; cmd[9] = 0x40;
        cmd[10] = 0x02;                 // subcommand: device info
        Check("0x01 subcommand write (49-byte BT framing)", HidWrite(cmd));
        // Matched on the echoed subcommand, as SDL's ReadSubcommandReply
        // does: another program's handshake gets replies of its own.
        var ack = ReadUntil(0x21, 2000, r => r[14] == 0x02);
        Check("0x21 subcommand reply (the held copy answers)",
              ack != null && ack[14] == 0x02, ack != null ? $"len={s_lastReadLen}" : "no reply");
        Check("0x21 reply completes at 362 (padded)", ack != null && s_lastReadLen == 362, $"{s_lastReadLen}");
        // The held copy never sets protocolSeen, so a change after the
        // subcommand still goes out as 0x3F, and no 0x30 starts. The reads
        // run 400 ms, long enough for a 15 ms stream to show, and past that
        // until the press arrives, so a pump that runs late cannot fail it.
        lock (pumpLock) pumpState = new HMGamepadState { Buttons = (HMButton)(1u << 1) };
        var still = CollectUntil(f => f[0] == 0x3F && (f[1] & 0x02) != 0, 400, 2000);
        lock (pumpLock) pumpState = new HMGamepadState();
        Check("a change after it still sends 0x3F on the held copy, and no 0x30",
              still.Any(f => f[0] == 0x3F && (f[1] & 0x02) != 0) && !still.Any(f => f[0] == 0x30), Ids(still));

        Volatile.Write(ref pumpStop, true);
        pump.Join(1000);
        s_hid.Dispose();
        ctrl.Dispose();

        // ── Phase 4: unheld pad flips to BT full-mode 0x30 on protocol ──
        // A fresh pad of the catalog's switch-pro. On a box with a Chromium browser
        // running, Edge/Chrome arms it within milliseconds; otherwise our
        // own 0x01 subcommand does. Either way the stream must be the
        // 49-byte BT full-mode 0x30: timer at byte 1, battery at byte 2.
        Console.WriteLine("\n-- Phase 4: unheld pad locks 49-byte 0x30 full-mode on protocol traffic --");

        using (var ctrl2 = ctx.CreateController(profile))
        {
            var pump2Stop = false;
            var pump2 = new Thread(() =>
            {
                var st = new HMGamepadState { Buttons = (HMButton)(1u << 1) }; // A held
                while (!Volatile.Read(ref pump2Stop)) { ctrl2.SubmitState(st); Thread.Sleep(8); }
            }) { IsBackground = true };
            pump2.Start();

            string serial2 = DeviceOrchestrator.DriverSerial(ctrl2.TransportIndex);
            string? path2 = null;
            for (int i = 0; i < 300 && path2 == null; i++)
            {
                path2 = HidDeviceEnumerator.Enumerate()
                    .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009
                        && d.SerialNumberString == serial2)?.DevicePath;
                if (path2 == null) Thread.Sleep(100);
            }
            Check($"second pad enumerates (serial {serial2})", path2 != null);
            if (path2 != null)
            {
                s_hid = CreateFileW(path2, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                Check("second pad opens", !s_hid.IsInvalid);
                if (!s_hid.IsInvalid)
                {
                    // Arm it ourselves; harmless if a browser already did.
                    HidWrite(cmd);

                    var nf = new System.Collections.Generic.List<byte[]>();
                    bool allLen362 = true;
                    var deadline = Stopwatch.StartNew();
                    // A hang detector: four frames take 60 ms on the 15 ms
                    // clock, and the loop ends when they arrive.
                    while (nf.Count < 4 && deadline.ElapsedMilliseconds < 10000)
                    {
                        var r = HidRead(200);
                        if (r != null && r[0] == 0x30)
                        {
                            nf.Add(r);
                            if (s_lastReadLen != 362) allLen362 = false;
                        }
                    }
                    Check("post-arm 0x30 frames", nf.Count >= 3, $"{nf.Count}");
                    Check("0x30 frames complete at 362 (padded)", nf.Count > 0 && allLen362);
                    if (nf.Count >= 3)
                    {
                        bool timerAdvances = false;
                        for (int i = 1; i < nf.Count; i++)
                            if (nf[i][1] != nf[0][1]) { timerAdvances = true; break; }
                        Check("byte1 advances (Nintendo timer)", timerAdvances,
                              string.Join(",", nf.Select(f => f[1].ToString("X2"))));
                        Check("byte2 battery/conn nonzero", nf[^1][2] != 0, $"0x{nf[^1][2]:X2}");
                        var na = ReadUntil(0x30, 2000, r => (r[3] & 0x08) != 0);
                        Check("A press at Nintendo byte3 bit3 post-arm", na != null);
                    }
                    s_hid.Dispose();
                }
            }
            Volatile.Write(ref pump2Stop, true);
            pump2.Join(1000);
        }

        }
        finally
        {
            try { System.IO.Directory.Delete(heldDir, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Console.WriteLine(s_failures == 0
            ? "\n=== PASS: 0x3F simple-mode on change; subcommand locks 49-byte 0x30 on unheld pad ==="
            : $"\n=== FAIL: {s_failures} check(s) failed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
