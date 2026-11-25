// live <umdf2|usbip> [hover]: the spike's touchscreen and pen created on the
// backend named, contacts and pen states submitted through SubmitTouch and
// SubmitPen, and each report read back from the device (HidD_GetInputReport)
// and decoded by Windows' own HID parser (HidP) per link collection: each
// Finger collection's Tip Switch, In Range, Contact Identifier, X and Y, the
// Contact Count, and the pen's switches, position and pressure. The Device
// Mode Windows writes while the touchscreen starts must reach the SDK on
// both backends (TouchDeviceMode). Contacts go at one point at the top center
// of the display, away from the corner controls of a sign-in screen. With
// "hover", no contact or pen tip goes down: for a machine whose desktop is
// in use.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

internal static unsafe partial class Program
{
    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetInputReport(IntPtr h, byte[] buffer, uint length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_SetFeature(IntPtr h, byte[] buffer, uint length);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_LINK_COLLECTION_NODE
    {
        public ushort LinkUsage, LinkUsagePage, Parent, NumberOfChildren, NextSibling, FirstChild;
        public uint Bits;
        public IntPtr UserContext;
    }

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetLinkCollectionNodes([Out] HIDP_LINK_COLLECTION_NODE[] nodes, ref uint length, IntPtr data);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, uint length);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetUsages(int type, ushort page, ushort link, [Out] ushort[] list, ref uint length, IntPtr data, byte[] report, uint length2);

    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    /// <summary>A HID child opened for reads of its preparsed data, input
    /// reports and features, with no access when the system holds it.</summary>
    sealed class Child : IDisposable
    {
        public readonly IntPtr Handle, Data;
        public readonly HIDP_CAPS Caps;
        public readonly HIDP_LINK_COLLECTION_NODE[] Links;
        public readonly string How, Path;

        public Child(string path)
        {
            Path = path;
            Handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            How = "read/write";
            if (Handle == new IntPtr(-1))
            {
                Handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                How = "no access";
            }
            if (Handle == new IntPtr(-1)) throw new InvalidOperationException($"open {path}: Win32 {Marshal.GetLastPInvokeError()}");
            if (!HidD_GetPreparsedData(Handle, out Data)) throw new InvalidOperationException($"preparsed data: Win32 {Marshal.GetLastPInvokeError()}");
            HidP_GetCaps(Data, out Caps);
            uint n = Caps.NumberLinkCollectionNodes;
            Links = new HIDP_LINK_COLLECTION_NODE[n];
            HidP_GetLinkCollectionNodes(Links, ref n, Data);
        }

        public byte[]? Input(byte reportId)
        {
            var buf = new byte[Caps.InputReportByteLength];
            buf[0] = reportId;
            return HidD_GetInputReport(Handle, buf, (uint)buf.Length) ? buf : null;
        }

        public int Value(byte[] report, ushort page, ushort link, ushort usage) =>
            HidP_GetUsageValue(HidP_Input, page, link, usage, out uint v, Data, report, (uint)report.Length) == HIDP_STATUS_SUCCESS ? (int)v : -1;

        public bool On(byte[] report, ushort page, ushort link, ushort usage)
        {
            var list = new ushort[64];
            uint n = (uint)list.Length;
            return HidP_GetUsages(HidP_Input, page, link, list, ref n, Data, report, (uint)report.Length) == HIDP_STATUS_SUCCESS
                   && list.Take((int)n).Contains(usage);
        }

        public void Dispose()
        {
            if (Data != IntPtr.Zero) HidD_FreePreparsedData(Data);
            if (Handle != new IntPtr(-1)) CloseHandle(Handle);
        }
    }

    static Child? OpenChild(string vidPid, string usage)
    {
        foreach (var n in HidChildren(vidPid))
            if (n.HardwareIds.Any(h => h.Equals("HID_DEVICE_UP:000D_U:" + usage, StringComparison.OrdinalIgnoreCase)) && InterfacePath(n.Id) is { } path)
                return new Child(path);
        return null;
    }

