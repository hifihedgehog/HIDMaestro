// The parts of backend_parity_check after parity (Program.cs header).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;
using Windows.Gaming.Input;

internal static partial class Program
{
    // ── long ─────────────────────────────────────────────────────────────

    /// <summary>The catalog profiles whose form on USB/IP has an input or
    /// output report over 64 bytes, with each one's widest of each.</summary>
    static void LongList()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        foreach (var p in ctx.AllProfiles.Where(x => x.IsDeployable && x.SupportsBackend(HMBackend.Usbip)).OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var form = p.FormOn(HMBackend.Usbip);
            var map = form.Inner.GetOrBuildReportMap();
            int input = map.Reports.Where(r => r.Kind == HidReportKind.Input).Select(r => r.FrameBytes).DefaultIfEmpty(0).Max();
            int output = map.Reports.Where(r => r.Kind == HidReportKind.Output).Select(r => r.FrameBytes).DefaultIfEmpty(0).Max();
            if (input > 64 || output > 64)
                Console.WriteLine($"  {p.Id,-40} form {form.Id,-34} input {input,4} output {output,4} own {form.BuildsItsOwnReports || form.Inner.Protocol != null} usbConfiguration {form.Inner.UsbConfiguration != null}");
            if (input > 64 || output > 64)
                Console.WriteLine("      " + string.Join(" ", map.Reports.Select(r => $"{r.Kind}:0x{r.ReportId:X2}:{r.FrameBytes}")));
        }
    }

    static void Long()
    {
        Console.WriteLine();
        Console.WriteLine("=== Reports over 64 bytes on USB/IP ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        // Each has an input report over 64 bytes on USB/IP. long-bt-shape is
        // a profile of the probe's own with dualsense-bt's descriptor, whose
        // 78-byte report 0x31 goes as raw frames, the widest the extended
        // lane takes. joycon-l's 362-byte report the device builds from
        // SubmitState. The keyboard's 65-byte report goes as raw frames.
        var btShape = ctx.GetProfile("dualsense-bt");
        var subjects = new List<(string Id, HMProfile? Profile)>
        {
            ("long-bt-shape", btShape == null ? null
                : new HMProfileBuilder().Id("long-bt-shape").Vid(0xBEEF).Pid(0xF0D8).DescriptorHex(btShape.DescriptorHex!).Build()),
            ("joycon-l", ctx.GetProfile("joycon-l")),
            ("apple-magic-keyboard-a1644", ctx.GetProfile("apple-magic-keyboard-a1644")),
        };
        foreach (var (id, source) in subjects)
        {
            if (source == null) { Check($"{id} is in the catalog", false); continue; }
            foreach (string? speed in new string?[] { null, "full" })
            {
                HMProfile p = source;
                if (speed != null)
                {
                    var inner = source.Inner.Clone();
                    inner.UsbConfiguration = new UsbConfigurationSpec { BusSpeed = speed };
                    p = new HMProfile(inner);
                }
                string? got = LongOne(ctx, p, speed ?? "derived", HMBackend.Usbip);
                if (speed == null && HMContext.MachineBackend == HMBackend.Umdf2)
                {
                    string? umdf2 = LongOne(ctx, p, "driver", HMBackend.Umdf2);
                    if (got != null && umdf2 != null)
                        Check($"{p.Id}: the report read on USB/IP is the one read on UMDF2, byte for byte", got == umdf2,
                              $"USB/IP {got[..Math.Min(40, got.Length)]}..., UMDF2 {umdf2[..Math.Min(40, umdf2.Length)]}...");
                }
            }
        }
    }

    /// <summary>One profile on one backend: the widest input report read
    /// in one ReadFile, and the longest output report written in one
    /// WriteFile. Returns the report read, hex, or null.</summary>
    static string? LongOne(HMContext ctx, HMProfile p, string speed, HMBackend backend)
    {
        string name = $"{p.Id} on {backend} at the {speed} speed";
        using var c = ctx.CreateController(p, "long:" + p.Id + ":" + speed, backend);
        string parent = ParentOf(c)!;
        if (backend == HMBackend.Usbip)
        {
            var set = c.UsbipHandle!.Device.Descriptors;
            s_usbIds.Add(parent);
            Note($"{name}: bus speed {set.Speed}, IN 0x81 wMaxPacketSize {set.Endpoints[0x81].MaxPacketSize}");
        }
        var collections = Hid.Read(parent, out var errors);
        for (int i = 0; collections.Count == 0 && i < 100; i++) { Thread.Sleep(50); collections = Hid.Read(parent, out errors); }
        var widest = collections.OrderByDescending(x => x.InputLength).FirstOrDefault();
        if (widest == null) { Check($"{name}: a HID collection enumerates", false, string.Join("; ", errors)); return null; }
        string? result = null;

        // Input: the widest input report the descriptor declares, whole on
        // the extended lane with a marker in its last byte, read back in one
        // ReadFile. A device whose protocol builds its reports sends its own.
        var map = p.Inner.GetOrBuildReportMap();
        var wide = map.Reports.Where(r => r.Kind == HidReportKind.Input).OrderByDescending(r => r.FrameBytes).First();
        bool own = p.BuildsItsOwnReports || p.Inner.Protocol != null || wide.FrameBytes > SharedMemoryIO.WIDE_FRAME_CAP;
        int carried = wide.FrameBytes;
        byte dataId = wide.ReportId;
        using (var h = Hid.Open(widest.Path, 0xC0000000))
        {
            if (h.IsInvalid) Note($"{name}: the widest collection ({widest.UsagePage:X4}:{widest.Usage:X4}) does not open for reading here");
            else
            {
                using var fs = new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true);
                var buf = new byte[widest.InputLength];
                var read = fs.ReadAsync(buf, 0, buf.Length);
                byte[] pattern = new byte[carried];
                pattern[0] = dataId;
                for (int i = 1; i < carried; i++) pattern[i] = (byte)(i * 7 + 3);
                pattern[carried - 1] = 0xA5;
                // The device may send other reports first, an idle repeat
                // among them: read until the report submitted arrives. A
                // device that builds its own reports may send one only when
                // its input changes, as a Joy-Con sends 0x3F, and the one its
                // first clock tick sends can go out before this handle is
                // open, which HID class then gives no reader. So each submit
                // here differs from the one before it, and a report follows
                // the open whatever the device's rule.
                var sw = Stopwatch.StartNew();
                bool held = false;
                while (sw.ElapsedMilliseconds < 3000)
                {
                    try
                    {
                        if (own)
                        {
                            held = !held;
                            c.SubmitState(held ? new HMGamepadState { Buttons = HMButton.A | HMButton.LeftBumper, Hat = HMHat.West } : new HMGamepadState());
                        }
                        else c.SubmitRawExtendedReport(pattern);
                    }
                    catch (Exception e) { Note($"{name}: submit {e.GetType().Name}: {e.Message}"); break; }
                    read.Wait(20);
                    if (!read.IsCompleted) continue;
                    if (own || buf[0] == dataId) break;
                    read = fs.ReadAsync(buf, 0, buf.Length);
                }
                if (!read.IsCompleted) { Check($"{name}: a read returns", false); }
                else
                {
                    int n = read.Result;
                    Check($"{name}: one ReadFile returns one whole report of {widest.InputLength} bytes", n == widest.InputLength, $"{n} bytes, ID 0x{buf[0]:X2}");
                    if (!own)
                        Check($"{name}: report 0x{dataId:X2} of {carried} bytes arrived whole, its last byte included",
                              buf.AsSpan(0, carried).SequenceEqual(pattern), $"byte {carried - 1} 0x{buf[carried - 1]:X2}");
                    if (!own) result = Convert.ToHexString(buf, 0, n);
                }

                // Output: the longest output report, written whole.
                // The collection's own: HID class takes a write of the
                // collection's output length, and its longest output report
                // is that length, report ID included.
                var outs = p.Inner.GetOrBuildReportMap().Reports.Where(r => r.Kind == HidReportKind.Output && r.FrameBytes + (r.ReportId == 0 ? 1 : 0) == widest.OutputLength)
                            .OrderByDescending(r => r.FrameBytes).ToList();
                if (outs.Count > 0 && widest.OutputLength > 0)
                {
                    var report = outs[0];
                    var w = new byte[widest.OutputLength];
                    w[0] = report.ReportId;
                    for (int i = 1; i < report.FrameBytes && i < w.Length; i++) w[i] = (byte)(i * 5 + 1);
                    var got = new ManualResetEventSlim();
                    int gotLength = -1;
                    byte gotFirst = 0;
                    // Another program on the machine can write to the same
                    // device. On the laptop on 2026-11-19 one sent this
                    // Joy-Con shape a report 0x01 of its own before this
                    // write, with the same ID and length. The packet counted
                    // is the one that carries this write's bytes, four of the
                    // pattern from its start, and any other is noted.
                    byte[] mark = w.AsSpan(1, Math.Min(4, w.Length - 1)).ToArray();
                    int others = 0;
                    string? otherFirst = null;
                    c.OutputReceived += (_, pkt) =>
                    {
                        if (pkt.ReportId != report.ReportId || got.IsSet) return;
                        var d = pkt.Data.Span;
                        if (d.Length < mark.Length || !d[..mark.Length].SequenceEqual(mark))
                        {
                            if (Interlocked.Increment(ref others) == 1) otherFirst = Convert.ToHexString(d[..Math.Min(12, d.Length)]);
                            return;
                        }
                        gotLength = d.Length;
                        gotFirst = d[0];
                        got.Set();
                    };
                    try { fs.Write(w, 0, w.Length); fs.Flush(); }
                    catch (Exception e) { Note($"{name}: WriteFile {e.GetType().Name}: {e.Message}"); }
                    int expect = Math.Min(report.FrameBytes - (report.ReportId != 0 ? 1 : 0), SharedMemoryIO.DATA_CAPACITY);
                    bool arrived = got.Wait(3000);
                    if (others > 0)
                        Note($"{name}: another program wrote output report 0x{report.ReportId:X2} to the device first, {others} time(s), starting {otherFirst}");
                    if (arrived)
                        Check($"{name}: output report 0x{report.ReportId:X2} of {report.FrameBytes} bytes arrives whole, as far as the output section holds it",
                              gotLength == expect && gotFirst == w[1], $"{gotLength} of {expect} bytes");
                    else if (p.Inner.Protocol != null)
                        Note($"{name}: the protocol consumed output report 0x{report.ReportId:X2}, so OutputReceived does not raise it");
                    else Check($"{name}: output report 0x{report.ReportId:X2} arrives", false, "no OutputReceived within 3 s");
                }
            }
        }
        return result;
    }

    // ── ports ────────────────────────────────────────────────────────────

    static void Ports()
    {
        Console.WriteLine();
        Console.WriteLine("=== Past the host controller's ports ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile("dualshock-4-v2")!;
        int ports = HIDMaestro.Internal.Usbip.UsbipBackend.Usb20Ports();
        int used = HIDMaestro.Internal.Usbip.VhciClient.IsAvailable()
            ? HIDMaestro.Internal.Usbip.UsbipBackend.Usb20PortsUsed(HIDMaestro.Internal.Usbip.VhciClient.GetImportedSpeeds()) : 0;
        Note($"{ports} USB 2.0 ports (root hub {HIDMaestro.Internal.Usbip.VhciClient.Usb20PortsFromRootHub()?.ToString() ?? "no answer"}, " +
             $"parameters {HIDMaestro.Internal.Usbip.UsbipBackend.Usb20PortsFromParameters()}), {used} in use before the run");
        var made = new List<HMController>();
        try
        {
            for (int i = used; i < ports; i++)
            {
                var c = ctx.CreateController(p, $"ports:{i}", HMBackend.Usbip);
                made.Add(c);
                s_usbIds.Add(ParentOf(c)!);
            }
            Check($"{ports - used} creates fill the free ports", made.Count == ports - used, $"{made.Count}");
            // dualshock-4-v2 derives a full-speed device, USB_SPEED_FULL (2).
            var speeds = HIDMaestro.Internal.Usbip.VhciClient.GetImportedSpeeds();
            Check("the host controller lists each of them at full speed, read at the row's speed offset",
                  speeds.Count(v => v == 2) >= made.Count, string.Join(" ", speeds));
            Exception? past = null;
            int leases = 0;
            var lease = HMContext.AcquireLease;
            HMContext.AcquireLease = (slot, key, profileId, backend) => { leases++; return lease(slot, key, profileId, backend); };
            try { using var extra = ctx.CreateController(p, "ports:past", HMBackend.Usbip); }
            catch (Exception e) { past = e; }
            finally { HMContext.AcquireLease = lease; }
            Check("the refusal comes before the create's lease, so nothing machine-wide is taken", leases == 0, $"{leases} lease(s)");
            Check("the create past the last port is refused by name",
                  past is HMResourceInUseException r && r.Message.Contains("USB 2.0 ports", StringComparison.Ordinal),
                  past == null ? "it returned" : $"{past.GetType().Name}: {past.Message}");
            int up = made.Count(c => Hid.Interfaces(Hid.Children(ParentOf(c)!).FirstOrDefault() ?? "-").Count > 0);
            Check("every controller created before it keeps its HID interface", up == made.Count, $"{up} of {made.Count}");
        }
        finally { ctx.DisposeControllersInParallel(made); }
    }

    // ── firstlife and start ──────────────────────────────────────────────

    static (double P50, double P95) Percentiles(List<long> values)
    {
        var s = values.OrderBy(v => v).ToList();
        double At(double q) => s.Count == 0 ? double.NaN : s[Math.Min(s.Count - 1, (int)Math.Ceiling(q * s.Count) - 1)];
        return (At(0.50), At(0.95));
    }

    static void FirstLife(int n)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {n} first lives against {n} later lives of one identity ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile("dualshock-4-v2")!;
        var first = new List<long>();
        var later = new List<long>();
        string run = DateTime.UtcNow.ToString("MMddHHmmss");
        for (int i = 0; i < n; i++)
        {
            var s = Take(ctx, p, HMBackend.Usbip, $"firstlife:{run}:{i}");
            if (s.ReadyMs >= 0) first.Add(s.ReadyMs);
        }
        _ = Take(ctx, p, HMBackend.Usbip, "firstlife:same");
        for (int i = 0; i < n; i++)
        {
            var s = Take(ctx, p, HMBackend.Usbip, "firstlife:same");
            if (s.ReadyMs >= 0) later.Add(s.ReadyMs);
        }
        var (f50, f95) = Percentiles(first);
        var (l50, l95) = Percentiles(later);
        Note($"call to an open HID interface, first lives: p50 {f50} ms, p95 {f95} ms ({first.Count} of {n})");
        Note($"call to an open HID interface, later lives: p50 {l50} ms, p95 {l95} ms ({later.Count} of {n})");
        Check("every first and later life became ready", first.Count == n && later.Count == n, $"{first.Count}, {later.Count}");
    }

    static void Start(int n)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {n} creates, each opened at once ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        int atOnce = 0;
        var times = new List<long>();
        foreach (string id in new[] { "dualshock-4-v2", "dualsense" })
        {
            var p = ctx.GetProfile(id)!;
            for (int i = 0; i < n / 2; i++)
            {
                var s = Take(ctx, p, HMBackend.Usbip, $"start:{id}");
                if (s.OpenAtReturn) atOnce++;
                times.Add(s.CreateMs);
            }
        }
        var (p50, p95) = Percentiles(times);
        Note($"create p50 {p50} ms, p95 {p95} ms");
        Check("the HID interface opened the moment each create returned, with no retry", atOnce == times.Count, $"{atOnce} of {times.Count}");
    }

    // ── wgi ──────────────────────────────────────────────────────────────

    static void Wgi()
    {
        Console.WriteLine();
        Console.WriteLine("=== Windows.Gaming.Input and GameInput on USB/IP ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var d = new HidDescriptorBuilder().Gamepad();
        d.AddStick("Left", 16); d.AddStick("Right", 16);
        d.AddTrigger("Left", 16); d.AddTrigger("Right", 16);
        d.AddHat(); d.AddButtons(11);
        var built = new HMProfileBuilder()
            .Id("parity-custom-gamepad").Name("Backend parity custom").Vendor("Custom")
            .Vid(0xBEEF).Pid(0xF0A6).ProductString("ParityCustomGamepad").ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb").FromDescriptorBuilder(d).Build();
        foreach (var p in new[] { ctx.GetProfile("dualshock-4-v2")!, ctx.GetProfile("dualsense")!, built })
        {
            using var c = ctx.CreateController(p, "wgi:" + p.Id, HMBackend.Usbip);
            s_usbIds.Add(ParentOf(c)!);
            RawGameController? raw = null;
            var sw = Stopwatch.StartNew();
            while (raw == null && sw.ElapsedMilliseconds < 10000)
            {
                raw = RawGameController.RawGameControllers.FirstOrDefault(r => r.HardwareVendorId == p.VendorId && r.HardwareProductId == p.ProductId);
                if (raw == null) Thread.Sleep(100);
            }
            Check($"{p.Id}: WGI lists a RawGameController with its VID and PID", raw != null,
                  raw == null ? "none within 10 s" : $"'{raw.DisplayName}', {raw.AxisCount} axes, {raw.ButtonCount} buttons, after {sw.ElapsedMilliseconds} ms");
            string? lister = Environment.GetEnvironmentVariable("HM_GI_LIST");
            if (lister != null && File.Exists(lister))
            {
                var psi = new ProcessStartInfo(lister) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var gi = Process.Start(psi)!;
                string text = gi.StandardOutput.ReadToEnd();
                gi.WaitForExit(10000);
                bool listed = text.Contains($"VID=0x{p.VendorId:X4} PID=0x{p.ProductId:X4}", StringComparison.OrdinalIgnoreCase);
                if (p == built) Note($"{p.Id}: GameInput lists it as a gamepad or controller: {listed}");
                else Check($"{p.Id}: GameInput lists it as a gamepad or controller", listed, text.Replace("\r", "").Replace("\n", " | ").Trim());
            }
        }
        using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GameInputSvc");
        Check("GameInputSvc runs", GameInputRunning(), $"start type {svc?.GetValue("Start")}");
    }

    static bool GameInputRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query GameInputSvc") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var sc = Process.Start(psi)!;
            string text = sc.StandardOutput.ReadToEnd();
            sc.WaitForExit(10000);
            return text.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // ── oem ──────────────────────────────────────────────────────────────

    static void Oem()
    {
        Console.WriteLine();
        Console.WriteLine("=== The joy.cpl name on USB/IP ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile("dualshock-4-v2")!;
        string key = $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{p.VendorId:X4}&PID_{p.ProductId:X4}";
        string? Read() { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue("OEMName") as string ?? (k == null ? "(no key)" : "(no value)"); }
        string before = Read()!;
        string name = p.Inner.DeviceDescription ?? p.ProductString;
        using (var c = ctx.CreateController(p, "oem:" + p.Id, HMBackend.Usbip))
        {
            s_usbIds.Add(ParentOf(c)!);
            Check("while the controller lives the identity's OEMName is the profile's name", Read() == name, $"{Read()} (before {before})");
        }
        Check("once it goes the value is what it was before", Read() == before, $"{Read()} against {before}");
    }

    // ── identity ─────────────────────────────────────────────────────────

    static string IdentityFile => Path.Combine(Path.GetTempPath(), "HIDMaestro_backend_parity_identity.txt");

    static void Identity(string phase)
    {
        Console.WriteLine();
        Console.WriteLine($"=== One identity key across lives, {phase} process ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var p = ctx.GetProfile("dualshock-4-v2")!;
        string Paths()
        {
            var s = Take(ctx, p, HMBackend.Usbip, "identity:r20");
            if (s.Parent != null) s_usbIds.Add(s.Parent);
            return s.Parent + " " + string.Join(" ", s.Collections.Select(c => c.Path));
        }
        string a = Paths(), b = Paths();
        Check("the same key gives the same USB instance and HID interface paths after a dispose", a == b && a.Contains("hid#", StringComparison.OrdinalIgnoreCase), a);
        if (phase == "first") File.WriteAllText(IdentityFile, a);
        else
        {
            string earlier = File.Exists(IdentityFile) ? File.ReadAllText(IdentityFile) : "(no earlier process)";
            Check("and after the process restarts", a == earlier, earlier);
        }
    }

    // ── twoprograms ──────────────────────────────────────────────────────

    static void TwoPrograms()
    {
        Console.WriteLine();
        Console.WriteLine("=== Two programs on one host controller ===");
        var psi = new ProcessStartInfo(Environment.ProcessPath!, "child dualshock-4-v2 dualsense")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        using var child = Process.Start(psi)!;
        // The child's lines are read on a task of their own, so a child that
        // hangs without a word fails this within the bound.
        string? ready = null;
        var reading = System.Threading.Tasks.Task.Run(() =>
        {
            string? line;
            while ((line = child.StandardOutput.ReadLine()) != null && !line.StartsWith("READY", StringComparison.Ordinal)) { }
            return line;
        });
        if (reading.Wait(HIDMaestro.Internal.TimeoutScale.Apply(60000))) ready = reading.Result;
        Check("the child program created its controllers", ready?.StartsWith("READY", StringComparison.Ordinal) == true, ready ?? "no answer");
        if (ready == null || !ready.StartsWith("READY", StringComparison.Ordinal)) { try { child.Kill(); } catch { } return; }
        var theirs = ready.Split(' ').Skip(1).ToList();   // port/busid/usbid each
        string States(IEnumerable<string> ids) => string.Join(", ", ids.Select(u => $"{u.Split('/')[2].Split('\\').Last()} {Hid.Status(u.Split('/')[2])}"));
        string Imports() => string.Join(", ", HIDMaestro.Internal.Usbip.VhciClient.GetImportedDevices().Select(r => $"{r.Port}:{r.Service}/{r.BusId}"));
        Note("the child's devices before this program creates: " + States(theirs) + "; imports " + Imports());
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var mine = new List<HMController>();
        string Listeners() => string.Join(" ", System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Port is >= 18509 and < 18530).Select(e => e.ToString()));
        HIDMaestro.Internal.Usbip.UsbipBackend.DuringStaleSweep = stage => Note($"stale sweep at '{stage}': imports {Imports()}; the child's {States(theirs)}; listeners {Listeners()}");
        mine.Add(ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "two:parent:0", HMBackend.Usbip));
        HIDMaestro.Internal.Usbip.UsbipBackend.DuringStaleSweep = null;
        Note("the child's devices after this program's first create: " + States(theirs) + "; imports " + Imports());
        mine.Add(ctx.CreateController(ctx.GetProfile("dualsense")!, "two:parent:1", HMBackend.Usbip));
        Note("the child's devices after its second: " + States(theirs));
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 10000 && !theirs.All(x => Hid.Status(x.Split('/')[2]) is { Present: true, Started: true })) Thread.Sleep(100);
        Note($"the child's devices {settle.ElapsedMilliseconds} ms later: " + States(theirs));
        var ours = mine.Select(c => $"{c.UsbipHandle!.ServerPort}/{c.UsbipHandle.Device.BusId}/{ParentOf(c)}").ToList();
        foreach (var c in mine) s_usbIds.Add(ParentOf(c)!);
        Note("this program: " + string.Join(" ", ours));
        Note("the child: " + string.Join(" ", theirs));
        var locations = ours.Concat(theirs).Select(x => string.Join("/", x.Split('/').Take(2))).ToList();
        Check("no two controllers share a server port and bus id", locations.Distinct().Count() == locations.Count);
        Check("the two programs run servers on different ports",
              ours.Select(x => x.Split('/')[0]).Distinct().Single() != theirs.Select(x => x.Split('/')[0]).Distinct().Single());
        bool AllUp(IEnumerable<string> usbIds) => usbIds.All(u => Hid.Status(u) is { Present: true, Started: true });
        Check("all four devices are started", AllUp(ours.Concat(theirs).Select(x => x.Split('/')[2])));
        // Each "gone" waits for its devnodes, bounded by the scaled limit,
        // and the check reads the state the wait ended on.
        bool AllGone(IEnumerable<string> located)
        {
            var bound = Stopwatch.StartNew();
            bool gone;
            while (!(gone = located.All(x => !Hid.Status(x.Split('/')[2]).Present)) && bound.ElapsedMilliseconds < HIDMaestro.Internal.TimeoutScale.Apply(10000))
                Thread.Sleep(50);
            return gone;
        }
        ctx.DisposeControllersInParallel(mine);
        bool oursGone = AllGone(ours);
        Check("this program's dispose leaves the child's devices started", AllUp(theirs.Select(x => x.Split('/')[2])));
        Check("and its own are gone", oursGone);
        child.StandardInput.WriteLine("quit");
        child.WaitForExit(30000);
        Check("the child's dispose takes its devices", AllGone(theirs));
        foreach (var t in theirs) s_usbIds.Add(t.Split('/')[2]);
    }

    static int Child(List<string> ids)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var made = new List<HMController>();
        for (int i = 0; i < ids.Count; i++)
            made.Add(ctx.CreateController(ctx.GetProfile(ids[i])!, $"two:child:{i}", HMBackend.Usbip));
        Console.WriteLine("READY " + string.Join(" ", made.Select(c => $"{c.UsbipHandle!.ServerPort}/{c.UsbipHandle.Device.BusId}/{ParentOf(c)}")));
        Console.Out.Flush();
        Console.In.ReadLine();
        ctx.DisposeControllersInParallel(made);
        return 0;
    }

    // ── Liveness of a server port ────────────────────────────────────────

    /// <summary>A sweep's answer to "does a server listen on this loopback
    /// port", the way a stale sweep asks it: a listener another process holds
    /// reads live, and each of 16 closed ports reads gone, asked one after
    /// another through a fresh view, all within 3 s. A connect to a closed
    /// port that waits out Windows's SYN retries takes about 2 s alone, and
    /// a sweep asks every port of the 16-port range.</summary>
    static void Liveness()
    {
        int live = FreePort();
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"listener {live}")
            { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        try
        {
            string? up = null;
            var reading = System.Threading.Tasks.Task.Run(() => child.StandardOutput.ReadLine());
            if (reading.Wait(30000)) up = reading.Result;
            Check("the child holds a loopback listener", up == "LISTENING", up ?? "no answer in 30 s");
            if (up != "LISTENING") return;
            var closed = new List<int>();
            while (closed.Count < 16) { int p = FreePort(); if (p != live && !closed.Contains(p)) closed.Add(p); }
            var view = new HIDMaestro.Internal.Usbip.UsbipBackend.SweepView();
            var clock = Stopwatch.StartNew();
            bool liveRead = view.IsListening(live);
            var wrong = closed.Where(p => view.IsListening(p)).ToList();
            long ms = clock.ElapsedMilliseconds;
            Check("the other process's listener reads live", liveRead);
            Check("each of 16 closed ports reads gone", wrong.Count == 0, string.Join(" ", wrong));
            Check("the 17 answers take under 3 s all told", ms < 3000, $"{ms} ms");
        }
        finally
        {
            try { child.StandardInput.WriteLine(); child.WaitForExit(10000); } catch { }
            try { if (!child.HasExited) child.Kill(); } catch { }
        }
    }

    /// <summary>A loopback port nothing listens on now: bound, read and
    /// closed.</summary>
    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static int Listener(int port)
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        l.Start();
        new Thread(() => { while (true) { try { l.AcceptSocket().Dispose(); } catch { return; } } }) { IsBackground = true }.Start();
        Console.WriteLine("LISTENING");
        Console.Out.Flush();
        Console.In.ReadLine();
        l.Stop();
        return 0;
    }

    // ── A program killed inside its attach ───────────────────────────────

    /// <summary>The next program's create after a program was killed inside
    /// the host controller's attach request. The child listens on a port of
    /// the SDK's range, answers nothing, and attaches a device there, so the
    /// host controller's request waits on the import handshake on a thread
    /// of the child. Killed then, the child cannot finish exiting while that
    /// thread is in the request, and the kernel keeps its connection to the
    /// child's port: the state S17 met on the ARM64 test PC on 2026-11-19,
    /// where every later attach on the machine waited. The create that
    /// follows must complete, and the connection must go.</summary>
    static void KilledAttach()
    {
        Console.WriteLine();
        Console.WriteLine("=== A create after a program killed inside its attach ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var pad = ctx.GetProfile("dualshock-4-v2")!;
        // One whole life first, so the host controller is installed and the
        // child's request reaches its handshake.
        using (ctx.CreateController(pad, "killed:first", HMBackend.Usbip)) { }
        int port = 0;
        for (int p = HIDMaestro.Internal.Usbip.UsbipServer.SecondBasePort + HIDMaestro.Internal.Usbip.UsbipServer.PortRange - 1;
             p >= HIDMaestro.Internal.Usbip.UsbipServer.SecondBasePort && port == 0; p--)
        {
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                probe.Start();
                probe.Stop();
                port = p;
            }
            catch (System.Net.Sockets.SocketException) { }
        }
        Check("a port of the SDK's range is free for the child", port != 0);
        if (port == 0) return;
        static int Held(int port) => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Count(c => c.State == System.Net.NetworkInformation.TcpState.Established && c.RemoteEndPoint.Port == port
                        && System.Net.IPAddress.IsLoopback(c.RemoteEndPoint.Address));
        static bool Until(Func<bool> done, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (done()) return true; Thread.Sleep(20); }
            return done();
        }
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"attachhang {port}")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
        try
        {
            string? line = null;
            var reading = System.Threading.Tasks.Task.Run(() => child.StandardOutput.ReadLine());
            if (reading.Wait(30000)) line = reading.Result;
            Check("the child starts its attach", line == "ATTACHING", line ?? "no answer in 30 s");
            if (line != "ATTACHING") return;
            bool waiting = Until(() => Held(port) > 0, 10000);
            Check("the host controller connects to the child's port and waits on the handshake", waiting && !child.HasExited,
                  $"connections {Held(port)}, child {(child.HasExited ? "exited" : "running")}");
            if (!waiting || child.HasExited) return;
            child.Kill();
            Thread.Sleep(1000);
            // A program that has finished exiting holds no connection: the
            // one still there a second after the kill is the held state.
            int heldAfterKill = Held(port);
            Check("the positive control: a second after the kill the host controller still holds its connection to the child's port",
                  heldAfterKill > 0, $"connections {heldAfterKill}");
            HMController? made = null;
            Exception? thrown = null;
            var clock = Stopwatch.StartNew();
            var create = System.Threading.Tasks.Task.Run(() =>
            {
                try { made = ctx.CreateController(pad, "killed:next", HMBackend.Usbip); }
                catch (Exception e) { thrown = e; }
            });
            bool done = create.Wait(15000);
            long ms = clock.ElapsedMilliseconds;
            Check("the next create completes", done && made != null,
                  !done ? "still waiting after 15 s" : thrown != null ? $"{thrown.GetType().Name}: {thrown.Message}" : $"{ms} ms");
            Check("the connection to the killed child's port is gone",
                  Until(() => Held(port) == 0, 5000), $"connections {Held(port)}");
            if (!done)
            {
                // Leave no machine held behind a failed check.
                HIDMaestro.Internal.Usbip.TcpConnectionOwner.EndConnectionsToExitedServers(p => p == port);
                create.Wait(15000);
            }
            made?.Dispose();
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch { }
        }
    }

    /// <summary>The child of <see cref="KilledAttach"/>: a listener that
    /// accepts and answers nothing, and an attach at its port, which the
    /// host controller's request waits on until the parent kills this
    /// process.</summary>
    static int AttachHang(int port)
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        l.Start();
        var held = new List<System.Net.Sockets.Socket>();
        new Thread(() => { while (true) { try { held.Add(l.AcceptSocket()); } catch { return; } } }) { IsBackground = true }.Start();
        Console.WriteLine("ATTACHING");
        Console.Out.Flush();
        try { HIDMaestro.Internal.Usbip.VhciClient.Attach("127.0.0.1", port, "1-1"); }
        catch (Exception e)
        {
            Console.WriteLine($"RETURNED {e.GetType().Name}: {e.Message}");
            Console.Out.Flush();
            return 3;
        }
        Console.WriteLine("RETURNED attached");
        Console.Out.Flush();
        return 4;
    }

    // ── The first create of a process ────────────────────────────────────

    /// <summary>n fresh processes, each creating one dualshock-4-v2 on the
    /// USB/IP backend and disposing it: the time of each process's first
    /// CreateController call, p50 and p95. Every first create runs the
    /// process's stale sweep.</summary>
    static void FirstCreate(int n)
    {
        var times = new List<long>();
        for (int i = 0; i < n; i++)
        {
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "firstone")
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            string output = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            string? line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("FIRST ", StringComparison.Ordinal));
            if (child.ExitCode != 0 || line == null) { Check($"run {i + 1}: the child created and disposed its controller", false, output.Trim()); return; }
            times.Add(long.Parse(line[6..]));
        }
        times.Sort();
        Note($"first create, {n} fresh processes: p50 {times[n / 2]} ms, p95 {times[(n * 95 + 99) / 100 - 1]} ms, min {times[0]} ms, max {times[^1]} ms, all {string.Join(" ", times)}");
        Check($"{n} fresh processes each created their first controller", times.Count == n);
    }

    static int FirstOne()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var clock = Stopwatch.StartNew();
        var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "first:one", HMBackend.Usbip);
        long ms = clock.ElapsedMilliseconds;
        c.Dispose();
        Console.WriteLine($"FIRST {ms}");
        return 0;
    }

    // ── A late acknowledgement after a re-create ─────────────────────────

    /// <summary>A USB/IP device's reader thread past its stop check can
    /// store a delivered sequence number after its controller's teardown.
    /// When this process has made the same index again by then, the number
    /// must not land in the new controller's acknowledgement section. The
    /// teardown and the re-create are played on SharedMemoryIO's own tables,
    /// with plain memory for the sections, so no Global object is made.</summary>
    static void AckGeneration()
    {
        const int index = 4093;
        var sm = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.SharedMemoryIO", throwOnError: true)!;
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var views = (Dictionary<int, IntPtr>)sm.GetField("s_inputAckViews", all)!.GetValue(null)!;
        var generations = sm.GetField("s_inputAckGenerations", all)?.GetValue(null) as Dictionary<int, long>;
        var lastGeneration = sm.GetField("s_lastAckGeneration", all);
        var lockObject = sm.GetField("s_inputViews", all)!.GetValue(null)!;
        int size = (int)sm.GetField("INPUT_ACK_SIZE", all)!.GetValue(null)!;
        var announce = sm.GetMethod("AnnounceDeviceDelivery", all)!;
        var store = sm.GetMethod("StoreDeviceDelivered", all)!;
        Note(generations == null ? "this SDK keeps no acknowledgement generation" : "this SDK keeps an acknowledgement generation per index");

        IntPtr Map()
        {
            IntPtr view = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            System.Runtime.InteropServices.Marshal.Copy(new byte[size], 0, view, size);
            lock (lockObject)
            {
                views[index] = view;
                if (generations != null)
                {
                    long next = (long)lastGeneration!.GetValue(null)! + 1;
                    lastGeneration.SetValue(null, next);
                    generations[index] = next;
                }
            }
            return view;
        }
        void Unmap()
        {
            lock (lockObject)
            {
                generations?.Remove(index);
                if (views.Remove(index, out IntPtr v)) System.Runtime.InteropServices.Marshal.FreeHGlobal(v);
            }
        }
        // The device's own call, in whichever form this SDK has.
        void Store(object? generation, uint seq) =>
            store.Invoke(null, store.GetParameters().Length == 4
                ? new object[] { index, generation!, seq, false }
                : new object[] { index, seq, false });

        try
        {
            IntPtr first = Map();
            object? oldGeneration = announce.Invoke(null, new object[] { index });
            Store(oldGeneration, 41);
            Check("the device's store lands in its own section (positive control)",
                  System.Runtime.InteropServices.Marshal.ReadInt32(first, 0) == 41);
            Unmap();
            IntPtr second = Map();
            object? newGeneration = announce.Invoke(null, new object[] { index });
            Store(oldGeneration, 42);
            Check("a store from the torn-down device leaves the re-created index's section alone",
                  System.Runtime.InteropServices.Marshal.ReadInt32(second, 0) == 0,
                  $"it reads {System.Runtime.InteropServices.Marshal.ReadInt32(second, 0)}");
            Store(newGeneration, 43);
            Check("the new device's store lands there",
                  System.Runtime.InteropServices.Marshal.ReadInt32(second, 0) == 43);
        }
        finally { Unmap(); }
    }

    // ── The port rule ────────────────────────────────────────────────────

    /// <summary>The host controller's USB 2.0 port count from its parameters,
    /// as usbip-win2 0.9.8.1 sets it (vhci.cpp set_usb_ports_cnt: each held
    /// to 1 to 254, USB 3 first, 255 in all), the imports that count against
    /// it, and where an import row carries its speed. Offline.</summary>
    static void PortRule()
    {
        int Rule(int usb2, int usb3) => HIDMaestro.Internal.Usbip.UsbipBackend.Usb20PortsRule(usb2, usb3);
        Check("30 and 30, the defaults: 30", Rule(30, 30) == 30);
        Check("254 and 254: USB 3 takes 254 first, which leaves 1", Rule(254, 254) == 1, Rule(254, 254).ToString());
        Check("200 and 100: 155 left beside USB 3's 100", Rule(200, 100) == 155, Rule(200, 100).ToString());
        Check("0 and 0 are held to 1 each", Rule(0, 0) == 1);
        Check("only imports below SuperSpeed take a USB 2.0 port",
              HIDMaestro.Internal.Usbip.UsbipBackend.Usb20PortsUsed(new[] { 1, 2, 3, 5, 6 }) == 3);
        var layouts = HIDMaestro.Internal.Usbip.VhciClient.Layouts;
        string offsets = string.Join(", ", layouts.Select(l => $"{l.Name} {HIDMaestro.Internal.Usbip.VhciClient.SpeedOffset(l)}"));
        Check("an import row's speed sits after its devid: 1104 in 0.9.8.1, 1100 in 0.9.8.0 and 0.9.7.x",
              offsets == "0.9.8.1 1104, 0.9.8.0 1100, 0.9.7.x 1100", offsets);
    }

    // ── A device Windows names by its port ───────────────────────────────

    /// <summary>One create of dualshock-4-v2 on USB/IP with the usbflags
    /// value IgnoreHWSerNum054C09CC set, which makes Windows name the device
    /// by its hub port rather than its serial. The start wait finds the
    /// device on its port and returns within its own 10 s bound, scaled, and
    /// the HID interface is up at return. The value is deleted after.</summary>
    static void IgnoreSerial()
    {
        Console.WriteLine();
        Console.WriteLine("=== A device Windows names by its port ===");
        const string flags = @"SYSTEM\CurrentControlSet\Control\usbflags";
        const string name = "IgnoreHWSerNum054C09CC";
        using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(flags))
            k.SetValue(name, new byte[] { 1 }, Microsoft.Win32.RegistryValueKind.Binary);
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var clock = Stopwatch.StartNew();
            using var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "ignoreserial:0", HMBackend.Usbip);
            long ms = clock.ElapsedMilliseconds;
            string serialId = c.UsbipHandle!.Device.Descriptors.UsbInstanceId!;
            string? onPort = HIDMaestro.Internal.Usbip.VhciClient.DeviceOnRootHubPort(c.UsbipHandle.Device.VhciPort);
            if (onPort != null) s_usbIds.Add(onPort);
            Note($"serial name {serialId}, the device on port {c.UsbipHandle.Device.VhciPort}: {onPort ?? "none"}, create {ms} ms");
            Check("Windows named the device by its port, not its serial (positive control for the flag)",
                  onPort != null && !string.Equals(onPort, serialId, StringComparison.OrdinalIgnoreCase), onPort ?? "none");
            int bound = HIDMaestro.Internal.TimeoutScale.Apply(10000);
            Check($"the create returns inside the start wait's {bound} ms bound", ms < bound, $"{ms} ms");
            int interfaces = onPort == null ? 0 : Hid.Interfaces(Hid.Children(onPort).FirstOrDefault() ?? "-").Count;
            Check("the HID interface is up when the create returns", interfaces > 0, $"{interfaces} interface(s)");
        }
        finally
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(flags, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
