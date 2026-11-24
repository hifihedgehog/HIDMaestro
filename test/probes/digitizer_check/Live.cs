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
        public readonly string How;

        public Child(string path)
        {
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
        string dir = SpikeProfiles();
        using var ctx = new HMContext(backend);
        ctx.InstallDriver();
        ctx.LoadProfilesFromDirectory(dir);
        LiveTouch(ctx, backend, hover);
        LivePen(ctx, backend, hover);
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
        return dir;
    }
}