    static int Live(HMBackend backend, bool hover)
    {
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("  refused: live needs elevation");
            return 3;
        }
        if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded."); return 2; }
        Console.WriteLine($"=== touch and pen, live on {backend}{(hover ? ", hover only" : "")}: Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}, session {Process.GetCurrentProcess().SessionId} ===");
        string dir = KillProfiles();
        using var ctx = new HMContext(backend);
        ctx.InstallDriver();
        ctx.LoadProfilesFromDirectory(dir);
        LiveTouch(ctx, backend, hover);
        LiveWide(ctx, backend, hover);
        LiveRaw(ctx, backend);
        LivePen(ctx, backend, hover);
        LivePad(ctx, backend, hover);
        Metrics("after every dispose");
        Console.WriteLine();
        Console.WriteLine($"=== {s_checks - s_fails}/{s_checks} checks passed ===");
        return s_fails == 0 ? 0 : 1;
    }

    static void LiveTouch(HMContext ctx, HMBackend backend, bool hover)
    {
        var profile = ctx.GetProfile("spike-touchscreen")!;
        var t = profile.Touch;
        Check("the spike touchscreen's layout: a touchscreen, 2 contacts per report", t is { IsTouchpad: false, ContactsPerReport: 2 },
              t == null ? "no touch layout" : $"{t.ContactsPerReport} per report, ids to {t.MaxContactId}");
        if (t == null) return;
        using var c = ctx.CreateController(profile, null, backend);
        Check("the touchscreen controller delivers every report", c.DeliversEveryReport || backend == HMBackend.Usbip, $"DeliversEveryReport {c.DeliversEveryReport}");
        Thread.Sleep(2000);
        using var touch = OpenChild("VID_1209&PID_0007", "0004");
        using var config = OpenChild("VID_1209&PID_0007", "000E");
        Check("its touch screen and configuration collections open", touch != null && config != null, $"touch {touch?.How ?? "none"}, configuration {config?.How ?? "none"}");
        if (touch == null || config == null) return;

        // The write path. Windows' inbox MTConfig driver holds the
        // configuration collection and writes its Device Mode while the
        // device starts. A program's own write there never reaches the
        // device on either backend (seen 2026-11-24 on the Celeron), so the
        // check is that Windows' write reached the SDK: 2, multiple input.
        var waited = Stopwatch.StartNew();
        while (c.TouchDeviceMode == null && waited.ElapsedMilliseconds < 3000) Thread.Sleep(20);
        Check("Windows' own Device Mode write reached the SDK during the start: 2, multiple input",
              c.TouchDeviceMode == 2, $"TouchDeviceMode {c.TouchDeviceMode?.ToString() ?? "never written"} after {waited.ElapsedMilliseconds} ms");

        ushort[] fingers = Enumerable.Range(0, touch.Links.Length).Where(i => touch.Links[i].LinkUsagePage == 0x0D && touch.Links[i].LinkUsage == 0x22)
                                     .Select(i => (ushort)i).ToArray();
        Check("Windows' parser finds two Finger collections", fingers.Length == 2, $"{fingers.Length} of {touch.Links.Length} link collections");
        int x0 = At(t.X, 2), x1 = t.X.Minimum + (int)((long)(t.X.Maximum - t.X.Minimum) * 55 / 100), y = t.Y.Minimum + (t.Y.Maximum - t.Y.Minimum) / 10;
        var contacts = new[] { new HMTouchContact { Id = 0, X = x0, Y = y, Hover = hover }, new HMTouchContact { Id = 1, X = x1, Y = y, Hover = hover } };

        var sw = Stopwatch.StartNew();
        c.SubmitTouch(contacts);
        long took = sw.ElapsedMilliseconds;
        Thread.Sleep(50);
        var r = touch.Input(0x01);
        string Show(byte[] rep) => string.Join(", ", fingers.Select(f =>
            $"id {touch.Value(rep, 0x0D, f, 0x51)} tip {(touch.On(rep, 0x0D, f, 0x42) ? 1 : 0)} range {(touch.On(rep, 0x0D, f, 0x32) ? 1 : 0)} ({touch.Value(rep, 0x01, f, 0x30)},{touch.Value(rep, 0x01, f, 0x31)})"))
            + $", count {touch.Value(rep, 0x0D, 0, 0x54)}";
        Check($"two contacts {(hover ? "in range" : "down")} read back through HidP at their places, Contact Count 2 (submit {took} ms)",
              r != null && touch.Value(r, 0x0D, 0, 0x54) == 2
              && fingers.Select(f => (touch.Value(r, 0x0D, f, 0x51), touch.Value(r, 0x01, f, 0x30), touch.On(r, 0x0D, f, 0x42), touch.On(r, 0x0D, f, 0x32)))
                        .SequenceEqual(new[] { (0, x0, !hover, true), (1, x1, !hover, true) }),
              r == null ? $"HidD_GetInputReport failed, Win32 {Marshal.GetLastPInvokeError()}" : Show(r));

        c.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty);
        Thread.Sleep(50);
        r = touch.Input(0x01);
        Check("an empty submit lifts both where they were: tips and In Range off, ids kept, Contact Count 2",
              r != null && touch.Value(r, 0x0D, 0, 0x54) == 2
              && fingers.All(f => !touch.On(r, 0x0D, f, 0x42) && !touch.On(r, 0x0D, f, 0x32))
              && fingers.Select(f => touch.Value(r, 0x0D, f, 0x51)).SequenceEqual(new[] { 0, 1 }),
              r == null ? "no read" : Show(r));
        Metrics("with the touchscreen");
    }

    /// <summary>The Elan touchscreen of the Dell XPS 13 9360: ten contacts a
    /// report, a 116-byte touch report, longer than the input section's
    /// 80-byte extended lane, which the wide frames section carries
    /// (SharedMemoryIO.WriteInputFrame). Three contacts read back whole
    /// through HidP. On USB/IP the interrupt reads HID class parks must ask
    /// for the whole report, or the device answers each with a cut one.</summary>
    static void LiveWide(HMContext ctx, HMBackend backend, bool hover)
    {
        var profile = ctx.GetProfile("kill-elan-xps9360")!;
        var t = profile.Touch;
        Check("the Elan touchscreen's layout: ten contacts a report", t is { IsTouchpad: false, ContactsPerReport: 10 },
              t == null ? "no touch layout" : $"{t.ContactsPerReport} per report");
        if (t == null) return;
        using var c = ctx.CreateController(profile, null, backend);
        Thread.Sleep(2000);
        using var touch = OpenChild("VID_1209&PID_000B", "0004");
        Check("its touch screen collection opens, a 116-byte input report", touch != null && touch.Caps.InputReportByteLength == 116,
              touch == null ? "none" : $"{touch.How}, {touch.Caps.InputReportByteLength} bytes");
        if (touch == null) return;
        if (c.UsbipHandle is { } handle)
        {
            var device = handle.Device;
            var type = device.GetType();
            var lockObj = type.GetField("_hidLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(device)!;
            var parked = (Dictionary<uint, int>)type.GetField("_parkedInLength", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(device)!;
            int[] asked;
            lock (lockObj) asked = parked.Values.ToArray();
            Check("on USB/IP, HID class's parked interrupt reads ask for the whole 116-byte report",
                  asked.Length > 0 && asked.All(n => n >= 116), asked.Length == 0 ? "no read parked" : $"asked {string.Join(" ", asked.Distinct())}");
        }
        ushort[] fingers = Enumerable.Range(0, touch.Links.Length).Where(i => touch.Links[i].LinkUsagePage == 0x0D && touch.Links[i].LinkUsage == 0x22)
                                     .Select(i => (ushort)i).ToArray();
        int y = t.Y.Minimum + (t.Y.Maximum - t.Y.Minimum) / 10, step = (t.X.Maximum - t.X.Minimum) / 40;
        var contacts = Enumerable.Range(0, 3).Select(i => new HMTouchContact { Id = i, X = At(t.X, 2) + (i - 1) * step, Y = y, Hover = hover }).ToArray();
        var sw = Stopwatch.StartNew();
        c.SubmitTouch(contacts);
        long took = sw.ElapsedMilliseconds;
        Thread.Sleep(50);
        var r = touch.Input(0x01);
        string Show(byte[] rep) => string.Join(", ", fingers.Take(3).Select(f =>
            $"id {touch.Value(rep, 0x0D, f, 0x51)} tip {(touch.On(rep, 0x0D, f, 0x42) ? 1 : 0)} ({touch.Value(rep, 0x01, f, 0x30)},{touch.Value(rep, 0x01, f, 0x31)})"))
            + $", count {touch.Value(rep, 0x0D, 0, 0x54)}, {rep.Length} bytes";
        Check($"three contacts {(hover ? "in range" : "down")} in a 116-byte report read back whole through HidP, Contact Count 3 (submit {took} ms)",
              r != null && r.Length == 116 && touch.Value(r, 0x0D, 0, 0x54) == 3
              && fingers.Take(3).Select(f => (touch.Value(r, 0x0D, f, 0x51), touch.Value(r, 0x01, f, 0x30), touch.On(r, 0x0D, f, 0x42)))
                        .SequenceEqual(contacts.Select(k => (k.Id, k.X, !hover))),
              r == null ? $"HidD_GetInputReport failed, Win32 {Marshal.GetLastPInvokeError()}" : Show(r));
        Check("the wide frame's submit waits for no delivery timeout", took < 500, $"{took} ms");
        c.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty);
        Thread.Sleep(50);
        r = touch.Input(0x01);
        Check("an empty submit lifts all three where they were, Contact Count 3",
              r != null && touch.Value(r, 0x0D, 0, 0x54) == 3 && fingers.Take(3).All(f => !touch.On(r, 0x0D, f, 0x42) && !touch.On(r, 0x0D, f, 0x32)),
              r == null ? "no read" : Show(r));
        Metrics("with the Elan touchscreen");
    }

    /// <summary>The input report lane at its limit: one 1024-byte report
    /// through SubmitRawExtendedReport, read back byte for byte, and a
    /// 1025-byte one refused. A vendor collection, so nothing reaches the
    /// desktop.</summary>
    static void LiveRaw(HMContext ctx, HMBackend backend)
    {
        var profile = ctx.GetProfile("spike-wide1024")!;
        using var c = ctx.CreateController(profile, null, backend);
        Thread.Sleep(2000);
        using var v = OpenChildPage("VID_1209&PID_000D", "FF00", "0001");
        Check("the 1024-byte vendor collection opens, a 1024-byte input report", v != null && v.Caps.InputReportByteLength == 1024,
              v == null ? "none" : $"{v.How}, {v.Caps.InputReportByteLength} bytes");
        if (v == null) return;
        if (c.UsbipHandle is { } handle)
        {
            var device = handle.Device;
            var type = device.GetType();
            var lockObj = type.GetField("_hidLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(device)!;
            var parked = (Dictionary<uint, int>)type.GetField("_parkedInLength", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(device)!;
            int[] asked;
            lock (lockObj) asked = parked.Values.ToArray();
            Check("on USB/IP, HID class's parked interrupt reads ask for the whole 1024-byte report",
                  asked.Length > 0 && asked.All(n => n >= 1024), asked.Length == 0 ? "no read parked" : $"asked {string.Join(" ", asked.Distinct())}");
        }
        var frame = new byte[1024];
        frame[0] = 1;
        for (int i = 1; i < frame.Length; i++) frame[i] = (byte)(i * 7 + 3);
        var sw = Stopwatch.StartNew();
        c.SubmitRawExtendedReport(frame);
        long took = sw.ElapsedMilliseconds;
        Thread.Sleep(50);
        var r = v.Input(1);
        int first = r == null ? -1 : Enumerable.Range(0, Math.Min(r.Length, frame.Length)).FirstOrDefault(i => r[i] != frame[i], -1);
        Check($"a 1024-byte report through SubmitRawExtendedReport reads back byte for byte (submit {took} ms)",
              r != null && r.Length == 1024 && r.AsSpan().SequenceEqual(frame),
              r == null ? $"HidD_GetInputReport failed, Win32 {Marshal.GetLastPInvokeError()}" : first < 0 ? $"{r.Length} bytes" : $"{r.Length} bytes, first difference at {first}");
        string? refusal = null;
        try { c.SubmitRawExtendedReport(new byte[1025]); }
        catch (ArgumentException e) { refusal = e.Message; }
        Check("a 1025-byte report is refused before any byte goes", refusal != null && refusal.Contains("1024-byte"), refusal ?? "accepted");
        r = v.Input(1);
        Check("after the refusal the device still holds the 1024-byte report", r != null && r.AsSpan().SequenceEqual(frame),
              r == null ? "no read" : $"{r.Length} bytes");
    }

    static void LivePen(HMContext ctx, HMBackend backend, bool hover)
    {
        var profile = ctx.GetProfile("spike-pen")!;
        var p = profile.Pen;
        Check("the spike pen's layout: pressure, tilt, barrel, eraser and invert", p is { Pressure: not null, TiltX: not null, HasInRange: true }
              && p.Buttons == (HMPenButtons.Barrel | HMPenButtons.Eraser | HMPenButtons.Invert), p == null ? "no pen layout" : $"buttons {p.Buttons}");
        if (p == null) return;
        using var c = ctx.CreateController(profile, null, backend);
        Thread.Sleep(2000);
        using var pen = OpenChild("VID_1209&PID_0008", "0002");
        Check("its pen collection opens", pen != null, pen?.How ?? "none");
        if (pen == null) return;
        int x = At(p.X, 2), y = p.Y.Minimum + (p.Y.Maximum - p.Y.Minimum) / 10;
        // Link 0 is the top collection. The stylus is its first child.
        ushort stylus = (ushort)Enumerable.Range(0, pen.Links.Length).FirstOrDefault(i => pen.Links[i].LinkUsagePage == 0x0D && pen.Links[i].LinkUsage == 0x20);
        string ShowS(byte[] rep) => $"tip {(pen.On(rep, 0x0D, stylus, 0x42) ? 1 : 0)} range {(pen.On(rep, 0x0D, stylus, 0x32) ? 1 : 0)} barrel {(pen.On(rep, 0x0D, stylus, 0x44) ? 1 : 0)} " +
                                    $"({pen.Value(rep, 0x01, stylus, 0x30)},{pen.Value(rep, 0x01, stylus, 0x31)}) pressure {pen.Value(rep, 0x0D, stylus, 0x30)} tilt {pen.Value(rep, 0x0D, stylus, 0x3D)}";

        c.SubmitPen(new HMPenState { InRange = true, Buttons = HMPenButtons.Barrel, X = x, Y = y, TiltX = 20 });
        Thread.Sleep(50);
        var r = pen.Input(0x01);
        Check("a pen in range with its barrel button reads back through HidP: range on, tip off, barrel on, its place, tilt 20",
              r != null && pen.On(r, 0x0D, stylus, 0x32) && !pen.On(r, 0x0D, stylus, 0x42) && pen.On(r, 0x0D, stylus, 0x44)
              && pen.Value(r, 0x01, stylus, 0x30) == x && pen.Value(r, 0x01, stylus, 0x31) == y && (sbyte)pen.Value(r, 0x0D, stylus, 0x3D) == 20,
              r == null ? $"HidD_GetInputReport failed, Win32 {Marshal.GetLastPInvokeError()}" : ShowS(r));
        if (!hover)
        {
            c.SubmitPen(new HMPenState { InRange = true, Tip = true, X = x, Y = y, Pressure = 200 });
            Thread.Sleep(50);
            r = pen.Input(0x01);
            Check("the tip down reads tip on, pressure 200", r != null && pen.On(r, 0x0D, stylus, 0x42) && pen.Value(r, 0x0D, stylus, 0x30) == 200,
                  r == null ? "no read" : ShowS(r));
        }
        c.SubmitPen(new HMPenState { InRange = false });
        Thread.Sleep(50);
        r = pen.Input(0x01);
        Check("out of range it reads every switch off at its last place", r != null && !pen.On(r, 0x0D, stylus, 0x32) && !pen.On(r, 0x0D, stylus, 0x42)
              && pen.Value(r, 0x01, stylus, 0x30) == x, r == null ? "no read" : ShowS(r));
        Metrics("with the pen");
    }

    // Microsoft's default certification blob (Windows Precision Touchpad
    // collection page), as mac-precision-touchpad's Hid.h carries it.
    const string PtpBlob = "fc28fe8440cb9a870dbe573cb670098807972d2be33834b66cedb0f7e59cf6c22e841be8b45178431f284b7c2d53affc47701b596f7443c4f34718531aa2a171c7950e315521d3b51ee90cbaecb889193eb3af75819d53b94157f46d3925297c87d9b498457da7269c653b856889d73bbdff1467f22bf02a4154f0fd2c667cf8c08f331303f1d3c10b89d91b62cd51b780b8af3a10c18a5be88a56f08caafa35e942c4d855c338cc2b535c6952d5c87302387c73b641e7ff05d82b799ae234608fa3321f097862bc80e30fbd65200813c1e2ee532d867ea75ac5d37d98be31481ffbdaafa2a86a89d6bff2d3322a9ae4cf17b7b8f4e13308248bc443a5e524c2";

    // The ITE 06CB:2968 precision touchpad, a USB device.
    const string PadDescriptor = "05010902a10185020901a10005091901290215002501750195028102950681010501093009311581257f750895028106c0c0050d0905a10185030922a10215002501094709429502750181029501750325050951810275019503810305011500261b047510550e651109303500466c039501810246db01263b0209318102050dc0550c66011047ffff000027ffff000075109501095681020954257f95017508810205090901250175019501810295078103050d85080955095975049502250fb102850d09607501950115002501b1029507b10385070600ff09c5150026ff007508960001b102c0050d090ea10185040922a10209521500250a75089501b102c00922a100850609570958750195022501b1029506b103c0c00600ff0901a10185090902150026ff00750895149102850a0903150026ff00750895149102850b0904150026ff007508951a8102850c0905150026ff007508951a8102850f0906150026ff0075089501b102850e0907150026ff0075089501b102c0";

    static void LivePad(HMContext ctx, HMBackend backend, bool hover)
    {
        var profile = ctx.GetProfile("spike-touchpad");
        Check("the spike touchpad loads", profile != null, profile == null ? "did not load" : "");
        if (profile == null) return;
        var t = profile.Touch;
        Check("its layout: a touchpad, 1 contact per report as Windows' hybrid form takes it, button 1", t is { IsTouchpad: true, ContactsPerReport: 1, Buttons: HMMouseButtons.Left },
              t == null ? "no touch layout" : $"{t.ContactsPerReport} per report, buttons {t.Buttons}");
        if (t == null) return;
        using var c = ctx.CreateController(profile, null, backend);
        var waited = Stopwatch.StartNew();
        while (c.TouchDeviceMode != 3 && waited.ElapsedMilliseconds < 8000) Thread.Sleep(20);
        Check("Windows took it for a precision touchpad: it wrote Device Mode 3 after reading the capabilities and the 257-byte certification report",
              c.TouchDeviceMode == 3, $"TouchDeviceMode {c.TouchDeviceMode?.ToString() ?? "never written"} after {waited.ElapsedMilliseconds} ms");
        using var pad = OpenChild("VID_1209&PID_0009", "0005");
        Check("its touch pad collection opens", pad != null, pad?.How ?? "none");
        if (pad == null) return;
        ushort[] fingers = Enumerable.Range(0, pad.Links.Length).Where(i => pad.Links[i].LinkUsagePage == 0x0D && pad.Links[i].LinkUsage == 0x22)
                                     .Select(i => (ushort)i).ToArray();
        if (hover) return;
        // The UMDF2 driver answers GET_INPUT_REPORT for report 1 alone
        // (driver.c, IOCTL_UMDF_HID_GET_INPUT_REPORT), and this touchpad
        // reports on 3, so only USB/IP reads the slide back. Windows reads
        // a touchpad's reports from its interrupt reads either way.
        if (backend != HMBackend.Usbip)
        {
            Console.WriteLine("  info: the slide is read back on USB/IP only: the UMDF2 driver answers an input report read for report 1 alone");
            return;
        }
        // One contact that slides and lifts, never a tap, in the middle.
        int y = At(t.Y, 2);
        byte[]? r = null;
        for (int step = 0; step <= 6; step++)
        {
            int x = t.X.Minimum + (int)((long)(t.X.Maximum - t.X.Minimum) * (40 + step * 3) / 100);
            c.SubmitTouch(new[] { new HMTouchContact { Id = 1, X = x, Y = y } });
            Thread.Sleep(50);
            if (step == 6) r = pad.Input(0x03);
        }
        int want = t.X.Minimum + (int)((long)(t.X.Maximum - t.X.Minimum) * 58 / 100);
        Check("a sliding contact reads back through HidP: id 1, tip and confidence on, its place, Contact Count 1",
              r != null && pad.Value(r, 0x0D, 0, 0x54) == 1 && fingers.Length == t.ContactsPerReport
              && pad.Value(r, 0x0D, fingers[0], 0x51) == 1 && pad.On(r, 0x0D, fingers[0], 0x42) && pad.On(r, 0x0D, fingers[0], 0x47)
              && pad.Value(r, 0x01, fingers[0], 0x30) == want && pad.Value(r, 0x01, fingers[0], 0x31) == y,
              r == null ? $"HidD_GetInputReport failed, Win32 {Marshal.GetLastPInvokeError()}"
                        : $"count {pad.Value(r, 0x0D, 0, 0x54)}, id {pad.Value(r, 0x0D, fingers[0], 0x51)} tip {pad.On(r, 0x0D, fingers[0], 0x42)} confidence {pad.On(r, 0x0D, fingers[0], 0x47)} ({pad.Value(r, 0x01, fingers[0], 0x30)},{pad.Value(r, 0x01, fingers[0], 0x31)}) scan {pad.Value(r, 0x0D, 0, 0x56)}");
        c.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty);
        Thread.Sleep(50);
        r = pad.Input(0x03);
        Check("the lift reads tip off with id 1 kept", r != null && !pad.On(r, 0x0D, fingers[0], 0x42) && pad.Value(r, 0x0D, fingers[0], 0x51) == 1,
              r == null ? "no read" : $"tip {pad.On(r, 0x0D, fingers[0], 0x42)} id {pad.Value(r, 0x0D, fingers[0], 0x51)}");
    }

    /// <summary>The spike's two profiles in a folder of their own.</summary>
    static string SpikeProfiles()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HIDMaestro_digitizer_spike");
        System.IO.Directory.CreateDirectory(dir);
        string touchStubs = "{ \"reports\": [ { \"id\": \"0x02\", \"size\": 2, \"data\": \"0202\", \"notify\": true }, " +
                            "{ \"id\": \"0x03\", \"size\": 3, \"data\": \"030000\", \"notify\": true } ] }";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spike-touchscreen.json"),
            Profile("spike-touchscreen", "HIDMaestro spike touchscreen", "0x0007", TouchDescriptor, touchStubs, "touchscreen"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spike-pen.json"),
            Profile("spike-pen", "HIDMaestro spike pen", "0x0008", PenDescriptor, null, "pen"));
        // A precision touchpad from the ITE 06CB:2968's
        // descriptor (hid-tools tests_kernel test_multitouch.py): device
        // capabilities 5 contacts and a click pad, Microsoft's default
        // certification blob (257 bytes with the ID), and the rest zero.
        string padStubs = "{ \"reports\": [ " + string.Join(", ", new[]
        {
            ("0x08", "0805"), ("0x07", "07" + PtpBlob), ("0x0D", "0D00"), ("0x04", "0400"), ("0x06", "0603"), ("0x0F", "0F00"), ("0x0E", "0E00"),
        }.Select(e => $"{{ \"id\": \"{e.Item1}\", \"size\": {e.Item2.Length / 2}, \"data\": \"{e.Item2}\", \"notify\": true }}")) + " ] }";
        string pad = Profile("spike-touchpad", "HIDMaestro spike touchpad", "0x0009", PadDescriptor, padStubs, "touchpad");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spike-touchpad.json"), pad);
        return dir;
    }
}
