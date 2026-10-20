// Keyboards and mice through Raw Input (issue #67).
//
//   A. Four mice with one VID and PID and four identity keys are four Raw
//      Input devices, each with its own path, and the same four paths come
//      back when they are created again in another order.
//   B. A burst of 600 one-count movements arrives whole: every report, in
//      order, faster than Windows reads them.
//   C. Four threads driving their own mouse at 250 Hz at once lose nothing.
//   D. An absolute mouse reaches Raw Input as MOUSE_MOVE_ABSOLUTE.
//   E. Buttons past five arrive through a second, vendor collection.
//   F. Two keyboards, one without report IDs and one with Report ID 2. A
//      key reaches Raw Input as make then break (F24, which nothing on
//      Windows binds). Each holds the LED state Windows sent it on arrival.
//      Scroll Lock pressed on one reaches Windows, and Windows sends both
//      the new state.
//   G. A consumer usage arrives as a generic HID report.
//   H. Every keyboard and mouse in the catalog starts, appears to Raw Input
//      as its kind, and acknowledges delivery.
//   I. An output or feature report written through HidD_SetFeature,
//      HidD_SetOutputReport or WriteFile reaches OutputReceived split at
//      its first byte, with Report ID 5 and with no report IDs.
//   J. Dispose with a key held sends the key up first. A consumer killed
//      while holding F24, mouse button 6 and an absolute pointer leaves
//      F24 and button 6 up and the pointer where it was, through the
//      release frames the driver sends when the consumer's owner pipe
//      closes. A child process of this probe is the consumer. The cleanup
//      that follows removes all three of its devices, the keyboard
//      included, which Windows will not disable.
//
// The burst moves the cursor 300 counts and back, and D puts it at the
// center of the screen. The probe puts the cursor back at the end. No
// test clicks: mouse buttons 1 to 5 are never pressed. The only keys are
// F24 and Scroll Lock, pressed twice so its state ends where it began.
// Requires elevation. Exit 0 PASS / 1 FAIL / 2 when another SDK
// consumer is running.
//
// --leds-diag prints a timeline of the LED reports two keyboards receive,
// with each one's output ring read from its first slot, read or not.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

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
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    static HMProfile Custom(ushort pid, string name, string type, HidDescriptorBuilder d) =>
        new HMProfileBuilder().Id($"kbm-live-{pid:x4}").Name(name).Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(pid).ProductString(name).ManufacturerString("HIDMaestro")
            .Type(type).Connection("usb").FromDescriptorBuilder(d).Build();

    static string Tag(ushort vid, ushort pid) => $"VID_{vid:X4}&PID_{pid:X4}";

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    /// <summary>Root devices under ROOT\HIDCLASS whose hardware IDs carry
    /// this VID and PID, live or phantom.</summary>
    static List<string> RootDevices(string tag)
    {
        var ids = new List<string>();
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS");
        if (key == null) return ids;
        foreach (var inst in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(inst);
            if (sub?.GetValue("HardwareID") is string[] hw
                && hw.Any(h => h.Contains(tag, StringComparison.OrdinalIgnoreCase)))
                ids.Add($@"ROOT\HIDCLASS\{inst}");
        }
        return ids;
    }

    /// <summary>The device is neither live nor phantom, the test
    /// DeviceManager.RemoveDevice ends with.</summary>
    static bool DeviceGone(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, 0) != 0 && CM_Locate_DevNodeW(out _, instanceId, 1) != 0;

    /// <summary>A Raw Input device of this VID and PID. Plain HID profiles
    /// sit under the HIDClass enumerator, so the path does not carry them.</summary>
    static bool Is(string path, string tag)
    {
        if (path.Contains(tag, StringComparison.OrdinalIgnoreCase)) return true;
        var a = RawInputSink.Attributes(path);
        return a is { } v && Tag(v.Vid, v.Pid).Equals(tag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Raw Input devices that appeared since <paramref name="before"/>.</summary>
    static List<(IntPtr Handle, uint Type, string Name)> WaitNew(HashSet<string> before, Func<(IntPtr Handle, uint Type, string Name), bool> want, int count, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        List<(IntPtr, uint, string)> found = new();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            found = RawInputSink.Devices().Where(d => !before.Contains(d.Name) && want(d)).ToList();
            if (found.Count >= count) return found;
            Thread.Sleep(100);
        }
        return found;
    }

    /// <summary>Windows reads a collection by the time Raw Input lists it.
    /// With --ready-timing, the first event submitted after the listing
    /// arrived in each of 30 lives of a keyboard, a mouse, a vendor
    /// collection and a consumer control, at most 27 ms after the listing
    /// (p95 26 ms), and every keyboard already held the LED state Windows
    /// sends one it has opened. So a keyboard waits for that state, up to
    /// the 1000 ms this waited before, and anything else 300 ms. A report
    /// sent before Windows reads the collection is lost, as a real device's
    /// is, and other work on the machine delays that read with no defect,
    /// so the wait is some 11 times the slowest of those readings.</summary>
    static void Readable(params HMController[] keyboards)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000 && keyboards.Any(k => k.KeyboardLeds == null)) Thread.Sleep(10);
        if (keyboards.Length == 0) Thread.Sleep(300);
    }

    static HashSet<string> Names() => RawInputSink.Devices().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which Raw Input mouse a controller drives: move it one count
    /// and back.</summary>
    static IntPtr Owner(RawInputSink sink, HMController c, IEnumerable<IntPtr> candidates)
    {
        var set = candidates.ToHashSet();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // A retry follows a move Windows did not report within 1500 ms.
            if (attempt > 0) Thread.Sleep(150);
            sink.Clear();
            c.SubmitMouse(new HMMouseState { X = 7 });
            IntPtr hit = IntPtr.Zero;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500 && hit == IntPtr.Zero)
            {
                foreach (var m in sink.Mice)
                    if (set.Contains(m.Device) && m.X > 0) { hit = m.Device; break; }
                Thread.Sleep(10);
            }
            c.SubmitMouse(new HMMouseState { X = -7 });
            if (hit != IntPtr.Zero) return hit;
        }
        return IntPtr.Zero;
    }

    /// <summary>"2x120 4x31 -2x200": each run of equal values and its length.</summary>
    static string Runs(IEnumerable<int> values)
    {
        var parts = new List<string>();
        int? cur = null; int n = 0;
        foreach (int v in values)
        {
            if (v == cur) { n++; continue; }
            if (cur is int c) parts.Add($"{c}x{n}");
            cur = v; n = 1;
        }
        if (cur is int last) parts.Add($"{last}x{n}");
        return string.Join(" ", parts);
    }

    /// <summary>Wait for a device's X deltas to stop arriving, and return
    /// how many came, their sum and the highest running total. Windows
    /// merges consecutive moves, so fewer than <paramref name="expectCount"/>
    /// messages carry every count: the wait also ends 300 ms after the
    /// counts are complete, back to 0 from <paramref name="peak"/>. A lost
    /// count keeps them incomplete, and the wait then runs to 1500 ms of
    /// quiet as before.</summary>
    static (int Count, long Sum, long Peak) Settle(RawInputSink sink, IntPtr device, int expectCount, long peak, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        int lastCount = -1;
        long quietSince = sw.ElapsedMilliseconds;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var (n, s, pk) = Totals(sink, device);
            if (n != lastCount) { lastCount = n; quietSince = sw.ElapsedMilliseconds; }
            bool complete = n >= expectCount || (s == 0 && pk == peak);
            if (complete && sw.ElapsedMilliseconds - quietSince > 300) break;
            if (sw.ElapsedMilliseconds - quietSince > 1500) break;
            Thread.Sleep(20);
        }
        return Totals(sink, device);
    }

    static (int Count, long Sum, long Peak) Totals(RawInputSink sink, IntPtr device)
    {
        long sum = 0, peak = 0; int count = 0;
        foreach (var m in sink.Mice.Where(m => m.Device == device))
        {
            sum += m.X; count++;
            peak = Math.Max(peak, sum);
        }
        return (count, sum, peak);
    }

    static int Main()
    {
        Console.WriteLine("=== Keyboards and mice through Raw Input (issue #67) ===");
        // The --hold-diag child runs beside its own parent, which has the SDK
        // loaded.
        string? consumer = Environment.GetCommandLineArgs().Contains("--hold-child") ? null : FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }
        // Every check here reads Raw Input. Exit 3, which the battery reads as
        // a skip, unlike the running consumer's 2.
        if (RawInputSink.NoRawInputHere(out string why))
        {
            Console.WriteLine($"  [SKIP] {why}.");
            return 3;
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        if (Environment.GetCommandLineArgs().Contains("--diag"))
        {
            var p = Custom(0xF0B0, "HIDMaestro Live Mouse", "mouse", new HidDescriptorBuilder().Mouse());
            var beforeD = Names();
            using var one = ctx.CreateController(p, "kbm-live-diag");
            Thread.Sleep(4000);
            Console.WriteLine($"instance {one.IdentityKey}");
            Console.WriteLine("New Raw Input devices:");
            foreach (var d in RawInputSink.Devices().Where(d => !beforeD.Contains(d.Name)))
                Console.WriteLine($"  type {d.Type} {d.Name}");
            Console.WriteLine("All Raw Input devices:");
            foreach (var d in RawInputSink.Devices())
                Console.WriteLine($"  type {d.Type} {RawInputSink.Attributes(d.Name)?.ToString() ?? "(no attributes)"} {d.Name}");
            Console.Out.Flush();
            Thread.Sleep(25000);
            return 0;
        }

        if (Environment.GetCommandLineArgs().Contains("--leds-diag"))
        {
            // When Windows sends a new keyboard its LED state: a timeline of
            // every LED report two keyboards receive, from the first's
            // creation, with the second created 8 s later.
            var clock = Stopwatch.StartNew();
            var p1 = Custom(0xF0B3, "HIDMaestro Live Keyboard", "keyboard", new HidDescriptorBuilder().Keyboard());
            var p2 = Custom(0xF0B5, "HIDMaestro Live Keyboard 2", "keyboard", new HidDescriptorBuilder().Keyboard(reportId: 2));
            var b1 = Names();
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  create keyboard 1");
            using var k1 = ctx.CreateController(p1, "kbm-live-keyboard");
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  created; KeyboardLeds={k1.KeyboardLeds?.ToString() ?? "null"}");
            k1.KeyboardLedsChanged += (_, l) => Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  keyboard 1 LEDs {l}");
            var l1 = WaitNew(b1, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, Tag(0xBEEF, 0xF0B3)), 1, 15000);
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  Raw Input lists keyboard 1 ({l1.Count})");
            while (clock.ElapsedMilliseconds < 8000) Thread.Sleep(50);
            var b2 = Names();
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  create keyboard 2");
            using var k2 = ctx.CreateController(p2, "kbm-live-keyboard-2");
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  created; KeyboardLeds={k2.KeyboardLeds?.ToString() ?? "null"}");
            foreach (var (k, name) in new[] { (k1, "keyboard 1"), (k2, "keyboard 2") })
            {
                // The ring from its first slot: what the driver published,
                // read or not.
                IntPtr view = HIDMaestro.Internal.SharedMemoryIO.EnsureOutputMapping(k.Index);
                uint seq = 0;
                var data = new byte[256];
                int n = 0;
                while (HIDMaestro.Internal.SharedMemoryIO.TryReadOutputFrame(view, ref seq, out byte src, out byte rid, out int size, data))
                {
                    n++;
                    Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  {name} ring slot {seq}: source {src} id 0x{rid:X2} [{Convert.ToHexString(data, 0, size)}]");
                }
                if (n == 0) Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  {name} ring empty (head {System.Runtime.InteropServices.Marshal.ReadInt32(view, 0)})");
            }
            k2.KeyboardLedsChanged += (_, l) => Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  keyboard 2 LEDs {l}");
            var l2 = WaitNew(b2, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, Tag(0xBEEF, 0xF0B5)), 1, 15000);
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  Raw Input lists keyboard 2 ({l2.Count})");
            while (clock.ElapsedMilliseconds < 16000) Thread.Sleep(50);
            Console.WriteLine($"{clock.ElapsedMilliseconds,6} ms  end; keyboard 1 {k1.KeyboardLeds?.ToString() ?? "null"}, keyboard 2 {k2.KeyboardLeds?.ToString() ?? "null"}");
            return 0;
        }

        if (Environment.GetCommandLineArgs().Contains("--hold-child"))
        {
            // Child of section J: hold F24, mouse button 6 and an absolute
            // pointer at the center of the screen until killed.
            var b0 = Names();
            var k = ctx.CreateController(Custom(0xF0B6, "HIDMaestro Live Hold Keyboard", "keyboard",
                new HidDescriptorBuilder().Keyboard()), "kbm-live-hold");
            var m = ctx.CreateController(Custom(0xF0B8, "HIDMaestro Live Hold Mouse", "mouse",
                new HidDescriptorBuilder().Mouse(reportId: 1).VendorButtons(3, 2)), "kbm-live-hold-mouse");
            var a = ctx.CreateController(Custom(0xF0B9, "HIDMaestro Live Hold Pointer", "mouse",
                new HidDescriptorBuilder().Mouse(absolute: true, wheel: false, pan: false)), "kbm-live-hold-pointer");
            WaitNew(b0, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, Tag(0xBEEF, 0xF0B6)), 1, 15000);
            WaitNew(b0, d => d.Type == RawInputSink.RIM_TYPEHID && Is(d.Name, Tag(0xBEEF, 0xF0B8)), 1, 15000);
            WaitNew(b0, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, Tag(0xBEEF, 0xF0B9)), 1, 15000);
            Readable(k);
            Readable();
            k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 });
            m.SubmitMouse(new HMMouseState { Buttons = HMMouseButtons.Button6 });
            a.SubmitMouse(new HMMouseState { X = 16384, Y = 16384 });
            Console.WriteLine($"HELD release-on-exit {(k.ReleasesOnConsumerExit ? "on" : "off")}");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (Environment.GetCommandLineArgs().Contains("--timing"))
        {
            // Five lives of one mouse: CreateController, until Raw Input
            // lists it, Dispose, until Raw Input drops it.
            var p = Custom(0xF0BA, "HIDMaestro Live Timing Mouse", "mouse", new HidDescriptorBuilder().Mouse());
            string tag = Tag(0xBEEF, 0xF0BA);
            for (int life = 1; life <= 5; life++)
            {
                var b0 = Names();
                var sw = Stopwatch.StartNew();
                var m = ctx.CreateController(p, "kbm-live-timing");
                long create = sw.ElapsedMilliseconds;
                while (sw.ElapsedMilliseconds < 15000 && !RawInputSink.Devices().Any(d => !b0.Contains(d.Name) && Is(d.Name, tag))) Thread.Sleep(5);
                long listed = sw.ElapsedMilliseconds;
                sw.Restart();
                m.Dispose();
                long dispose = sw.ElapsedMilliseconds;
                while (sw.ElapsedMilliseconds < 15000 && RawInputSink.Devices().Any(d => Is(d.Name, tag))) Thread.Sleep(5);
                Console.WriteLine($"life {life}: CreateController {create} ms, listed at {listed} ms, Dispose {dispose} ms, gone at {sw.ElapsedMilliseconds} ms");
            }
            return 0;
        }

        if (Environment.GetCommandLineArgs().Contains("--ready-timing"))
            return ReadyTiming(ctx);

        int kd = Array.IndexOf(Environment.GetCommandLineArgs(), "--key-diag");
        if (kd > 0 && kd + 1 < Environment.GetCommandLineArgs().Length)
        {
            // F24 make and break on a catalog keyboard, as Raw Input sees it.
            string id = Environment.GetCommandLineArgs()[kd + 1];
            var p = ctx.GetProfile(id) ?? throw new ArgumentException(id);
            using var ksink = new RawInputSink((0x01, 0x06));
            var b0 = Names();
            using var k = ctx.CreateController(p, "kbm-live-key");
            var dev = WaitNew(b0, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, Tag(p.VendorId, p.ProductId)), 1, 15000);
            Console.WriteLine($"Raw Input keyboards: {dev.Count}");
            Thread.Sleep(1000);
            ksink.Clear();
            k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 });
            k.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
            Thread.Sleep(500);
            var keys = ksink.Keys.Where(x => dev.Count == 1 && x.Device == dev[0].Handle).ToList();
            Console.WriteLine($"F24: {string.Join(", ", keys.Select(x => $"vk 0x{x.VKey:X2} flags {x.Flags}"))}{(keys.Count == 0 ? "no key message" : "")}");
            return 0;
        }

        int wd = Array.IndexOf(Environment.GetCommandLineArgs(), "--wheel-diag");
        if (wd > 0 && wd + 1 < Environment.GetCommandLineArgs().Length)
        {
            // What Windows sets on a mouse whose descriptor has a wheel
            // Resolution Multiplier, and what one wheel count becomes.
            string id = Environment.GetCommandLineArgs()[wd + 1];
            var p = ctx.GetProfile(id) ?? throw new ArgumentException(id);
            using var wsink = new RawInputSink((0x01, 0x02));
            var b0 = Names();
            using var m = ctx.CreateController(p, "kbm-live-wheel");
            var outs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            m.OutputReceived += (_, pkt) => outs.Enqueue($"{pkt.Source} 0x{pkt.ReportId:X2} [{Convert.ToHexString(pkt.Data.Span)}]");
            var dev = WaitNew(b0, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, Tag(p.VendorId, p.ProductId)), 1, 15000);
            Console.WriteLine($"Raw Input mice: {dev.Count}");
            Thread.Sleep(1500);
            IntPtr view = HIDMaestro.Internal.SharedMemoryIO.EnsureOutputMapping(m.Index);
            uint seq = 0; var data = new byte[256];
            while (HIDMaestro.Internal.SharedMemoryIO.TryReadOutputFrame(view, ref seq, out byte src, out byte rid, out int size, data))
                Console.WriteLine($"ring slot {seq}: source {src} id 0x{rid:X2} [{Convert.ToHexString(data, 0, size)}]");
            foreach (int w in new[] { 1, -1, 4, -4 })
            {
                wsink.Clear();
                m.SubmitMouse(new HMMouseState { Wheel = w });
                Thread.Sleep(300);
                var ev = wsink.Mice.Where(x => dev.Count == 1 && x.Device == dev[0].Handle && (x.ButtonFlags & 0x0400) != 0).ToList();
                Console.WriteLine($"Wheel {w,2}: {string.Join(", ", ev.Select(x => $"delta {x.ButtonData}"))}{(ev.Count == 0 ? "no wheel message" : "")}");
            }
            foreach (var (mx, my) in new[] { (10, -5), (-10, 5), (300, 0), (-300, 0) })
            {
                wsink.Clear();
                m.SubmitMouse(new HMMouseState { X = mx, Y = my });
                Thread.Sleep(300);
                var ev = wsink.Mice.Where(x => dev.Count == 1 && x.Device == dev[0].Handle).ToList();
                Console.WriteLine($"Move {mx},{my}: sum {ev.Sum(x => x.X)},{ev.Sum(x => x.Y)} in {ev.Count} messages");
            }
            return 0;
        }

        using var sink = new RawInputSink((0x01, 0x02), (0x01, 0x06), (0xFF00, 0x01), (0x0C, 0x01));

        // B and D move the real cursor. It goes back where it was.
        bool haveCursor = GetCursorPos(out POINT cursor);
        try
        {
            FourMice(ctx, sink);
            Absolute(ctx, sink);
            ExtraButtons(ctx, sink);
            Keyboard(ctx, sink);
            Consumer(ctx, sink);
            OutputSplit(ctx);
            Catalog(ctx);
            OwnerExit(ctx, sink);
        }
        catch (Exception ex)
        {
            Check("the probe ran without throwing", false, ex.ToString());
        }
        finally
        {
            if (haveCursor) SetCursorPos(cursor.X, cursor.Y);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── A, B and C ───────────────────────────────────────────────────────

    static void FourMice(HMContext ctx, RawInputSink sink)
    {
        Console.WriteLine("\n== A. Four mice, one VID and PID, four identity keys ==");
        var profile = Custom(0xF0B0, "HIDMaestro Live Mouse", "mouse", new HidDescriptorBuilder().Mouse());
        string tag = Tag(0xBEEF, 0xF0B0);
        string[] keys = { "kbm-live-mouse-1", "kbm-live-mouse-2", "kbm-live-mouse-3", "kbm-live-mouse-4" };

        var before = Names();
        var pads = keys.Select(k => ctx.CreateController(profile, k)).ToList();
        var mice = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, tag), 4, 15000);
        Check("Raw Input lists four new mice", mice.Count == 4, $"{mice.Count}");
        Check("each with its own device path", mice.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4);
        var pathByKey = new Dictionary<string, string>();
        for (int i = 0; i < pads.Count; i++)
        {
            var h = Owner(sink, pads[i], mice.Select(m => m.Handle));
            pathByKey[keys[i]] = mice.FirstOrDefault(m => m.Handle == h).Name ?? "";
        }
        Check("each controller drives one of them", pathByKey.Values.Where(v => v.Length > 0).Distinct().Count() == 4);

        Console.WriteLine("\n== B. 600 one-count movements as fast as SubmitMouse returns ==");
        var first = pads[0];
        var dev = mice.First(m => m.Name == pathByKey[keys[0]]).Handle;
        Thread.Sleep(200);
        sink.Clear();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++) first.SubmitMouse(new HMMouseState { X = 1 });
        for (int i = 0; i < 300; i++) first.SubmitMouse(new HMMouseState { X = -1 });
        sw.Stop();
        var (count, sum, peak) = Settle(sink, dev, 600, 300, 8000);
        Console.WriteLine($"  sent in {sw.ElapsedMilliseconds} ms ({600_000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0} reports/s), DeliversEveryReport={first.DeliversEveryReport}");
        Console.WriteLine($"  Raw Input delivered {count} messages; Windows merges consecutive moves (RAWMOUSE has MOUSE_MOVE_NOCOALESCE for that reason)");
        Check("the controller delivers every report", first.DeliversEveryReport);
        Check("SubmitMouse paces reports to one per millisecond", sw.ElapsedMilliseconds >= 590, $"{sw.ElapsedMilliseconds} ms");
        Check("every count arrived: 300 out, then 300 back", peak == 300 && sum == 0, $"peak {peak}, sum {sum}");

        Console.WriteLine("\n== C. Four threads, one mouse each, 250 Hz for a second each way ==");
        Thread.Sleep(200);
        sink.Clear();
        var devs = keys.Select(k => mice.First(m => m.Name == pathByKey[k]).Handle).ToArray();
        var threads = pads.Select((p, i) => new Thread(() =>
        {
            var next = Stopwatch.GetTimestamp();
            long step = Stopwatch.Frequency / 250;
            for (int n = 0; n < 500; n++)
            {
                p.SubmitMouse(new HMMouseState { X = n < 250 ? 2 : -2 });
                next += step;
                while (Stopwatch.GetTimestamp() < next) Thread.Sleep(0);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        for (int i = 0; i < 4; i++)
        {
            var (c, s, pk) = Settle(sink, devs[i], 500, 500, 6000);
            Check($"mouse {i + 1}: every count arrived, 500 out and back", pk == 500 && s == 0, $"{c} messages, peak {pk}, sum {s}");
            if (pk != 500 || s != 0)
            {
                // What arrived, as runs of equal deltas, and what any mouse
                // outside the test did in the same window.
                Console.WriteLine("    deltas: " + Runs(sink.Mice.Where(m => m.Device == devs[i]).Select(m => m.X)));
                var others = sink.Mice.Where(m => !devs.Contains(m.Device) && (m.X != 0 || m.Y != 0)).ToList();
                Console.WriteLine($"    other mice: {others.Count} moves, X sum {others.Sum(m => (long)m.X)}");
            }
        }

        Console.WriteLine("\n== A, again: the same keys in reverse order ==");
        foreach (var p in pads) p.Dispose();
        WaitGone(tag, 10000);
        before = Names();
        var again = keys.Reverse().Select(k => (Key: k, Pad: ctx.CreateController(profile, k))).ToList();
        var mice2 = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, tag), 4, 15000);
        int same = 0;
        foreach (var (k, p) in again)
        {
            var h = Owner(sink, p, mice2.Select(m => m.Handle));
            string path = mice2.FirstOrDefault(m => m.Handle == h).Name ?? "";
            if (string.Equals(path, pathByKey[k], StringComparison.OrdinalIgnoreCase)) same++;
        }
        Check("each identity key brings back its own device path", same == 4, $"{same} of 4");
        foreach (var (_, p) in again) p.Dispose();
        WaitGone(tag, 10000);
    }

    static void WaitGone(string tag, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs
               && RawInputSink.Devices().Any(d => Is(d.Name, tag)))
            Thread.Sleep(100);
    }

    // ── D ────────────────────────────────────────────────────────────────

    static void Absolute(HMContext ctx, RawInputSink sink)
    {
        Console.WriteLine("\n== D. An absolute mouse ==");
        var profile = Custom(0xF0B1, "HIDMaestro Live Absolute Mouse", "mouse",
                             new HidDescriptorBuilder().Mouse(absolute: true, wheel: false, pan: false));
        string tag = Tag(0xBEEF, 0xF0B1);
        var before = Names();
        using var pad = ctx.CreateController(profile, "kbm-live-absolute");
        var dev = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, tag), 1, 15000);
        Check("Raw Input lists it as a mouse", dev.Count == 1);
        if (dev.Count == 1)
        {
            Readable();
            sink.Clear();
            pad.SubmitMouse(new HMMouseState { X = 16384, Y = 16384 });
            var sw = Stopwatch.StartNew();
            RawInputSink.Mouse? m = null;
            while (sw.ElapsedMilliseconds < 2000 && m == null)
            {
                m = sink.Mice.FirstOrDefault(x => x.Device == dev[0].Handle);
                Thread.Sleep(20);
            }
            Check("the report arrives flagged MOUSE_MOVE_ABSOLUTE", m != null && (m.Flags & 0x01) != 0, m == null ? "none" : $"flags 0x{m.Flags:X}");
            Check("at the middle of the 0-65535 range", m != null && Math.Abs(m.X - 32768) < 64 && Math.Abs(m.Y - 32768) < 64,
                  m == null ? "" : $"{m.X}, {m.Y}");
        }
        pad.Dispose();
        WaitGone(tag, 10000);
    }

    // ── E ────────────────────────────────────────────────────────────────

    static void ExtraButtons(HMContext ctx, RawInputSink sink)
    {
        Console.WriteLine("\n== E. Buttons past five ==");
        var profile = Custom(0xF0B2, "HIDMaestro Live Eight-Button Mouse", "mouse",
                             new HidDescriptorBuilder().Mouse(reportId: 1).VendorButtons(3, 2));
        string tag = Tag(0xBEEF, 0xF0B2);
        var before = Names();
        using var pad = ctx.CreateController(profile, "kbm-live-buttons");
        // Windows enumerates the two collections on their own, so each one
        // is waited for: the vendor one can arrive before the mouse.
        var vendor = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEHID && Is(d.Name, tag), 1, 15000);
        var mouse = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, tag), 1, 15000);
        Check("the mouse collection is a Raw Input mouse", mouse.Count == 1);
        Check("the vendor collection is a Raw Input HID device of its own", vendor.Count == 1);
        if (vendor.Count == 1)
        {
            Readable();
            sink.Clear();
            pad.SubmitMouse(new HMMouseState { Buttons = HMMouseButtons.Button6 | HMMouseButtons.Button8 });
            pad.SubmitMouse(new HMMouseState());
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && sink.Reports.Count(r => r.Device == vendor[0].Handle) < 2) Thread.Sleep(20);
            var reports = sink.Reports.Where(r => r.Device == vendor[0].Handle).Select(r => Convert.ToHexString(r.Report)).ToList();
            Check("buttons 6 and 8 arrive as report 2, 0x05, then the release",
                  reports.Count >= 2 && reports[0] == "0205" && reports[^1] == "0200", string.Join(",", reports));
        }
        pad.Dispose();
        WaitGone(tag, 10000);
    }

    // ── F ────────────────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    const int VK_SCROLL = 0x91, VK_NUMLOCK = 0x90, VK_CAPITAL = 0x14;
    static bool ScrollLockOn() => (GetKeyState(VK_SCROLL) & 1) != 0;

    /// <summary>The lock LEDs Windows holds right now.</summary>
    static HMKeyboardLeds LockState() =>
        ((GetKeyState(VK_NUMLOCK) & 1) != 0 ? HMKeyboardLeds.NumLock : 0)
        | ((GetKeyState(VK_CAPITAL) & 1) != 0 ? HMKeyboardLeds.CapsLock : 0)
        | ((GetKeyState(VK_SCROLL) & 1) != 0 ? HMKeyboardLeds.ScrollLock : 0);

    /// <summary>Wait for an LED report whose Scroll Lock bit is <paramref name="on"/>.</summary>
    static bool WaitLeds(System.Collections.Concurrent.ConcurrentQueue<HMKeyboardLeds> q, bool on, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (q.Any(l => l.HasFlag(HMKeyboardLeds.ScrollLock) == on)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    static void PressScrollLock(HMController pad)
    {
        pad.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.ScrollLock });
        pad.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
    }

    sealed class Watched
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<HMKeyboardLeds> Leds = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Outputs = new();
        public void Attach(HMController c)
        {
            c.KeyboardLedsChanged += (_, l) => Leds.Enqueue(l);
            c.OutputReceived += (_, pkt) => Outputs.Enqueue($"{pkt.Source} 0x{pkt.ReportId:X2} [{Convert.ToHexString(pkt.Data.Span)}]");
        }
        public void Clear()
        {
            while (Leds.TryDequeue(out _)) { }
            while (Outputs.TryDequeue(out _)) { }
        }
        public string Seen => Outputs.IsEmpty ? "no output report" : string.Join("; ", Outputs);
    }

    static void Keyboard(HMContext ctx, RawInputSink sink)
    {
        Console.WriteLine("\n== F. Keyboards, one without report IDs and one with Report ID 2 ==");
        var p1 = Custom(0xF0B3, "HIDMaestro Live Keyboard", "keyboard", new HidDescriptorBuilder().Keyboard());
        var p2 = Custom(0xF0B5, "HIDMaestro Live Keyboard 2", "keyboard", new HidDescriptorBuilder().Keyboard(reportId: 2));
        string tag1 = Tag(0xBEEF, 0xF0B3), tag2 = Tag(0xBEEF, 0xF0B5);
        var before = Names();
        var w1 = new Watched();
        var w2 = new Watched();
        using var k1 = ctx.CreateController(p1, "kbm-live-keyboard");
        w1.Attach(k1);
        using var k2 = ctx.CreateController(p2, "kbm-live-keyboard-2");
        w2.Attach(k2);
        var dev1 = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, tag1), 1, 15000);
        var dev2 = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, tag2), 1, 15000);
        Check("Raw Input lists both as keyboards", dev1.Count == 1 && dev2.Count == 1, $"{dev1.Count}, {dev2.Count}");
        if (dev1.Count == 1 && dev2.Count == 1)
        {
            Readable(k1, k2);
            sink.Clear();
            k1.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 });
            k1.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && !sink.Keys.Any(k => k.Device == dev1[0].Handle && (k.Flags & 1) == 1)) Thread.Sleep(20);
            var keys = sink.Keys.Where(k => k.Device == dev1[0].Handle).ToList();
            // Windows repeats a held key after the keyboard delay, 250 ms at
            // the shortest. When other work holds this thread that long
            // between the two submits, the make repeats before the break,
            // and those repeats pass. No make, no break, a break before a
            // make or another key still fails.
            Check("F24 arrives as make then break (VK_F24)",
                  keys.Count >= 2 && keys.All(k => k.VKey == 0x87) && keys.Take(keys.Count - 1).All(k => (k.Flags & 1) == 0)
                  && (keys[^1].Flags & 1) == 1,
                  string.Join(", ", keys.Select(k => $"vk 0x{k.VKey:X2} flags {k.Flags}")));

            // Windows sends each keyboard the lock state as it arrives,
            // before CreateController returns.
            var held = LockState();
            Check($"each keyboard holds the state Windows sent it on arrival ({held})",
                  k1.KeyboardLeds == held && k2.KeyboardLeds == held,
                  $"{k1.KeyboardLeds?.ToString() ?? "null"}, {k2.KeyboardLeds?.ToString() ?? "null"}");

            // Scroll Lock pressed on keyboard 1: Windows toggles the lock and
            // sends the new state to every keyboard. Pressed twice, so the
            // state ends where it began.
            bool initial = ScrollLockOn();
            w1.Clear();
            w2.Clear();
            PressScrollLock(k1);
            bool got1 = WaitLeds(w1.Leds, !initial, 3000), got2 = WaitLeds(w2.Leds, !initial, 3000);
            var want = held ^ HMKeyboardLeds.ScrollLock;
            Check($"Scroll Lock {(initial ? "off" : "on")}: the keyboard without report IDs reads {want}",
                  got1 && k1.KeyboardLeds == want, w1.Seen);
            Check($"Scroll Lock {(initial ? "off" : "on")}: the keyboard with Report ID 2 reads {want}",
                  got2 && k2.KeyboardLeds == want, w2.Seen);

            w1.Clear();
            w2.Clear();
            PressScrollLock(k1);
            bool back1 = WaitLeds(w1.Leds, initial, 3000), back2 = WaitLeds(w2.Leds, initial, 3000);
            var rw = Stopwatch.StartNew();
            while (rw.ElapsedMilliseconds < 1000 && ScrollLockOn() != initial) Thread.Sleep(20);
            Check($"pressing it again restores Scroll Lock {(initial ? "on" : "off")} on both",
                  back1 && back2 && ScrollLockOn() == initial && k1.KeyboardLeds == held && k2.KeyboardLeds == held,
                  $"{w1.Seen} | {w2.Seen}");
        }
        k1.Dispose();
        k2.Dispose();
        WaitGone(tag1, 10000);
        WaitGone(tag2, 10000);
    }

    // ── G ────────────────────────────────────────────────────────────────

    static void Consumer(HMContext ctx, RawInputSink sink)
    {
        Console.WriteLine("\n== G. Consumer controls ==");
        var profile = Custom(0xF0B4, "HIDMaestro Live Consumer Control", "keyboard", new HidDescriptorBuilder().ConsumerControl());
        string tag = Tag(0xBEEF, 0xF0B4);
        var before = Names();
        using var pad = ctx.CreateController(profile, "kbm-live-consumer");
        var dev = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEHID && Is(d.Name, tag), 1, 15000);
        Check("Raw Input lists the consumer collection", dev.Count == 1);
        if (dev.Count == 1)
        {
            Readable();
            sink.Clear();
            // 0x0069 Red Menu Button: a usage Windows binds to nothing.
            pad.SubmitConsumer(new ushort[] { 0x0069 });
            pad.SubmitConsumer(ReadOnlySpan<ushort>.Empty);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && sink.Reports.Count(r => r.Device == dev[0].Handle) < 2) Thread.Sleep(20);
            var reports = sink.Reports.Where(r => r.Device == dev[0].Handle).Select(r => Convert.ToHexString(r.Report)).ToList();
            // Raw Input's RAWHID report starts with the report ID, 0 here.
            Check("usage 0x0069 arrives, then the release", reports.Count == 2 && reports[0] == "006900" && reports[1] == "000000",
                  string.Join(",", reports));
        }
        pad.Dispose();
        WaitGone(tag, 10000);
    }

    // ── I ────────────────────────────────────────────────────────────────

    /// <summary>A vendor collection with one 8-byte input, output and
    /// feature report, with Report ID <paramref name="reportId"/> or none.</summary>
    static byte[] VendorDescriptor(byte reportId)
    {
        var d = new List<byte> { 0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01 };
        if (reportId != 0) d.AddRange(new byte[] { 0x85, reportId });
        d.AddRange(new byte[]
        {
            0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x08,
            0x09, 0x02, 0x81, 0x02,     // Input
            0x09, 0x03, 0x91, 0x02,     // Output
            0x09, 0x04, 0xB1, 0x02,     // Feature
            0xC0,
        });
        return d.ToArray();
    }

    static void OutputSplit(HMContext ctx)
    {
        Console.WriteLine("\n== I. How an output or feature report reaches OutputReceived ==");
        // 0x21 first: clear of the PID report IDs the driver acts on.
        byte[] body = { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28 };
        foreach (byte rid in new byte[] { 0, 5 })
        {
            ushort pid = (ushort)(0xF0C0 + rid);
            var profile = new HMProfileBuilder().Id($"kbm-live-{pid:x4}").Name($"HIDMaestro Live Vendor {rid}").Vendor("HIDMaestro")
                .Vid(0xBEEF).Pid(pid).ProductString($"HIDMaestro Live Vendor {rid}").ManufacturerString("HIDMaestro")
                .Type("other").Connection("usb").Descriptor(VendorDescriptor(rid)).InputReportSize(8 + (rid != 0 ? 1 : 0)).Build();
            string tag = Tag(0xBEEF, pid);
            var before = Names();
            var got = new System.Collections.Concurrent.ConcurrentQueue<HMOutputPacket>();
            using (var pad = ctx.CreateController(profile, $"kbm-live-vendor-{rid}"))
            {
                pad.OutputReceived += (_, pkt) => got.Enqueue(new HMOutputPacket(pkt.Source, pkt.ReportId, pkt.Data.ToArray(), pkt.SeqNo));
                var dev = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEHID && Is(d.Name, tag), 1, 15000);
                if (dev.Count != 1) { Check($"Report ID {rid}: the vendor collection appears", false); continue; }
                Thread.Sleep(500);
                IntPtr h = RawInputSink.OpenReadWrite(dev[0].Name);
                if (h == new IntPtr(-1)) { Check($"Report ID {rid}: the vendor collection opens for writing", false); continue; }
                try
                {
                    // What an application hands Windows: the Report ID
                    // first, 0 when the descriptor declares none.
                    var buf = new byte[9];
                    buf[0] = rid;
                    body.CopyTo(buf, 1);
                    var calls = new (string Name, HMOutputSource Source, Func<bool> Call)[]
                    {
                        ("HidD_SetFeature", HMOutputSource.HidFeature, () => RawInputSink.SetFeature(h, buf)),
                        ("HidD_SetOutputReport", HMOutputSource.HidOutput, () => RawInputSink.SetOutputReport(h, buf)),
                        ("WriteFile", HMOutputSource.HidOutput, () => RawInputSink.Write(h, buf)),
                    };
                    // Report ID 0: the device receives the 8 bytes alone, and
                    // the packet carries the first in ReportId.
                    byte wantId = rid != 0 ? rid : body[0];
                    string wantData = Convert.ToHexString(rid != 0 ? body : body[1..]);
                    foreach (var (name, source, call) in calls)
                    {
                        while (got.TryDequeue(out _)) { }
                        bool sent = call();
                        var sw = Stopwatch.StartNew();
                        HMOutputPacket p = default;
                        bool have = false;
                        while (sw.ElapsedMilliseconds < 2000 && !(have = got.TryDequeue(out p))) Thread.Sleep(10);
                        Check($"Report ID {rid}, {name}: ReportId 0x{wantId:X2}, Data {wantData}",
                              sent && have && p.Source == source && p.ReportId == wantId && Convert.ToHexString(p.Data.Span) == wantData,
                              !sent ? "the call failed" : !have ? "no packet" : $"{p.Source} 0x{p.ReportId:X2} {Convert.ToHexString(p.Data.Span)}");
                    }
                }
                finally { RawInputSink.Close(h); }
            }
            WaitGone(tag, 10000);
        }
    }

    // ── J ────────────────────────────────────────────────────────────────

    static void OwnerExit(HMContext ctx, RawInputSink sink)
    {
        Console.WriteLine("\n== J. A consumer that goes away with input held ==");

        // Dispose with a key down sends the key up first.
        var kp = Custom(0xF0B7, "HIDMaestro Live Dispose Keyboard", "keyboard", new HidDescriptorBuilder().Keyboard());
        string ktag = Tag(0xBEEF, 0xF0B7);
        var before = Names();
        var kb = ctx.CreateController(kp, "kbm-live-dispose");
        var kd = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, ktag), 1, 15000);
        if (kd.Count == 1)
        {
            Readable(kb);
            kb.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 });
            Thread.Sleep(100);
            sink.Clear();
            kb.Dispose();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2000 && !sink.Keys.Any(k => k.Device == kd[0].Handle && k.VKey == 0x87 && (k.Flags & 1) == 1))
                Thread.Sleep(20);
            Check("Dispose with F24 held sends F24 up first",
                  sink.Keys.Any(k => k.Device == kd[0].Handle && k.VKey == 0x87 && (k.Flags & 1) == 1));
        }
        else
        {
            Check("Raw Input lists the keyboard", false);
            kb.Dispose();
        }
        WaitGone(ktag, 10000);

        // A consumer killed with F24, button 6 and the pointer held.
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--hold-child")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string? line;
        while ((line = child.StandardOutput.ReadLine()) != null && !line.StartsWith("HELD")) { }
        if (line == null)
        {
            Check("the child holds its input", false, "it exited first");
            return;
        }
        Check("the driver watches the consumer's exit", line.EndsWith(" on"), line);
        var held = new[] { 0xF0B6, 0xF0B8, 0xF0B9 }.SelectMany(p => RootDevices(Tag(0xBEEF, (ushort)p)))
                                                   .Where(id => CM_Locate_DevNodeW(out _, id, 0) == 0).ToList();
        var devs = RawInputSink.Devices();
        IntPtr Find(uint type, ushort pid) => devs.FirstOrDefault(d => d.Type == type && Is(d.Name, Tag(0xBEEF, pid))).Handle;
        IntPtr hk = Find(RawInputSink.RIM_TYPEKEYBOARD, 0xF0B6), hv = Find(RawInputSink.RIM_TYPEHID, 0xF0B8),
               hp = Find(RawInputSink.RIM_TYPEMOUSE, 0xF0B9);
        // Until F24 repeats: Windows repeats a held key after the keyboard
        // delay, 250 to 1000 ms.
        int Repeats() => sink.Keys.Count(k => k.Device == hk && k.VKey == 0x87 && (k.Flags & 1) == 0);
        var heldFor = Stopwatch.StartNew();
        while (heldFor.ElapsedMilliseconds < 1500 && Repeats() < 3) Thread.Sleep(20);
        int repeats = Repeats();
        long heldMs = heldFor.ElapsedMilliseconds;
        sink.Clear();
        var clock = Stopwatch.StartNew();
        child.Kill();
        child.WaitForExit();
        // The driver's release frames: F24 up, button 6 up and the pointer
        // where it was. Then 500 ms, ten of Windows' key repeats, for a key
        // that goes down again or a pointer that moves after them.
        long released = -1;
        while (clock.ElapsedMilliseconds < 3000)
        {
            if (released < 0
                && sink.Keys.Any(k => k.Device == hk && k.VKey == 0x87 && (k.Flags & 1) == 1)
                && sink.Reports.Any(r => r.Device == hv && Convert.ToHexString(r.Report) == "0200")
                && sink.Mice.Any(x => x.Device == hp))
                released = clock.ElapsedMilliseconds;
            if (released >= 0 && clock.ElapsedMilliseconds - released >= 500) break;
            Thread.Sleep(20);
        }
        var keys = sink.Keys.Where(k => k.Device == hk && k.VKey == 0x87).ToList();
        int up = keys.FindIndex(k => (k.Flags & 1) == 1);
        Console.WriteLine($"  held: {repeats} F24 repeats in {heldMs} ms; after the kill: {string.Join(" ", keys.Select(k => (k.Flags & 1) == 1 ? "up" : "down"))}");
        Check("F24 goes up when the consumer is killed, and stops repeating",
              up >= 0 && keys.Skip(up + 1).All(k => (k.Flags & 1) == 1), $"{keys.Count} F24 messages");
        var vendor = sink.Reports.Where(r => r.Device == hv).Select(r => Convert.ToHexString(r.Report)).ToList();
        Check("button 6 goes up (vendor report 2, 0x00)", vendor.Contains("0200"), string.Join(",", vendor));
        var moves = sink.Mice.Where(x => x.Device == hp).ToList();
        Check("the absolute pointer stays where it was",
              moves.All(x => Math.Abs(x.X - 32768) < 64 && Math.Abs(x.Y - 32768) < 64),
              moves.Count == 0 ? "no report" : string.Join(", ", moves.Select(x => $"{x.X},{x.Y}")));

        // The cleanup removes every device the dead consumer left. Windows
        // will not disable a keyboard, so the keyboard's root refuses the
        // plain disable and has to go the way Dispose removes it.
        var cleanup = Stopwatch.StartNew();
        HMContext.RemoveAllVirtualControllers();
        long cleanupMs = cleanup.ElapsedMilliseconds;
        var left = held.Where(id => !DeviceGone(id)).ToList();
        while (left.Count > 0 && cleanup.ElapsedMilliseconds < cleanupMs + 5000)
        {
            Thread.Sleep(100);
            left = held.Where(id => !DeviceGone(id)).ToList();
        }
        Check("the cleanup removes the dead consumer's keyboard and both mice",
              held.Count == 3 && left.Count == 0,
              left.Count > 0 ? "left: " + string.Join(", ", left) : $"{held.Count} devices, {cleanupMs} ms");
        WaitGone(Tag(0xBEEF, 0xF0B6), 10000);
    }

    // ── --ready-timing ───────────────────────────────────────────────────

    /// <summary>Thirty lives of a keyboard, a mouse, a mouse with a vendor
    /// collection and a consumer control, held together. Each life prints,
    /// per device, how long after Raw Input lists it the first of a
    /// repeated event arrives, and whether the keyboard already holds the
    /// LED state Windows sends it.</summary>
    static int ReadyTiming(HMContext ctx)
    {
        using var sink = new RawInputSink((0x01, 0x02), (0x01, 0x06), (0xFF00, 0x01), (0x0C, 0x01));
        var kp = Custom(0xF0D0, "HIDMaestro Ready Keyboard", "keyboard", new HidDescriptorBuilder().Keyboard());
        var mp = Custom(0xF0D1, "HIDMaestro Ready Mouse", "mouse", new HidDescriptorBuilder().Mouse());
        var vp = Custom(0xF0D2, "HIDMaestro Ready Buttons", "mouse", new HidDescriptorBuilder().Mouse(reportId: 1).VendorButtons(3, 2));
        var cp = Custom(0xF0D3, "HIDMaestro Ready Consumer", "keyboard", new HidDescriptorBuilder().ConsumerControl());
        var all = new List<long>[4] { new(), new(), new(), new() };
        int ledsAtList = 0;
        for (int life = 1; life <= 30; life++)
        {
            var before = Names();
            var k = ctx.CreateController(kp, "kbm-ready-k");
            var m = ctx.CreateController(mp, "kbm-ready-m");
            var v = ctx.CreateController(vp, "kbm-ready-v");
            var c = ctx.CreateController(cp, "kbm-ready-c");
            try
            {
                var lk = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && Is(d.Name, Tag(0xBEEF, 0xF0D0)), 1, 15000);
                var lm = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEMOUSE && Is(d.Name, Tag(0xBEEF, 0xF0D1)), 1, 15000);
                var lv = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEHID && Is(d.Name, Tag(0xBEEF, 0xF0D2)), 1, 15000);
                var lc = WaitNew(before, d => d.Type == RawInputSink.RIM_TYPEHID && Is(d.Name, Tag(0xBEEF, 0xF0D3)), 1, 15000);
                if (lk.Count + lm.Count + lv.Count + lc.Count != 4) { Console.WriteLine($"life {life}: not all listed"); continue; }
                if (k.KeyboardLeds != null) ledsAtList++;
                sink.Clear();
                var t = new long[4] { -1, -1, -1, -1 };
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 3000 && t.Any(x => x < 0))
                {
                    if (t[0] < 0) { k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 }); k.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty); }
                    if (t[1] < 0) { m.SubmitMouse(new HMMouseState { X = 1 }); m.SubmitMouse(new HMMouseState { X = -1 }); }
                    if (t[2] < 0) { v.SubmitMouse(new HMMouseState { Buttons = HMMouseButtons.Button6 }); v.SubmitMouse(new HMMouseState()); }
                    if (t[3] < 0) { c.SubmitConsumer(new ushort[] { 0x0069 }); c.SubmitConsumer(ReadOnlySpan<ushort>.Empty); }
                    Thread.Sleep(5);
                    long now = clock.ElapsedMilliseconds;
                    if (t[0] < 0 && sink.Keys.Any(x => x.Device == lk[0].Handle)) t[0] = now;
                    if (t[1] < 0 && sink.Mice.Any(x => x.Device == lm[0].Handle)) t[1] = now;
                    if (t[2] < 0 && sink.Reports.Any(x => x.Device == lv[0].Handle)) t[2] = now;
                    if (t[3] < 0 && sink.Reports.Any(x => x.Device == lc[0].Handle)) t[3] = now;
                }
                for (int i = 0; i < 4; i++) all[i].Add(t[i]);
                Console.WriteLine($"life {life}: keyboard {t[0]} ms, mouse {t[1]} ms, vendor {t[2]} ms, consumer {t[3]} ms after listing");
            }
            finally
            {
                ctx.DisposeControllersInParallel(new[] { k, m, v, c });
                foreach (ushort pid in new ushort[] { 0xF0D0, 0xF0D1, 0xF0D2, 0xF0D3 }) WaitGone(Tag(0xBEEF, pid), 10000);
            }
        }
        string[] names = { "keyboard", "mouse", "vendor", "consumer" };
        for (int i = 0; i < 4; i++)
        {
            var s = all[i].OrderBy(x => x).ToList();
            if (s.Count == 0) continue;
            Console.WriteLine($"{names[i]}: p50 {s[s.Count / 2]} ms, p95 {s[(int)Math.Ceiling(s.Count * 0.95) - 1]} ms, max {s[^1]} ms over {s.Count}");
        }
        Console.WriteLine($"keyboard LED state present when listed: {ledsAtList} of 30");
        return 0;
    }

    // ── H ────────────────────────────────────────────────────────────────

    static void Catalog(HMContext ctx)
    {
        var profiles = ctx.AllProfiles.Where(p => p.Inner.IsKeyboardOrMouse).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        Console.WriteLine($"\n== H. The catalog's {profiles.Count} keyboards and mice ==");
        var failures = new List<string>();
        var sw = Stopwatch.StartNew();
        foreach (var p in profiles)
        {
            string tag = Tag(p.VendorId, p.ProductId);
            uint want = p.Type == "mouse" ? RawInputSink.RIM_TYPEMOUSE : RawInputSink.RIM_TYPEKEYBOARD;
            var before = Names();
            HMController? pad = null;
            try
            {
                pad = ctx.CreateController(p, "kbm-live-catalog");
                var dev = WaitNew(before, d => d.Type == want && Is(d.Name, tag), 1, 10000);
                if (dev.Count == 0) { failures.Add($"{p.Id}: no new Raw Input {(want == 0 ? "mouse" : "keyboard")}"); continue; }
                // Two reports, so the second waits on the first's delivery.
                if (p.Type == "mouse")
                {
                    pad.SubmitMouse(new HMMouseState());
                    pad.SubmitMouse(new HMMouseState());
                }
                else
                {
                    pad.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
                    pad.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
                }
                var aw = Stopwatch.StartNew();
                while (!pad.DeliversEveryReport && aw.ElapsedMilliseconds < 2000) Thread.Sleep(20);
                if (!pad.DeliversEveryReport) failures.Add($"{p.Id}: no delivery acknowledgement");
            }
            catch (Exception ex) { failures.Add($"{p.Id}: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                pad?.Dispose();
                WaitGone(tag, 8000);
            }
        }
        foreach (var f in failures) Console.WriteLine($"    {f}");
        Check($"every one starts, reaches Raw Input as its kind and acknowledges delivery ({sw.Elapsed.TotalSeconds:F0} s)",
              failures.Count == 0, $"{failures.Count} failed");
    }
}
