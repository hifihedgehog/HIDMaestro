// Keyboard and mouse measurements that run on either backend, outside the
// battery's sections. Two of them need no Raw Input and run in session 0,
// as over SSH. The third runs in a signed-in user's session.
//
//   --order N        Four mice, each with a vendor collection carrying a
//                    running counter in buttons 6 to 13, 500 reports each
//                    at the fastest the SDK delivers them, every core kept
//                    busy, N rounds. Each vendor collection is read by its
//                    own reader: ReadFile on the collection's HID interface
//                    with 512 HID class buffers, at the highest thread
//                    priority. Raw Input reads every collection with
//                    ReadFile on that same interface (the system's own open
//                    of the collection), so the reader sees the stream HID
//                    class keeps for every reader of the collection, in the
//                    order HID class received it, and needs no session with
//                    input. A round of one mouse fails when fewer than 499
//                    counters arrive (lost) or one does not follow its
//                    predecessor by one (swapped).
//   --dispose-ack N  A keyboard holds F24 and is disposed, N times. The
//                    keyboard must hold the LED state Windows sends it,
//                    decoded from its output report. The
//                    delivery acknowledgement must carry the last frame the
//                    controller wrote, and that frame must be the release
//                    (the keyboard report with no key), before the device
//                    goes: Dispose sends the release frames, waits for the
//                    host's next read after the last, then removes the
//                    device. Read from this program's own mappings of the
//                    input and acknowledgement sections, which outlive the
//                    controller's.
//   --held-on-kill   A child holds Left Shift on a keyboard and is killed.
//                    Prints Left Shift's key state, as GetAsyncKeyState and
//                    this program's keyboard hook see it, before the kill and
//                    for three seconds after it, with each repeat the hook
//                    sees and when Raw Input stops listing the keyboard.
//                    Fails unless the child's control key reached the hook
//                    and Left Shift read down before the kill, and then, on
//                    UMDF2, Left Shift goes up with no repeat after it, or on
//                    USB/IP, the keyboard leaves Raw Input with no repeat
//                    after that. Needs a session with input.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Bench
{
    /// <summary>The serial the controller's HID device serves: the UMDF2
    /// driver's HM-CTL- and the transport index, or on the USB/IP backend the
    /// identity's serial its device descriptor names.</summary>
    internal static string SerialOf(HMController c) =>
        c.Backend == HMBackend.Usbip ? c.UsbipHandle!.Device.Descriptors.SerialString!
                                     : DeviceOrchestrator.DriverSerial(c.TransportIndex);

    static HMProfile Custom(ushort pid, string name, string type, HidDescriptorBuilder d) =>
        new HMProfileBuilder().Id($"kbm-bench-{pid:x4}").Name(name).Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(pid).ProductString(name).ManufacturerString("HIDMaestro")
            .Type(type).Connection("usb").FromDescriptorBuilder(d).Build();

    // ── --order ─────────────────────────────────────────────────────────

    internal static int Order(HMContext ctx, int rounds)
    {
        Console.WriteLine($"=== Report order under load: 4 mice, 500 reports each, {rounds} rounds ===");
        var profile = Custom(0xF0C8, "HIDMaestro Order Mouse", "mouse",
            new HidDescriptorBuilder().Mouse(reportId: 1, wheel: false, pan: false).VendorButtons(8, 2));
        var pads = Enumerable.Range(1, 4).Select(i => ctx.CreateController(profile, $"kbm-order-{i}")).ToList();
        var readers = new List<CounterReader>();
        try
        {
            Console.WriteLine($"  backend {pads[0].Backend}, delivers every report: {string.Join(",", pads.Select(p => p.DeliversEveryReport))}");
            foreach (var p in pads)
            {
                string serial = SerialOf(p);
                HMHidDeviceInfo? v = null;
                var sw = Stopwatch.StartNew();
                while (v == null && sw.ElapsedMilliseconds < 30000)
                {
                    v = HMDeviceExtractor.ListDevices().FirstOrDefault(d => d.VendorId == 0xBEEF && d.ProductId == 0xF0C8
                        && d.TopLevelUsagePage == 0xFF00 && d.SerialNumberString == serial);
                    if (v == null) Thread.Sleep(100);
                }
                if (v == null) { Console.WriteLine($"  [FAIL] the vendor collection of serial {serial} did not appear"); return 1; }
                var r = CounterReader.Open(v.DevicePath);
                if (r == null) { Console.WriteLine($"  [FAIL] the vendor collection of serial {serial} did not open: {Marshal.GetLastWin32Error()}"); return 1; }
                readers.Add(r);
            }
            Thread.Sleep(1000);
            int lostRounds = 0, swappedRounds = 0;
            double longest = 0;
            double tickMs = 1000.0 / Stopwatch.Frequency;
            for (int round = 1; round <= rounds; round++)
            {
                Thread.Sleep(300);
                foreach (var r in readers) r.Clear();
                using var stop = new ManualResetEventSlim(false);
                var burners = Enumerable.Range(0, Environment.ProcessorCount)
                    .Select(_ => new Thread(() => { while (!stop.IsSet) Thread.SpinWait(1000); }) { IsBackground = true }).ToList();
                burners.ForEach(t => t.Start());
                var maxMs = new double[4];
                var threads = pads.Select((p, i) => new Thread(() =>
                {
                    for (int n = 0; n < 500; n++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        p.SubmitMouse(new HMMouseState { Buttons = (HMMouseButtons)((uint)(n & 0xFF) << 5) });
                        double ms = (Stopwatch.GetTimestamp() - t0) * tickMs;
                        if (ms > maxMs[i]) maxMs[i] = ms;
                    }
                })).ToList();
                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
                stop.Set();
                burners.ForEach(t => t.Join());
                // Until the counts hold still for 400 ms, at most 4 s.
                int last = -1;
                var still = Stopwatch.StartNew();
                var cap = Stopwatch.StartNew();
                while (cap.ElapsedMilliseconds < 4000)
                {
                    Thread.Sleep(50);
                    int now = readers.Sum(r => r.Count);
                    if (now != last) { last = now; still.Restart(); }
                    else if (still.ElapsedMilliseconds >= 400) break;
                }
                var line = new List<string>();
                bool bad = false;
                for (int i = 0; i < 4; i++)
                {
                    var vals = readers[i].Values();
                    int breaks = 0, backward = 0;
                    for (int k = 1; k < vals.Count; k++)
                    {
                        int d = (vals[k] - vals[k - 1]) & 0xFF;
                        if (d == 1) continue;
                        breaks++;
                        if (d == 0 || d >= 128) backward++;
                    }
                    // 499 arrive when none is lost: the first report's
                    // counter, 0, is the state already sent, so it is not
                    // sent again.
                    bool lost = vals.Count < 499;
                    if (lost) lostRounds++;
                    if (breaks > 0) swappedRounds++;
                    if (lost || breaks > 0) bad = true;
                    longest = Math.Max(longest, maxMs[i]);
                    line.Add($"m{i + 1} {vals.Count}/{breaks}b{backward}r max{maxMs[i]:F1}");
                }
                if (bad || round % 20 == 0) Console.WriteLine($"  round {round,3}: {string.Join(" | ", line)}");
                foreach (var p in pads) p.SubmitMouse(new HMMouseState());
            }
            int deviceRounds = rounds * 4;
            Console.WriteLine($"=== Report order, {pads[0].Backend}: {deviceRounds} device-rounds, {lostRounds} with a lost report, " +
                              $"{swappedRounds} with a counter out of order, longest submit {longest:F1} ms ===");
            return lostRounds == 0 && swappedRounds == 0 ? 0 : 1;
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
            foreach (var p in pads) p.Dispose();
        }
    }

    /// <summary>A reader of one vendor collection: report 2's counter byte,
    /// in arrival order, on a thread of its own at the highest priority,
    /// with 512 HID class buffers.</summary>
    sealed class CounterReader : IDisposable
    {
        readonly IntPtr _h;
        readonly ConcurrentQueue<int> _q = new();
        readonly Thread _t;

        CounterReader(IntPtr h)
        {
            _h = h;
            _t = new Thread(() =>
            {
                var buf = new byte[2];
                while (ReadFile(_h, buf, 2, out uint n, IntPtr.Zero))
                    if (n >= 2 && buf[0] == 2) _q.Enqueue(buf[1]);
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            _t.Start();
        }

        public static CounterReader? Open(string path)
        {
            IntPtr h = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            HidD_SetNumInputBuffers(h, 512);
            return new CounterReader(h);
        }

        public int Count => _q.Count;
        public List<int> Values() => _q.ToList();
        public void Clear() { while (_q.TryDequeue(out _)) { } }
        public void Dispose() { CancelIoEx(_h, IntPtr.Zero); CloseHandle(_h); }
    }

    // ── --dispose-ack ───────────────────────────────────────────────────

    internal static int DisposeAck(HMContext ctx, int cases)
    {
        Console.WriteLine($"=== Dispose with F24 held: the release frame delivered before the device goes, {cases} cases ===");
        var profile = Custom(0xF0C9, "HIDMaestro Dispose Ack Keyboard", "keyboard", new HidDescriptorBuilder().Keyboard());
        int passed = 0;
        for (int i = 1; i <= cases; i++)
        {
            var k = ctx.CreateController(profile, "kbm-dispose-ack");
            int index = k.TransportIndex;
            // Windows reads a keyboard once it opens it, and sends it its LED
            // state then. Up to 15 s for that, then a report goes out.
            var sw = Stopwatch.StartNew();
            while (k.KeyboardLeds == null && sw.ElapsedMilliseconds < 15000) Thread.Sleep(10);
            // The LED state Windows sends a keyboard it opens, decoded from
            // the output report the device publishes, on either backend.
            var leds = k.KeyboardLeds;
            k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 });
            k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F24 });
            bool delivers = k.DeliversEveryReport;
            IntPtr inputH = OpenFileMappingW(4, false, $@"Global\HIDMaestroInput{index}");
            IntPtr ackH = OpenFileMappingW(4, false, $@"Global\HIDMaestroInputAck{index}");
            IntPtr input = inputH == IntPtr.Zero ? IntPtr.Zero : MapViewOfFile(inputH, 4, 0, 0, UIntPtr.Zero);
            IntPtr ack = ackH == IntPtr.Zero ? IntPtr.Zero : MapViewOfFile(ackH, 4, 0, 0, UIntPtr.Zero);
            var dw = Stopwatch.StartNew();
            k.Dispose();
            long disposeMs = dw.ElapsedMilliseconds;
            string detail;
            bool ok;
            if (input == IntPtr.Zero || ack == IntPtr.Zero)
            {
                ok = false;
                detail = $"sections not opened ({Marshal.GetLastWin32Error()})";
            }
            else
            {
                uint written = (uint)Marshal.ReadInt32(input, 0);
                uint delivered = (uint)Marshal.ReadInt32(ack, SharedMemoryIO.INPUT_ACK_OFFSET_DELIVERED);
                int extSize = Marshal.ReadInt32(input, SharedMemoryIO.EXTENDED_SIZE_OFFSET);
                var frame = new byte[Math.Clamp(extSize, 0, SharedMemoryIO.EXTENDED_DATA_CAPACITY)];
                Marshal.Copy(input + SharedMemoryIO.EXTENDED_DATA_OFFSET, frame, 0, frame.Length);
                bool release = frame.Length > 0 && frame.All(b => b == 0);
                ok = leds != null && delivers && delivered == written && release;
                detail = $"LED state {(leds == null ? "never sent" : leds.ToString())}, delivers every report {delivers}, last frame written {written}, delivered {delivered}, " +
                         $"last frame [{Convert.ToHexString(frame)}], Dispose {disposeMs} ms";
            }
            if (input != IntPtr.Zero) UnmapViewOfFile(input);
            if (ack != IntPtr.Zero) UnmapViewOfFile(ack);
            if (inputH != IntPtr.Zero) CloseHandle(inputH);
            if (ackH != IntPtr.Zero) CloseHandle(ackH);
            if (ok) passed++;
            Console.WriteLine($"  case {i}: {(ok ? "PASS" : "FAIL")}, {detail}");
        }
        Console.WriteLine($"=== Dispose acknowledgement, {ctx.ResolveBackend(profile)}: {passed}/{cases} cases held the LED state Windows sent and delivered the release frame before the device went ===");
        return passed == cases ? 0 : 1;
    }

    // ── --held-on-kill ──────────────────────────────────────────────────

    const int VK_LSHIFT = 0xA0;

    /// <summary>The run's timeout scale, HIDMAESTRO_TIMEOUT_SCALE, at least 1.</summary>
    static readonly double Scale = Math.Max(1.0,
        double.TryParse(Environment.GetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) ? scale : 1.0);

    /// <summary>The child: Left Shift held on a keyboard until it is
    /// killed.</summary>
    internal static int HoldShiftChild(HMContext ctx)
    {
        var k = ctx.CreateController(Custom(0xF0CA, "HIDMaestro Held Shift Keyboard", "keyboard",
            new HidDescriptorBuilder().Keyboard()), "kbm-held-shift");
        var sw = Stopwatch.StartNew();
        while (k.KeyboardLeds == null && sw.ElapsedMilliseconds < 15000) Thread.Sleep(10);
        Thread.Sleep(300);
        // The control: F22 pressed and released, which the parent's hook
        // must see before Left Shift's state means anything.
        k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.F22 });
        k.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
        k.SubmitKeyboard(HMKeyModifiers.LeftShift, ReadOnlySpan<HMKey>.Empty);
        Console.WriteLine($"HELD backend {k.Backend} release-on-exit {(k.ReleasesOnConsumerExit ? "on" : "off")}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    internal static int HeldOnKill()
    {
        Console.WriteLine("=== Left Shift held by a consumer that is killed ===");
        using var sink = new RawInputSink(true, (0x01, 0x06));
        var before = RawInputSink.Devices().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--held-shift-child")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string? line;
        while ((line = child.StandardOutput.ReadLine()) != null && !line.StartsWith("HELD")) { }
        if (line == null) { Console.WriteLine("  [FAIL] the child exited before it held Left Shift"); return 1; }
        Console.WriteLine($"  child: {line}");
        bool usbip = line.Contains(" backend Usbip ");
        var kb = RawInputSink.Devices().FirstOrDefault(d => d.Type == RawInputSink.RIM_TYPEKEYBOARD && !before.Contains(d.Name)
                                                           && RawInputSink.Attributes(d.Name) is { Vid: 0xBEEF, Pid: 0xF0CA });
        Console.WriteLine($"  Raw Input lists the keyboard: {(kb.Name != null ? kb.Name : "no")}");
        var cw = Stopwatch.StartNew();
        bool control = false;
        while (!control && cw.ElapsedMilliseconds < 3000)
        {
            control = sink.HookKeys.Any(k => k.VKey == 0x85 && (k.Flags & 0x80) == 0);
            Thread.Sleep(10);
        }
        Console.WriteLine($"  control: the child's F22 reached this program's hook: {(control ? "yes" : "no, so keyboard input does not reach this desktop and nothing below means anything")}");
        // Windows repeats a held key after the keyboard delay. 1.5 s held.
        sink.Clear();
        Thread.Sleep(1500);
        int heldDowns = sink.HookKeys.Count(k => k.VKey == VK_LSHIFT && (k.Flags & 0x80) == 0);
        bool downBefore = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
        Console.WriteLine($"  before the kill: GetAsyncKeyState(VK_LSHIFT) {(downBefore ? "down" : "up")}, {heldDowns} Left Shift downs at the hook in 1.5 s");
        sink.Clear();
        var clock = Stopwatch.StartNew();
        child.Kill();
        child.WaitForExit();
        long killed = clock.ElapsedMilliseconds;
        long upAt = -1, goneAt = -1, upTicks = 0, goneTicks = 0;
        while (clock.ElapsedMilliseconds < 3000 + killed)
        {
            if (upAt < 0 && (GetAsyncKeyState(VK_LSHIFT) & 0x8000) == 0) { upAt = clock.ElapsedMilliseconds; upTicks = Stopwatch.GetTimestamp(); }
            if (goneAt < 0 && kb.Name != null && !RawInputSink.Devices().Any(d => string.Equals(d.Name, kb.Name, StringComparison.OrdinalIgnoreCase)))
            { goneAt = clock.ElapsedMilliseconds; goneTicks = Stopwatch.GetTimestamp(); }
            Thread.Sleep(10);
        }
        // A repeat the hook took before the key went up or the device went,
        // which its thread can stamp late on a loaded machine, is not
        // counted: only a down after the mark plus a drain of 250 ms at the
        // run's timeout scale.
        long mark = usbip ? goneTicks : upTicks;
        long after = mark == 0 ? long.MaxValue : mark + (long)(Stopwatch.Frequency * 0.25 * Scale);
        var hook = sink.HookKeys.Where(k => k.VKey == VK_LSHIFT).ToList();
        var raw = kb.Name == null ? new List<RawInputSink.Key>() : sink.Keys.Where(k => k.Device == kb.Handle).ToList();
        Console.WriteLine($"  killed at {killed} ms; GetAsyncKeyState(VK_LSHIFT) up at {(upAt < 0 ? "never in 3 s" : upAt + " ms")}; " +
                          $"Raw Input stopped listing the keyboard at {(goneAt < 0 ? "never in 3 s" : goneAt + " ms")}");
        Console.WriteLine($"  hook after the kill: {(hook.Count == 0 ? "nothing" : string.Join(" ", hook.Select(k => ((k.Flags & 0x80) != 0 ? "up" : "down") + ((k.Flags & 0x10) != 0 ? "(injected)" : ""))))}");
        Console.WriteLine($"  Raw Input from the keyboard after the kill: {(raw.Count == 0 ? "nothing" : string.Join(" ", raw.Select(k => $"vk 0x{k.VKey:X2} {((k.Flags & 1) != 0 ? "break" : "make")}")))}");
        int repeats = hook.Count(k => (k.Flags & 0x80) == 0 && k.Ticks > after);
        bool ok = control && downBefore && repeats == 0 && (usbip ? goneAt >= 0 : upAt >= 0);
        Console.WriteLine($"=== Held key on kill, {(usbip ? "Usbip" : "Umdf2")}: {(ok ? "PASS" : "FAIL")}; control {(control ? "seen" : "NOT seen")}; " +
                          $"Left Shift {(downBefore ? "down" : "up")} before the kill, {(upAt >= 0 ? "up" : "still down")} after it, " +
                          $"{(usbip ? (goneAt >= 0 ? "the keyboard gone, " : "the keyboard still listed, ") : "")}{repeats} repeats after {(usbip ? "it went" : "the up")} ===");
        return ok ? 0 : 1;
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, uint len, out uint read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(IntPtr h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll")] static extern bool HidD_SetNumInputBuffers(IntPtr h, uint n);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr v);
}
