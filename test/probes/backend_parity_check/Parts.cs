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

    static void Long()
    {
        Console.WriteLine();
        Console.WriteLine("=== Reports over 64 bytes on USB/IP ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        foreach (string id in new[] { "dualsense-bt", "switch-pro", "apple-magic-keyboard-a1644" })
        {
            var source = ctx.GetProfile(id);
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
        bool own = p.BuildsItsOwnReports || p.Inner.Protocol != null || wide.FrameBytes > SharedMemoryIO.EXTENDED_DATA_CAPACITY;
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
                var sw = Stopwatch.StartNew();
                while (!read.IsCompleted && sw.ElapsedMilliseconds < 3000)
                {
                    try
                    {
                        if (own) c.SubmitState(new HMGamepadState());
                        else c.SubmitRawExtendedReport(pattern);
                    }
                    catch (Exception e) { Note($"{name}: submit {e.GetType().Name}: {e.Message}"); break; }
                    read.Wait(20);
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
                var outs = p.Inner.GetOrBuildReportMap().Reports.Where(r => r.Kind == HidReportKind.Output).OrderByDescending(r => r.FrameBytes).ToList();
                if (outs.Count > 0 && widest.OutputLength > 0)
                {
                    var report = outs[0];
                    var w = new byte[widest.OutputLength];
                    w[0] = report.ReportId;
                    for (int i = 1; i < report.FrameBytes && i < w.Length; i++) w[i] = (byte)(i * 5 + 1);
                    var got = new ManualResetEventSlim();
                    int gotLength = -1;
                    byte gotFirst = 0;
                    c.OutputReceived += (_, pkt) =>
                    {
                        if (pkt.ReportId != report.ReportId || got.IsSet) return;
                        gotLength = pkt.Data.Length;
                        gotFirst = pkt.Data.Length > 0 ? pkt.Data.Span[0] : (byte)0;
                        got.Set();
                    };
                    try { fs.Write(w, 0, w.Length); fs.Flush(); }
                    catch (Exception e) { Note($"{name}: WriteFile {e.GetType().Name}: {e.Message}"); }
                    int expect = Math.Min(report.FrameBytes - (report.ReportId != 0 ? 1 : 0), SharedMemoryIO.DATA_CAPACITY);
                    if (got.Wait(3000))
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
        int used = HIDMaestro.Internal.Usbip.VhciClient.IsAvailable() ? HIDMaestro.Internal.Usbip.VhciClient.GetImportedDevices().Count : 0;
        Note($"{ports} USB 2.0 ports, {used} in use before the run");
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
            Exception? past = null;
            try { using var extra = ctx.CreateController(p, "ports:past", HMBackend.Usbip); }
            catch (Exception e) { past = e; }
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
        string? ready = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 60000 && (ready = child.StandardOutput.ReadLine()) != null && !ready.StartsWith("READY", StringComparison.Ordinal)) { }
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
        ctx.DisposeControllersInParallel(mine);
        Thread.Sleep(500);
        Check("this program's dispose leaves the child's devices started", AllUp(theirs.Select(x => x.Split('/')[2])));
        Check("and its own are gone", ours.All(x => !Hid.Status(x.Split('/')[2]).Present));
        child.StandardInput.WriteLine("quit");
        child.WaitForExit(30000);
        Thread.Sleep(500);
        Check("the child's dispose takes its devices", theirs.All(x => !Hid.Status(x.Split('/')[2]).Present));
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
}
