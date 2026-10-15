// S85: an exception a program's event handler throws stops nothing else.
//
// Each event the output thread raises gets a handler that throws, attached
// first, and a counting handler after it. The counting handler must still
// get every event, and the SDK must still apply every packet: a keyboard's
// LED state, OutputDecoded on the profile codec's path and on Switch Pro
// rumble, and a Bluetooth DualSense's switch to report 0x31 on the feature
// read that arms it. Before v1.13.0 a throwing handler stopped the handlers
// after it, and a throwing OutputReceived handler skipped the rest of the
// packet.
//
// A handler still running when Dispose's join of the output thread gives up
// must not leave that thread touching what Dispose released: it ends on its
// own once the handler returns. Before this release Dispose disposed the
// cancellation source the thread waits on, and the thread's next wait threw.
//
// An OutputReceived handler that disposes its own controller: Dispose does not
// wait for the output reader it runs on. Before this release it joined its
// own thread for the join's full 500 ms. The same holds when the handler
// disposes its controller together with two others through
// DisposeControllersInParallel, the path HMContext.Dispose takes for two or
// more controllers, and when it calls HMContext.Dispose. Before this
// release Parallel.ForEach chose the thread for each controller, and a pool
// thread waited 500 ms for the reader.
//
// "offline" runs the set dispose with controllers that have no device,
// needing no elevation: the handler's own controller stays on its thread,
// and every failure of the dispose callback comes out in one
// AggregateException.
//
// The other events raised on SDK threads are tested where their stimulus
// lives: the adapter ports' OutputChanged in S80, the USB audio events in
// usbip_server_check, HapticReceived in S50, and the Switch 2 Pro's decode
// in its composite check.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that
// embeds HIDMaestro is running.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const int Reports = 20;

    static readonly Action<HMController, HMOutputPacket> ThrowOnOutput =
        (_, _) => throw new InvalidOperationException("S85: a throwing OutputReceived handler");
    static readonly EventHandler<HMOutputDecodedEventArgs> ThrowOnDecoded =
        (_, _) => throw new InvalidOperationException("S85: a throwing OutputDecoded handler");

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "offline") return Offline();
        Console.WriteLine("=== S85: a throwing event handler stops nothing else ===");
        if (Probe.OtherSdkRunning()) return 2;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        OutputAndDecoded(ctx);
        KeyboardLeds(ctx);
        ArmOnFeatureRead(ctx);
        SwitchProRumble(ctx);
        SlowHandlerAtDispose(ctx);
        DisposeFromOutputHandler(ctx);
        DisposeSetFromOutputHandler(ctx);
        // Last: it disposes the context.
        DisposeContextFromOutputHandler(ctx);
        return Probe.Finish("S85");
    }

    // ── OutputReceived and the codec's OutputDecoded ────────────────────

    static void OutputAndDecoded(HMContext ctx)
    {
        Console.WriteLine("-- dualshock-4-v2: OutputReceived and OutputDecoded");
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = ctx.CreateController(profile);
        int received = 0, decoded = 0;
        c.OutputReceived += ThrowOnOutput;
        c.OutputReceived += (_, p) => { if (p.ReportId == 0x05) Interlocked.Increment(ref received); };
        c.OutputDecoded += ThrowOnDecoded;
        c.OutputDecoded += (_, e) => { if (e.ReportId == 0x05) Interlocked.Increment(ref decoded); };

        using var h = OpenNew(profile, before);
        if (h == null) return;
        // Report 0x05, the USB DualShock 4's 32-byte output report.
        var report = new byte[32];
        report[0] = 0x05;
        int sent = 0;
        for (int i = 0; i < Reports; i++)
        {
            report[4] = (byte)(i + 1);
            if (HidD_SetOutputReport(h, report, report.Length)) sent++;
            Thread.Sleep(2);
        }
        Probe.WaitFor(() => Volatile.Read(ref received) >= sent && Volatile.Read(ref decoded) >= sent, 3000);
        Probe.Check($"the handler after a throwing OutputReceived handler gets all {Reports} output reports",
            sent == Reports && Volatile.Read(ref received) == sent, $"{received} of {sent} sent");
        Probe.Check($"OutputDecoded still fires for all {Reports}, and the handler after a throwing one gets them",
            sent == Reports && Volatile.Read(ref decoded) == sent, $"{decoded} of {sent} sent");
    }

    // ── A handler still running at Dispose ──────────────────────────────

    static void SlowHandlerAtDispose(HMContext ctx)
    {
        Console.WriteLine("-- dualshock-4-v2: an OutputReceived handler still running at Dispose");
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var c = ctx.CreateController(profile);
        var output = (Thread)typeof(HMController).GetField("_outputThread", Any)!.GetValue(c)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        c.OutputReceived += (_, p) =>
        {
            if (p.ReportId != 0x05 || entered.IsSet) return;
            entered.Set();
            release.Wait(5000);
        };
        // Every exception the output thread meets from here on, caught or
        // not.
        var met = new ConcurrentQueue<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> watch = (_, e) =>
        {
            if (Thread.CurrentThread == output) met.Enqueue($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        };
        AppDomain.CurrentDomain.FirstChanceException += watch;
        try
        {
            using (var h = OpenNew(profile, before))
            {
                if (h == null) { c.Dispose(); return; }
                var report = new byte[32];
                report[0] = 0x05;
                HidD_SetOutputReport(h, report, report.Length);
            }
            bool inHandler = entered.Wait(3000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            c.Dispose();
            long disposeMs = sw.ElapsedMilliseconds;
            bool stillInHandler = !release.IsSet && output.IsAlive;
            release.Set();
            bool ended = Probe.WaitFor(() => !output.IsAlive, 3000);
            Probe.Check("Dispose returns while an OutputReceived handler still runs", inHandler && stillInHandler,
                $"handler entered {inHandler}, Dispose took {disposeMs} ms");
            Probe.Check("the output thread then ends without meeting anything Dispose released",
                ended && met.IsEmpty, ended ? (met.IsEmpty ? "no exception" : string.Join("; ", met)) : "the thread is still running");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= watch;
            release.Set();
        }
    }

    // ── A handler that disposes its own controller ─────────────────────

    static void DisposeFromOutputHandler(HMContext ctx)
    {
        Console.WriteLine("-- dualshock-4-v2: an OutputReceived handler that disposes its own controller");
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var c = ctx.CreateController(profile);
        var output = (Thread)typeof(HMController).GetField("_outputThread", Any)!.GetValue(c)!;
        long started = 0;
        using var entered = new ManualResetEventSlim();
        c.OutputReceived += (_, p) =>
        {
            if (p.ReportId != 0x05 || entered.IsSet) return;
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            entered.Set();
            c.Dispose();
        };
        using (var h = OpenNew(profile, before))
        {
            if (h == null) { c.Dispose(); return; }
            var report = new byte[32];
            report[0] = 0x05;
            HidD_SetOutputReport(h, report, report.Length);
        }
        bool ran = entered.Wait(3000);
        // The controller leaves the context where Dispose hands it to the
        // context's teardown, after the join Dispose skips here.
        bool handedOver = Probe.WaitFor(() => !ctx.ActiveControllers.Contains(c), 3000);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        bool ended = Probe.WaitFor(() => !output.IsAlive, 10_000);
        Probe.Check("an OutputReceived handler disposing its own controller does not wait for its own thread",
            ran && handedOver && ms < 250 && ended,
            ran ? $"teardown reached after {ms:F0} ms, reader ended {ended}" : "the handler never ran");
    }

    // ── A handler that disposes the whole context ──────────────────────

    /// <summary>The handler calls HMContext.Dispose with its controller and
    /// one other in the context. The other has no handler. The handler's
    /// own leaves the SDK's output pump registry after its Dispose has
    /// skipped or finished the join of its reader, which is the thread the
    /// handler holds.</summary>
    static void DisposeContextFromOutputHandler(HMContext ctx)
    {
        Console.WriteLine("-- dualshock-4-v2 x2: an OutputReceived handler that disposes the context");
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var other = ctx.CreateController(profile);
        bool otherUp = Probe.WaitFor(() => HidPaths(profile.VendorId, profile.ProductId).Any(p => !before.Contains(p)), 10_000);
        // Without it the handler's own device cannot be told apart, and the
        // check below would return without a result.
        Probe.Check("the other DualShock 4's HID device appears", otherUp);
        foreach (var p in HidPaths(profile.VendorId, profile.ProductId)) before.Add(p);
        var c = ctx.CreateController(profile);
        int index = (int)typeof(HMController).GetProperty("Index", Any)!.GetValue(c)!;
        var output = (Thread)typeof(HMController).GetField("_outputThread", Any)!.GetValue(c)!;
        long started = 0;
        using var entered = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        c.OutputReceived += (_, p) =>
        {
            if (p.ReportId != 0x05 || entered.IsSet) return;
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            entered.Set();
            ctx.Dispose();
            returned.Set();
        };
        bool registered = OutputPumpRegistered(index);
        using (var h = otherUp ? OpenNew(profile, before) : null)
        {
            if (h == null) { ctx.Dispose(); return; }
            var report = new byte[32];
            report[0] = 0x05;
            HidD_SetOutputReport(h, report, report.Length);
        }
        bool ran = entered.Wait(3000);
        bool released = Probe.WaitFor(() => !OutputPumpRegistered(index), 3000);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        bool done = returned.Wait(30_000);
        bool ended = Probe.WaitFor(() => !output.IsAlive, 10_000);
        if (!ran) ctx.Dispose();
        Probe.Check("an OutputReceived handler disposing the context does not wait for its own thread",
            registered && ran && released && ms < 250 && done && ended,
            ran ? $"own reader released after {ms:F0} ms, Dispose returned {done}, reader ended {ended}"
                : $"the handler never ran (registered {registered})");
    }

    /// <summary>Whether the SDK's output pump registry holds the controller
    /// at <paramref name="index"/>. HMController.Dispose removes it right
    /// after the join of the output reader, or after skipping that join on
    /// the reader's own thread.</summary>
    static bool OutputPumpRegistered(int index)
    {
        var t = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.SharedMemoryIO")!;
        var pumps = (System.Collections.IDictionary)t.GetField("s_outputPumps", Any)!.GetValue(null)!;
        lock (pumps) return pumps.Contains(index);
    }

    // ── Offline: the set dispose's threads and exceptions ──────────────

    /// <summary>HMContext's set dispose on its own, with controllers that
    /// have no device: one claims the calling thread as its output reader,
    /// as a handler's controller does, and the dispose callback throws for
    /// two of the three. Needs no elevation and no driver.</summary>
    static int Offline()
    {
        Console.WriteLine("=== S85, offline: a set disposed from a handler's thread ===");
        SetErrorMode(QuietErrorMode);
        var helper = typeof(HMContext).GetMethod("DisposeConcurrently", Any)!;
        var outputField = typeof(HMController).GetField("_outputThread", Any)!;
        var set = new HMController[3];
        for (int i = 0; i < set.Length; i++)
            set[i] = (HMController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HMController));
        // The handler's own controller, last in the set.
        outputField.SetValue(set[2], Thread.CurrentThread);
        var threads = new ConcurrentDictionary<int, int>();
        Action<HMController> dispose = c =>
        {
            int i = Array.IndexOf(set, c);
            threads[i] = Environment.CurrentManagedThreadId;
            Thread.Sleep(50);
            if (i != 1) throw new InvalidOperationException($"S85 offline: controller {i}");
        };
        Exception? caught = null;
        try { helper.Invoke(null, new object[] { set, dispose }); }
        catch (TargetInvocationException e) { caught = e.InnerException; }
        int me = Environment.CurrentManagedThreadId;
        Probe.Check("the handler's own controller is disposed on the handler's thread, the others too",
            threads.Count == 3 && threads.TryGetValue(2, out int t2) && t2 == me,
            string.Join(", ", threads.OrderBy(p => p.Key).Select(p => $"{p.Key}: {(p.Value == me ? "caller" : "pool")}")));
        var agg = caught as AggregateException;
        // One level, as Parallel.ForEach throws: no AggregateException
        // inside it.
        var inner = agg?.InnerExceptions.ToArray() ?? Array.Empty<Exception>();
        var messages = inner.Select(x => x.Message).OrderBy(m => m).ToArray();
        bool flat = inner.All(x => x is InvalidOperationException);
        Probe.Check("every failure comes out in one flat AggregateException, as from Parallel.ForEach",
            agg != null && flat && messages.SequenceEqual(new[] { "S85 offline: controller 0", "S85 offline: controller 2" }),
            $"{caught?.GetType().Name ?? "nothing"}: [{string.Join(", ", messages)}]");
        return Probe.Finish("S85 offline");
    }

    const uint QuietErrorMode = 0x0001 | 0x0002 | 0x8000; // SEM_FAILCRITICALERRORS, SEM_NOGPFAULTERRORBOX, SEM_NOOPENFILEERRORBOX
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);

    // ── A handler that disposes its controller with others ─────────────

    /// <summary>The handler disposes three controllers at once, its own
    /// last in the set. Without the fix, Parallel.ForEach chooses the
    /// thread for each, and a pool thread that takes the handler's own
    /// waits for the reader the handler holds. On the old code the check
    /// fails only when that happens.</summary>
    static void DisposeSetFromOutputHandler(HMContext ctx)
    {
        Console.WriteLine("-- dualshock-4-v2 x3: an OutputReceived handler that disposes its own controller and two others at once");
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var first = new[] { ctx.CreateController(profile), ctx.CreateController(profile) };
        // The two others' HID devices go into the set to skip, so the one
        // opened below is the third's.
        bool othersUp = Probe.WaitFor(() => HidPaths(profile.VendorId, profile.ProductId).Count(p => !before.Contains(p)) >= 2, 10_000);
        // Without them the handler's own device cannot be told apart, and
        // the check below would return without a result.
        Probe.Check("the two other DualShock 4s' HID devices appear", othersUp);
        foreach (var p in HidPaths(profile.VendorId, profile.ProductId)) before.Add(p);
        var c = ctx.CreateController(profile);
        var set = new[] { first[0], first[1], c };
        var output = (Thread)typeof(HMController).GetField("_outputThread", Any)!.GetValue(c)!;
        long started = 0;
        using var entered = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        c.OutputReceived += (_, p) =>
        {
            if (p.ReportId != 0x05 || entered.IsSet) return;
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            entered.Set();
            ctx.DisposeControllersInParallel(set);
            returned.Set();
        };
        using (var h = othersUp ? OpenNew(profile, before) : null)
        {
            if (h == null) { ctx.DisposeControllersInParallel(set); return; }
            var report = new byte[32];
            report[0] = 0x05;
            HidD_SetOutputReport(h, report, report.Length);
        }
        bool ran = entered.Wait(3000);
        // The controller leaves the context where its Dispose hands it to
        // the context's teardown, after the join Dispose skips on its own
        // thread.
        bool handedOver = Probe.WaitFor(() => !ctx.ActiveControllers.Contains(c), 3000);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        bool done = returned.Wait(30_000);
        bool ended = Probe.WaitFor(() => !output.IsAlive, 10_000);
        bool allGone = set.All(x => !ctx.ActiveControllers.Contains(x));
        if (!ran) ctx.DisposeControllersInParallel(set);
        Probe.Check("an OutputReceived handler disposing its own controller with two others does not wait for its own thread",
            ran && handedOver && ms < 250 && done && ended && allGone,
            ran ? $"teardown reached after {ms:F0} ms, the call returned {done}, reader ended {ended}, all three gone {allGone}"
                : "the handler never ran");
    }

    // ── KeyboardLedsChanged ─────────────────────────────────────────────

    static void KeyboardLeds(HMContext ctx)
    {
        Console.WriteLine("-- microsoft-wired-keyboard-600: the LED state");
        var profile = ctx.GetProfile("microsoft-wired-keyboard-600")!;
        using var k = ctx.CreateController(profile);
        // Windows sends a keyboard its LED state when it arrives, before the
        // handlers below are attached.
        bool arrived = Probe.WaitFor(() => k.KeyboardLeds != null, 15_000);
        Probe.Check("Windows sends the new keyboard its LED state", arrived);
        if (!arrived) return;

        var seen = new ConcurrentQueue<HMKeyboardLeds>();
        k.OutputReceived += ThrowOnOutput;
        k.KeyboardLedsChanged += (_, _) => throw new InvalidOperationException("S85: a throwing KeyboardLedsChanged handler");
        k.KeyboardLedsChanged += (_, l) => seen.Enqueue(l);

        bool initial = ScrollLockOn();
        PressScrollLock(k);
        bool toggled = Probe.WaitFor(() => seen.Any(l => l.HasFlag(HMKeyboardLeds.ScrollLock) != initial), 3000);
        bool stateToggled = k.KeyboardLeds?.HasFlag(HMKeyboardLeds.ScrollLock) == !initial;
        PressScrollLock(k);
        bool restored = Probe.WaitFor(() => seen.Count >= 2 && seen.Last().HasFlag(HMKeyboardLeds.ScrollLock) == initial, 3000);
        // Leave Scroll Lock as it was found, whatever the handlers saw.
        if (!Probe.WaitFor(() => ScrollLockOn() == initial, 1000)) PressScrollLock(k);
        Probe.WaitFor(() => ScrollLockOn() == initial, 1000);

        Probe.Check("behind a throwing OutputReceived handler and a throwing KeyboardLedsChanged handler, " +
                    "the handler after them sees Scroll Lock go on and off, and KeyboardLeds follows",
            toggled && stateToggled && restored && k.KeyboardLeds?.HasFlag(HMKeyboardLeds.ScrollLock) == initial,
            $"seen [{string.Join(", ", seen)}], KeyboardLeds {k.KeyboardLeds?.ToString() ?? "null"}");
        Probe.Check("Scroll Lock is left as it was found", ScrollLockOn() == initial);
    }

    static void PressScrollLock(HMController k)
    {
        k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.ScrollLock });
        k.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
    }

    // ── The packet applied after a throwing OutputReceived handler ──────

    static void ArmOnFeatureRead(HMContext ctx)
    {
        Console.WriteLine("-- dualsense-bt: the feature read that switches it to report 0x31");
        var profile = ctx.GetProfile("dualsense-bt")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = ctx.CreateController(profile);
        var armedField = typeof(HMController).GetField("_extendedModeArmed", Any)!;
        bool Armed() => (bool)armedField.GetValue(c)!;
        int reads = 0;
        c.OutputReceived += ThrowOnOutput;
        c.OutputReceived += (_, p) => { if (p.Source == HMOutputSource.HidFeatureRead && p.ReportId == 0x05) Interlocked.Increment(ref reads); };

        using var h = OpenNew(profile, before);
        if (h == null) return;
        Probe.Check("the DualSense starts on report 0x01", !Armed());
        int length = Caps(h).FeatureReportByteLength;
        var buffer = new byte[Math.Max(length, 2)];
        buffer[0] = 0x05;
        bool read = HidD_GetFeature(h, buffer, buffer.Length);
        bool armed = Probe.WaitFor(Armed, 2000);
        Probe.Check("behind a throwing OutputReceived handler, a host's read of feature report 0x05 " +
                    "still switches it to report 0x31, and the handler after it sees the read",
            armed && Volatile.Read(ref reads) == 1,
            $"HidD_GetFeature {(read ? "succeeded" : "failed")}, {reads} read seen, armed {armed}");
    }

    // ── Switch Pro rumble's OutputDecoded ───────────────────────────────

    static void SwitchProRumble(HMContext ctx)
    {
        Console.WriteLine("-- switch-pro: OutputDecoded on rumble report 0x10");
        var profile = ctx.GetProfile("switch-pro")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = ctx.CreateController(profile);
        int decoded = 0;
        c.OutputReceived += ThrowOnOutput;
        c.OutputDecoded += ThrowOnDecoded;
        c.OutputDecoded += (_, e) => { if (e.ReportId == 0x10) Interlocked.Increment(ref decoded); };

        using var h = OpenNew(profile, before);
        if (h == null) return;
        int length = Caps(h).OutputReportByteLength;
        var report = new byte[length];
        report[0] = 0x10;
        // One HD rumble block per side after the packet counter: a 160 Hz
        // tone at a low amplitude, the bytes SDL sends for a light rumble.
        byte[] block = { 0x00, 0x01, 0x40, 0x40 };
        block.CopyTo(report, 2);
        block.CopyTo(report, 6);
        int sent = 0;
        for (int i = 0; i < Reports && length >= 10; i++)
        {
            report[1] = (byte)(i & 0x0F);
            if (HidD_SetOutputReport(h, report, report.Length)) sent++;
            Thread.Sleep(2);
        }
        Probe.WaitFor(() => Volatile.Read(ref decoded) >= sent, 3000);
        Probe.Check($"Switch Pro rumble: behind throwing handlers, OutputDecoded's next handler gets all {Reports}",
            sent == Reports && Volatile.Read(ref decoded) == sent, $"{decoded} of {sent} sent, report length {length}");
    }

    // ── HID helpers ─────────────────────────────────────────────────────

    /// <summary>The new HID device's handle, opened for reading and writing,
    /// or null after a failed check.</summary>
    static SafeFileHandle? OpenNew(HMProfile profile, HashSet<string> before)
    {
        string? path = null;
        Probe.WaitFor(() => (path = HidPaths(profile.VendorId, profile.ProductId).FirstOrDefault(p => !before.Contains(p))) != null, 10_000);
        Probe.Check($"{profile.Id}'s HID device appears", path != null);
        if (path == null) return null;
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Probe.Check($"{profile.Id}'s HID device opens", !h.IsInvalid, h.IsInvalid ? $"error {Marshal.GetLastWin32Error()}" : "");
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    static HIDP_CAPS Caps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out IntPtr pp)) return default;
        try { HidP_GetCaps(pp, out var caps); return caps; }
        finally { HidD_FreePreparsedData(pp); }
    }

    /// <summary>The HID device paths of one VID and PID, through the SDK's
    /// own enumerator.</summary>
    static IEnumerable<string> HidPaths(ushort vid, ushort pid)
    {
        var t = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.HidDeviceEnumerator")!;
        var list = (System.Collections.IEnumerable)t.GetMethod("Enumerate", Any)!.Invoke(null, null)!;
        foreach (var d in list)
        {
            var dt = d.GetType();
            if (Convert.ToUInt16(dt.GetField("VendorId")!.GetValue(d)) != vid) continue;
            if (Convert.ToUInt16(dt.GetField("ProductId")!.GetValue(d)) != pid) continue;
            if (dt.GetField("DevicePath")!.GetValue(d) is string path) yield return path;
        }
    }

    const int VK_SCROLL = 0x91;
    static bool ScrollLockOn() => (GetKeyState(VK_SCROLL) & 1) != 0;

    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    // hidsdi.h: the HidD_ functions return BOOLEAN, one byte.
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
}
