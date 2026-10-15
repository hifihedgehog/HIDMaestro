// V. USB/IP re-attach attempts a killed program leaves behind. usbip-win2's
//    host controller starts them when an import's connection drops without
//    a plugout, keys them by host, port and bus id, and keeps them for about
//    two hours (persistent.cpp start_attach_attempts, vhci.cpp
//    init_constants). The probe counts them with STOP_ATTACH_ATTEMPTS,
//    whose reply carries the number it canceled, and a probe-made attempt
//    against a port outside the SDK's range is the positive control.
//      1. A program on this SDK is killed with a composite on transport
//         index 8 (bus id 1-9). A running program that had already swept
//         then creates its first composite and its server takes the same
//         port. The killed program's attempt is gone.
//      2. A program on this SDK is killed with composites on indices 0
//         and 8, and a v1.12.0 program takes its port with bus ids 1-1 and
//         1-10. Its connections are reset, and another program's sweep
//         cancels the killed program's attempt for 1-9 and leaves the
//         v1.12.0 program's re-attach of 1-1, a bus id the killed
//         program's record names too: both its composites come back.
//      3. That v1.12.0 program, with composites on slots 0 and 9, is
//         killed, and a program on this SDK takes its port. Its create
//         leaves no attempt on bus id 1-1. The attempt on 1-10, which no
//         record names, reaches the new server once and never again: the
//         probe listens on the port after that program and hears its own
//         attempt come back, and none for 1-10.
//      4. A running program's composite loses its connection: the probe
//         deletes the server end's TCP entry. Another program's composite
//         takes the vhci port it had, and the host controller's own attempt
//         imports the first one again on a new port. The first program's
//         record names the new port, and its dispose removes its own
//         composite and leaves the other program's.
//      5. A running program's composite, on bus id 1-1, loses its
//         connection: its record's vhci port goes to 0, and the program's
//         own sweep runs before the host controller's re-attach and the
//         composite comes back. It loses it again and the program disposes
//         it, and then again and the program exits without disposing it:
//         each time no attempt for it is left.
//      6. Another program's listener that never answers a device-list
//         request, answers it slowly, or claims more devices than a server
//         exports, holds a query no longer than its one-second deadline and
//         gets no list.
//         A program on this SDK is killed with composites on indices 9 to
//         12, and a listener that never answers takes its port. A sweep
//         asks that listener once, not once per composite, leaves the
//         attempts, and keeps the killed program's record, the one thing
//         naming them.
//         Once the listener is gone the next sweep cancels them and
//         removes the record.
//      7. Two programs each run a GameCube adapter. The first one's
//         controller is given the other's vhci port, the stale value a
//         drop that races the port tracker leaves, and binds WinUSB. The
//         binding lands on its own adapter, and the other's keeps its
//         driver.
//    "--v-parts=<digits>" after the section letters runs only those parts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    const int UsbipBasePort = 18509;

    static void SectionV()
    {
        var touched = new HashSet<(int Port, string BusId)>();
        try
        {
            if (!Vhci.Available()) { Check("V: the usbip-win2 host controller answers", false); return; }

            // Positive control: an attach that fails starts attempts
            // (vhci_ioctl.cpp complete), and the count reports them.
            const int controlPort = UsbipBasePort - 1;
            touched.Add((controlPort, "1-1"));
            bool attached = Vhci.Attach("127.0.0.1", controlPort, "1-1");
            int control = 0;
            WaitFor(() => (control += Math.Max(0, Vhci.Stop("127.0.0.1", controlPort, "1-1"))) > 0, 5000);
            Check("V: an attempt the probe starts is counted", !attached && control == 1, $"attach {attached}, counted {control}");

            if (VPart('1')) { VTakenOverPort(touched); Clean(); }
            if (VPart('2') || VPart('3')) { VLegacyTakesPort(touched); Clean(); }
            if (VPart('4')) { VDroppedImport(touched); Clean(); }
            if (VPart('5')) { VOwnReattach(touched); Clean(); }
            if (VPart('6')) { VListenerThatHolds(touched); Clean(); }
            if (VPart('7')) { VBindByLocation(touched); Clean(); }
        }
        finally
        {
            foreach (var (port, busId) in touched) Vhci.Stop("127.0.0.1", port, busId);
        }
    }

    /// <summary>Whether "--v-parts=" names this part, or is absent.</summary>
    static bool VPart(char part)
    {
        string? arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--v-parts=", StringComparison.Ordinal));
        return arg == null || arg.AsSpan("--v-parts=".Length).Contains(part);
    }

    // ── V1 ─────────────────────────────────────────────────────────────

    static void VTakenOverPort(HashSet<(int Port, string BusId)> touched)
    {
        using var b = PeerProcess.Start("V1-running");
        string pad = b.Send("create pad F1C0 -");
        if (!Ok(pad)) { Check("V1: the running program creates a gamepad", false, pad); return; }

        int port;
        string busId;
        using (var a = PeerProcess.Start("V1-killed"))
        {
            string ca = a.Send("createat 8 composite -");
            if (!Ok(ca)) { Check("V1: the program to be killed creates a composite at index 8", false, ca); return; }
            busId = "1-" + (int.Parse(PeerProcess.Field(ca, "transport")!) + 1);
            port = RecordUsbipPort(a.Pid);
            touched.Add((port, busId));
            Check($"V1: its import {port}/{busId} is attached", Vhci.Imported().Any(r => r.Port == port && r.BusId == busId));
            a.Kill();
        }
        Check($"V1: the host controller drops {busId} once the program is killed",
              WaitFor(() => !Vhci.Imported().Any(r => r.Port == port && r.BusId == busId), 10_000));

        string cb = b.Send("create composite -");
        if (!Ok(cb)) { Check("V1: the running program creates its first composite", false, cb); return; }
        int bPort = RecordUsbipPort(b.Pid);
        touched.Add((bPort, "1-" + (int.Parse(PeerProcess.Field(cb, "transport")!) + 1)));
        Check($"V1: its server takes the killed program's port {port}", bPort == port, $"port {bPort}");
        int left = Vhci.Stop("127.0.0.1", port, busId);
        Check($"V1: no re-attach attempt for {busId} at {port} outlives the takeover", left == 0, $"{left} canceled by the probe");
    }

    // ── V2 and V3 ──────────────────────────────────────────────────────

    static void VLegacyTakesPort(HashSet<(int Port, string BusId)> touched)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("V2: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }

        int port;
        string busId;
        using (var a = PeerProcess.Start("V2-killed"))
        {
            string c0 = a.Send("createat 0 composite -");
            string ca = a.Send("createat 8 composite -");
            if (!Ok(c0) || !Ok(ca)) { Check("V2: the program to be killed creates composites at indices 0 and 8", false, $"{c0} / {ca}"); return; }
            busId = "1-" + (int.Parse(PeerProcess.Field(ca, "transport")!) + 1);
            port = RecordUsbipPort(a.Pid);
            touched.Add((port, busId));
            a.Kill();
        }
        WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000);

        Stopwatch dropped;
        using (var old = PeerProcess.Start("V2-v1.12.0", "v1.12.0"))
        {
            string c0 = old.Send("createat 0 composite -");
            string c9 = old.Send("createat 9 composite -");
            if (!Ok(c0) || !Ok(c9)) { Check("V2: the v1.12.0 program creates composites on slots 0 and 9", false, $"{c0} / {c9}"); return; }
            touched.Add((port, "1-1"));
            touched.Add((port, "1-10"));
            var mine = Vhci.Imported().Where(r => r.Port == port && (r.BusId == "1-1" || r.BusId == "1-10")).ToList();
            Check($"V2: the v1.12.0 program's server takes the killed program's port {port}", mine.Count == 2,
                  string.Join(", ", Vhci.Imported().Select(r => $"{r.Port}/{r.BusId}")));

            int reset = Tcp.ResetServerEnd(port, old.Pid);
            Check("V2: the probe resets the v1.12.0 program's connections", reset == 2, $"{reset} reset");
            WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000);
            var resetAt = Stopwatch.StartNew();
            using (var sweeper = PeerProcess.Start("V2-sweeper"))
            {
                string s = sweeper.Send("sweep");
                Check("V2: another program sweeps", Ok(s), s);
            }
            int left = Vhci.Stop("127.0.0.1", port, busId);
            Check($"V2: the sweep cancels the killed program's attempt for {busId} at {port}", left == 0, $"{left} canceled by the probe");
            // The host controller's attempts go out 30 s after the reset.
            Check("V2: and leaves the v1.12.0 program's re-attach: both its composites come back",
                  WaitFor(() => Vhci.Imported().Count(r => r.Port == port && (r.BusId == "1-1" || r.BusId == "1-10")) == 2,
                          (int)Math.Max(0, 45_000 - resetAt.ElapsedMilliseconds)),
                  string.Join(", ", Vhci.Imported().Select(r => $"{r.VhciPort}:{r.Port}/{r.BusId}")));

            old.Kill();
            dropped = Stopwatch.StartNew();
        }
        Check("V3: the host controller drops the v1.12.0 imports once the program is killed",
              WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000));

        using (var nu = PeerProcess.Start("V3-new"))
        {
            string cn = nu.Send("create composite -");
            if (!Ok(cn)) { Check("V3: a program on this SDK creates a composite", false, cn); return; }
            string nuBus = "1-" + (int.Parse(PeerProcess.Field(cn, "transport")!) + 1);
            int nuPort = RecordUsbipPort(nu.Pid);
            Check($"V3: its server takes the v1.12.0 program's port {port} and its composite bus id 1-1",
                  nuPort == port && nuBus == "1-1", $"{nuPort}/{nuBus}");
            int left1 = Vhci.Stop("127.0.0.1", port, "1-1");
            Check($"V3: no re-attach attempt for 1-1 at {port} outlives the create", left1 == 0, $"{left1} canceled by the probe");

            // The first attempt goes out 30 s after the drop
            // (persistent.cpp start_attach_attempts, DELAY), so by 38 s the
            // one for 1-10 has reached the new server and been answered.
            while (dropped.Elapsed < TimeSpan.FromSeconds(38)) Thread.Sleep(250);
            Check("V3: the new program's composite is still attached",
                  Vhci.Imported().Any(r => r.Port == port && r.BusId == "1-1"));
        }

        // The answered attempt goes out again a first delay later, 30 s
        // (on_plugin_hardware, ReattachFirstDelay), at 60 s. The probe
        // listens on the port in its place and answers ST_NODEV. The
        // positive control is an attempt the probe starts there, whose
        // first try comes 30 s after it.
        var seen = new List<string>();
        using (var squat = new AttemptListener(port, seen))
        {
            touched.Add((port, "1-99"));
            Vhci.Attach("127.0.0.1", port, "1-99");
            while (dropped.Elapsed < TimeSpan.FromSeconds(78)) Thread.Sleep(250);
        }
        List<string> heard;
        lock (seen) heard = seen.ToList();
        Check("V3: the probe's own attempt reaches the probe's listener again", heard.Count(b => b == "1-99") >= 2,
              string.Join(", ", heard));
        Check("V3: the attempt for 1-10, which no record names, never comes back after it reached the new server",
              !heard.Contains("1-10"), string.Join(", ", heard));
        int left10 = Vhci.Stop("127.0.0.1", port, "1-10");
        Check("V3: and the host controller holds no attempt for 1-10", left10 == 0, $"{left10} canceled by the probe");
    }

    /// <summary>A USB/IP server that exports nothing: it answers every
    /// import with ST_NODEV and notes the bus id asked for.</summary>
    sealed class AttemptListener : IDisposable
    {
        readonly System.Net.Sockets.TcpListener _listener;
        readonly Thread _thread;
        readonly List<string> _seen;
        volatile bool _stop;

        public AttemptListener(int port, List<string> seen)
        {
            _seen = seen;
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _thread = new Thread(Run) { IsBackground = true, Name = "V squatter" };
            _thread.Start();
        }

        void Run()
        {
            while (!_stop)
            {
                if (!_listener.Pending()) { Thread.Sleep(20); continue; }
                using var s = _listener.AcceptSocket();
                try
                {
                    s.ReceiveTimeout = 5000;
                    var head = new byte[8 + 32];
                    int got = 0;
                    while (got < head.Length)
                    {
                        int n = s.Receive(head, got, head.Length - got, System.Net.Sockets.SocketFlags.None);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < head.Length || head[2] != 0x80 || head[3] != 0x03) continue;
                    int end = Array.IndexOf(head, (byte)0, 8, 32);
                    lock (_seen) _seen.Add(Encoding.ASCII.GetString(head, 8, (end < 0 ? 40 : end) - 8));
                    // op_common: version 0x0111, OP_REP_IMPORT, ST_NODEV.
                    s.Send(new byte[] { 0x01, 0x11, 0x00, 0x03, 0, 0, 0, 4 });
                    s.Receive(new byte[1]);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(10_000);
            _listener.Stop();
        }
    }

    // ── V4 ─────────────────────────────────────────────────────────────

    static void VDroppedImport(HashSet<(int Port, string BusId)> touched)
    {
        using var a = PeerProcess.Start("V4-dropped");
        string ca = a.Send("create composite -");
        if (!Ok(ca)) { Check("V4: the first program creates a composite", false, ca); return; }
        string busA = "1-" + (int.Parse(PeerProcess.Field(ca, "transport")!) + 1);
        int portA = RecordUsbipPort(a.Pid);
        touched.Add((portA, busA));
        int x = Vhci.Imported().FirstOrDefault(r => r.Port == portA && r.BusId == busA).VhciPort;
        Check($"V4: its composite {portA}/{busA} is attached", x > 0);
        if (x <= 0) return;

        int reset = Tcp.ResetServerEnd(portA, a.Pid);
        Check("V4: the probe resets the composite's connection", reset == 1, $"{reset} connection(s) reset");
        Check($"V4: the host controller drops it from vhci port {x}",
              WaitFor(() => !Vhci.Imported().Any(r => r.Port == portA && r.BusId == busA), 10_000));
        var dropped = Stopwatch.StartNew();

        using var b = PeerProcess.Start("V4-other");
        string cb = b.Send("create composite -");
        if (!Ok(cb)) { Check("V4: another program creates a composite", false, cb); return; }
        string busB = "1-" + (int.Parse(PeerProcess.Field(cb, "transport")!) + 1);
        int portB = RecordUsbipPort(b.Pid);
        touched.Add((portB, busB));
        bool OnX() => Vhci.Imported().Any(r => r.Port == portB && r.BusId == busB && r.VhciPort == x);
        Check($"V4: the other program's composite takes vhci port {x}", OnX(),
              string.Join(", ", Vhci.Imported().Select(r => $"{r.VhciPort}:{r.Port}/{r.BusId}")));

        // The host controller's attempt goes out 30 s after the drop
        // (persistent.cpp start_attach_attempts, DELAY).
        int y = 0;
        WaitFor(() => (y = Vhci.Imported().FirstOrDefault(r => r.Port == portA && r.BusId == busA).VhciPort) > 0,
                (int)Math.Max(0, 45_000 - dropped.ElapsedMilliseconds));
        Check($"V4: the host controller imports {busA} again, on another vhci port", y > 0 && y != x, $"vhci port {y}");
        int recorded = 0;
        Check("V4: the first program's record names the new vhci port",
              y > 0 && WaitFor(() => (recorded = RecordVhciPort(a.Pid)) == y, 5000), $"record {recorded}, vhci {y}");

        string d = a.Send("dispose");
        Check("V4: the first program disposes its controller", Ok(d), d);
        Thread.Sleep(1000);
        Check($"V4: the other program's composite is still on vhci port {x}", OnX(),
              string.Join(", ", Vhci.Imported().Select(r => $"{r.VhciPort}:{r.Port}/{r.BusId}")));
        Check("V4: the disposed composite is gone",
              WaitFor(() => !Vhci.Imported().Any(r => r.Port == portA && r.BusId == busA), 5000));
    }

    // ── V5 ─────────────────────────────────────────────────────────────

    static void VOwnReattach(HashSet<(int Port, string BusId)> touched)
    {
        using var p = PeerProcess.Start("V5");
        string c = p.Send("create composite -");
        if (!Ok(c)) { Check("V5: the program creates a composite", false, c); return; }
        string bus = "1-" + (int.Parse(PeerProcess.Field(c, "transport")!) + 1);
        int port = RecordUsbipPort(p.Pid);
        touched.Add((port, bus));
        // The program's own sweep cancels 1-1 to 1-8 on its port, a server
        // before v1.13.0 having left no record. Only on one of those does
        // the comeback check tell a sweep that leaves the program's exports
        // from one that does not.
        Check("V5: the composite has bus id 1-1, which the own-port sweep would cancel", bus == "1-1", bus);
        bool Attached() => Vhci.Imported().Any(r => r.Port == port && r.BusId == bus);

        bool Drop(string when)
        {
            int n = Tcp.ResetServerEnd(port, p.Pid);
            bool gone = WaitFor(() => !Attached(), 10_000);
            Check($"V5: the probe drops the composite's connection {when}", n == 1 && gone, $"{n} reset");
            return n == 1 && gone;
        }

        if (!Drop("a first time")) return;
        var dropped = Stopwatch.StartNew();
        int recorded = -1;
        Check("V5: its record's vhci port goes to 0 with the drop",
              WaitFor(() => (recorded = RecordVhciPort(p.Pid)) == 0, 3000), $"record {recorded}");
        string s = p.Send("sweep");
        Check("V5: the program's own sweep runs", Ok(s), s);
        Check($"V5: its composite {port}/{bus} comes back",
              WaitFor(Attached, (int)Math.Max(0, 45_000 - dropped.ElapsedMilliseconds)));
        if (!Attached()) return;

        if (!Drop("a second time")) return;
        string d = p.Send("dispose");
        int afterDispose = Vhci.Stop("127.0.0.1", port, bus);
        Check("V5: its dispose leaves no re-attach attempt for it", Ok(d) && afterDispose == 0, $"{d}, {afterDispose} canceled by the probe");

        string c2 = p.Send("create composite -");
        if (!Ok(c2)) { Check("V5: the program creates its composite again", false, c2); return; }
        if (!Drop("a third time")) return;
        string q = p.Send("quit");
        bool exited = WaitFor(() => p.Exited, 20_000);
        int afterExit = Vhci.Stop("127.0.0.1", port, bus);
        Check("V5: its exit without a dispose leaves no re-attach attempt for it", exited && afterExit == 0,
              $"{q}, exited {exited}, {afterExit} canceled by the probe");
    }

    // ── V6 ─────────────────────────────────────────────────────────────

    static void VListenerThatHolds(HashSet<(int Port, string BusId)> touched)
    {
        // A free port outside the SDK's range keeps these listeners away
        // from every server's port.
        const int probePort = UsbipBasePort - 2;
        using (var q = PeerProcess.Start("V6-query"))
        {
            using (new FakeServer(probePort, FakeServer.Kind.Silent))
            {
                string r = q.Send($"exports {probePort}");
                Check("V6: a listener that never answers gets no list, within the deadline",
                      PeerProcess.Field(r, "n") == "null" && int.TryParse(PeerProcess.Field(r, "ms"), out int ms) && ms < 2000, r);
            }
            using (new FakeServer(probePort, FakeServer.Kind.Trickle))
            {
                string r = q.Send($"exports {probePort}");
                Check("V6: a listener that answers one byte at a time gets no list, within the deadline",
                      PeerProcess.Field(r, "n") == "null" && int.TryParse(PeerProcess.Field(r, "ms"), out int ms) && ms < 2000, r);
            }
            using (new FakeServer(probePort, FakeServer.Kind.Oversized))
            {
                string r = q.Send($"exports {probePort}");
                Check("V6: a listener that claims more devices than a server exports gets no list",
                      PeerProcess.Field(r, "n") == "null", r);
            }
        }

        int port;
        var buses = new List<string>();
        string? ownerId;
        using (var k = PeerProcess.Start("V6-killed"))
        {
            for (int i = 9; i <= 12; i++)
            {
                string c = k.Send($"createat {i} composite -");
                if (!Ok(c)) { Check($"V6: the program to be killed creates a composite at index {i}", false, c); return; }
                buses.Add("1-" + (int.Parse(PeerProcess.Field(c, "transport")!) + 1));
            }
            port = RecordUsbipPort(k.Pid);
            ownerId = Native.OwnerRecords().Where(r => r.Value == k.Pid).Select(r => r.Key).FirstOrDefault();
            foreach (string b in buses) touched.Add((port, b));
            k.Kill();
        }
        // The attempts' first try comes 30 s after the drop, and a try that
        // reaches the silent listener would hang in its handshake, where a
        // cancel is lost. Everything below runs well inside those 30 s.
        var dropped = Stopwatch.StartNew();
        WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000);

        using var s = PeerProcess.Start("V6-sweeper");
        using (new FakeServer(port, FakeServer.Kind.Silent))
        {
            var sw = Stopwatch.StartNew();
            string r = s.Send("sweep");
            long ms = sw.ElapsedMilliseconds;
            Check("V6: a sweep asks the silent listener on the killed program's port once, not once per composite",
                  Ok(r) && ms < 2500, $"{r}, {ms} ms for {buses.Count} composites");
            int kept = Vhci.Stop("127.0.0.1", port, buses[0]);
            Check($"V6: the sweep leaves the attempt for {buses[0]}, since the listener did not say what it exports",
                  kept == 1, $"{kept} canceled by the probe");
            Check("V6: and keeps the killed program's record, which names the attempts it left",
                  ownerId != null && Native.OwnerRecords().ContainsKey(ownerId));
        }
        string r2 = s.Send("sweep");
        var left = buses.Select(b => Vhci.Stop("127.0.0.1", port, b)).ToList();
        Check("V6: once the listener is gone, the next sweep cancels every attempt the record names",
              Ok(r2) && left.All(n => n == 0), $"{string.Join(", ", left)} canceled by the probe, {dropped.ElapsedMilliseconds} ms after the drop");
        Check("V6: and removes the record", ownerId != null && !Native.OwnerRecords().ContainsKey(ownerId));
    }

    // ── V7 ─────────────────────────────────────────────────────────────

    static void VBindByLocation(HashSet<(int Port, string BusId)> touched)
    {
        using var a = PeerProcess.Start("V7-binds");
        using var b = PeerProcess.Start("V7-other");
        string ca = a.Send("create gc -");
        string cb = b.Send("create gc -");
        if (!Ok(ca) || !Ok(cb)) { Check("V7: both programs create a GameCube adapter", false, $"{ca} / {cb}"); return; }
        string busA = "1-" + (int.Parse(PeerProcess.Field(ca, "transport")!) + 1);
        string busB = "1-" + (int.Parse(PeerProcess.Field(cb, "transport")!) + 1);
        int portA = RecordUsbipPort(a.Pid), portB = RecordUsbipPort(b.Pid);
        touched.Add((portA, busA));
        touched.Add((portB, busB));
        int vhciA = 0, vhciB = 0;
        WaitFor(() =>
        {
            var rows = Vhci.Imported();
            vhciA = rows.FirstOrDefault(r => r.Port == portA && r.BusId == busA).VhciPort;
            vhciB = rows.FirstOrDefault(r => r.Port == portB && r.BusId == busB).VhciPort;
            return vhciA > 0 && vhciB > 0;
        }, 10_000);
        string? devA = null, devB = null;
        WaitFor(() => (devA = Usb.OnVhciPort(0x057E, 0x0337, vhciA)) != null && (devB = Usb.OnVhciPort(0x057E, 0x0337, vhciB)) != null, 15_000);
        if (devA == null || devB == null) { Check("V7: both adapters enumerate", false, $"vhci {vhciA} {devA}, vhci {vhciB} {devB}"); return; }
        string? serviceB = Usb.Service(devB);

        string st = a.Send($"staleport {vhciB}");
        Check($"V7: the first program's controller now names vhci port {vhciB}", st == $"OK staleport {vhciB}", st);
        string bound = a.Send("bindwinusb", 60_000);
        Check("V7: its BindWinUsb binds", bound == "OK bindwinusb Bound", bound);
        Check("V7: WinUSB is on its own adapter",
              WaitFor(() => string.Equals(Usb.Service(devA), "WinUSB", StringComparison.OrdinalIgnoreCase), 15_000),
              Usb.Service(devA) ?? "none");
        Check($"V7: the other program's adapter keeps its driver {serviceB ?? "none"}", Usb.Service(devB) == serviceB,
              Usb.Service(devB) ?? "none");
    }

    /// <summary>USB devnodes behind the usbip-win2 host controller.</summary>
    static class Usb
    {
        const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;
        const uint CM_DRP_SERVICE = 0x05, CM_DRP_ADDRESS = 0x1D;

        /// <summary>The present device of a VID and PID on one vhci port.</summary>
        public static string? OnVhciPort(ushort vid, ushort pid, int vhciPort)
        {
            string prefix = $@"USB\VID_{vid:X4}&PID_{pid:X4}\";
            const uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (CM_Get_Device_ID_List_SizeW(out uint len, "USB", flags) != 0 || len <= 1) return null;
            var buf = new char[len];
            if (CM_Get_Device_ID_ListW("USB", buf, len, flags) != 0) return null;
            foreach (string id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (Native.CM_Locate_DevNodeW(out uint dev, id, 0) != 0) continue;
                var b = new byte[4];
                uint size = 4;
                if (CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_ADDRESS, out _, b, ref size, 0) == 0
                    && BitConverter.ToUInt32(b, 0) == (uint)vhciPort)
                    return id;
            }
            return null;
        }

        /// <summary>The devnode's driver service, or null.</summary>
        public static string? Service(string id)
        {
            if (Native.CM_Locate_DevNodeW(out uint dev, id, 0) != 0) return null;
            var b = new byte[512];
            uint size = (uint)b.Length;
            if (CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_SERVICE, out _, b, ref size, 0) != 0) return null;
            return Encoding.Unicode.GetString(b, 0, (int)size).TrimEnd('\0');
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint len, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_DevNode_Registry_PropertyW(uint dev, uint property, out uint type, byte[] buffer, ref uint size, uint flags);
    }

    /// <summary>A loopback listener that takes device-list requests and
    /// answers them badly: never, one byte at a time, or with more devices
    /// than a server exports. Every connection it accepts closes when it is
    /// disposed.</summary>
    sealed class FakeServer : IDisposable
    {
        public enum Kind { Silent, Trickle, Oversized }

        readonly System.Net.Sockets.TcpListener _listener;
        readonly Kind _kind;
        readonly Thread _thread;
        readonly List<System.Net.Sockets.Socket> _held = new();
        volatile bool _stop;

        public FakeServer(int port, Kind kind)
        {
            _kind = kind;
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _thread = new Thread(Run) { IsBackground = true, Name = "V fake server" };
            _thread.Start();
        }

        void Run()
        {
            while (!_stop)
            {
                if (!_listener.Pending()) { Thread.Sleep(10); continue; }
                var s = _listener.AcceptSocket();
                lock (_held) _held.Add(s);
                if (_kind == Kind.Silent) continue;
                try
                {
                    var request = new byte[8];
                    for (int got = 0; got < 8;) { int n = s.Receive(request, got, 8 - got, System.Net.Sockets.SocketFlags.None); if (n <= 0) break; got += n; }
                    // op_common: version 0x0111, OP_REP_DEVLIST, ST_OK, then the count.
                    if (_kind == Kind.Trickle)
                    {
                        byte[] reply = { 0x01, 0x11, 0x00, 0x05, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 };
                        foreach (byte b in reply)
                        {
                            if (_stop) break;
                            s.Send(new[] { b });
                            Thread.Sleep(400);
                        }
                    }
                    else
                    {
                        const int count = 5000;
                        var reply = new byte[12 + count * 312];
                        new byte[] { 0x01, 0x11, 0x00, 0x05, 0, 0, 0, 0 }.CopyTo(reply, 0);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), count);
                        for (int i = 0; i < count; i++)
                            Encoding.ASCII.GetBytes($"1-{i + 1}").CopyTo(reply, 12 + i * 312 + 256);
                        s.Send(reply);
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(10_000);
            lock (_held) foreach (var s in _held) { try { s.Close(); } catch { } }
            _listener.Stop();
        }
    }

    /// <summary>The vhci port a program's owner record names for its one
    /// controller, or 0.</summary>
    static int RecordVhciPort(int pid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (root == null) return 0;
        foreach (string id in root.GetSubKeyNames())
        {
            using var r = root.OpenSubKey(id);
            if (r?.GetValue("Pid") is not int p || p != pid) continue;
            using var cs = r.OpenSubKey("Controllers");
            if (cs == null) return 0;
            foreach (string token in cs.GetSubKeyNames())
            {
                using var c = cs.OpenSubKey(token);
                if (c?.GetValue("VhciPort") is int vp) return vp;
            }
        }
        return 0;
    }

    /// <summary>Deletes TCP connections the way an administrator can from
    /// outside the program that holds them: SetTcpEntry with
    /// MIB_TCP_STATE_DELETE_TCB, which resets the connection.</summary>
    static class Tcp
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_CONNECTIONS = 4, MIB_TCP_STATE_ESTAB = 5, MIB_TCP_STATE_DELETE_TCB = 12;

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW_OWNER_PID { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid; }

        [StructLayout(LayoutKind.Sequential)]
        struct MIB_TCPROW { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

        /// <summary>Reset every established connection whose local end is
        /// <paramref name="port"/> in process <paramref name="pid"/>. The
        /// number reset.</summary>
        public static int ResetServerEnd(int port, int pid)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return -1;
                int n = Marshal.ReadInt32(buf), done = 0, rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < n; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                    int local = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    if (row.State != MIB_TCP_STATE_ESTAB || row.OwningPid != (uint)pid || local != port) continue;
                    var del = new MIB_TCPROW
                    {
                        State = MIB_TCP_STATE_DELETE_TCB,
                        LocalAddr = row.LocalAddr, LocalPort = row.LocalPort,
                        RemoteAddr = row.RemoteAddr, RemotePort = row.RemotePort,
                    };
                    if (SetTcpEntry(ref del) == 0) done++;
                }
                return done;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        static extern uint SetTcpEntry(ref MIB_TCPROW row);
    }

    /// <summary>The USB/IP server port a program's owner record names.</summary>
    static int RecordUsbipPort(int pid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (root == null) return 0;
        foreach (string id in root.GetSubKeyNames())
        {
            using var r = root.OpenSubKey(id);
            if (r?.GetValue("Pid") is int p && p == pid) return r.GetValue("UsbipPort") is int port ? port : 0;
        }
        return 0;
    }

    /// <summary>usbip-win2's host controller through its ioctl API, read
    /// from include/usbip/vhci.h of 0.9.7.x, 0.9.8.0 and 0.9.8.1. The layout
    /// is found the way the SDK's VhciClient finds it.</summary>
    static class Vhci
    {
        static readonly Guid Interface = new(0xB4030C06, 0xDC5F, 0x4FCC, 0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0);
        const uint PLUGIN_HARDWARE = 0x0022E000, GET_IMPORTED_DEVICES = 0x0022E008, STOP_ATTACH_ATTEMPTS = 0x0022E014;

        // plugin_hardware, stop_attach_attempts and imported_device sizes,
        // and where busid starts in the location. stop_attach_attempts ends
        // with int count, the attempts it canceled.
        static readonly (int Plugin, int Stop, int Row, int BusId)[] Layouts =
        {
            (1124, 1108, 1132, 8),
            (1120, 1104, 1128, 4),
            (1100, 1104, 1108, 4),
        };

        public static bool Available() => Path() != null;

        static string? Path()
        {
            var g = Interface;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0) != 0 || len <= 1) return null;
            var buf = new char[len];
            if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0) != 0) return null;
            int end = Array.IndexOf(buf, '\0');
            return end > 0 ? new string(buf, 0, end) : null;
        }

        static IntPtr Open() =>
            Path() is string p ? Native.CreateFileW(p, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero) : Native.INVALID;

        static (int Plugin, int Stop, int Row, int BusId)? Layout(IntPtr h, out byte[] rows, out uint written)
        {
            foreach (var l in Layouts)
            {
                var buf = new byte[4 + l.Row * 64];
                BitConverter.GetBytes((uint)(4 + l.Row)).CopyTo(buf, 0);
                if (DeviceIoControl(h, GET_IMPORTED_DEVICES, buf, buf.Length, buf, buf.Length, out written, IntPtr.Zero))
                {
                    rows = buf;
                    return l;
                }
            }
            rows = Array.Empty<byte>();
            written = 0;
            return null;
        }

        static void Location(byte[] buf, int at, string host, int port, string busId)
        {
            Encoding.ASCII.GetBytes(busId).CopyTo(buf, at);
            Encoding.ASCII.GetBytes(port.ToString()).CopyTo(buf, at + 32);
            Encoding.ASCII.GetBytes(host).CopyTo(buf, at + 64);
        }

        /// <summary>The imports: vhci port, server port and bus id.</summary>
        public static List<(int VhciPort, int Port, string BusId)> Imported()
        {
            var list = new List<(int, int, string)>();
            IntPtr h = Open();
            if (h == Native.INVALID) return list;
            try
            {
                if (Layout(h, out byte[] buf, out uint written) is not { } l) return list;
                for (int off = 4; off + l.Row <= written; off += l.Row)
                {
                    int vp = BitConverter.ToInt32(buf, off);
                    int at = off + l.BusId;
                    string bus = Z(buf, at, 32), service = Z(buf, at + 32, 32);
                    if (vp >= 1 && int.TryParse(service, out int port)) list.Add((vp, port, bus));
                }
            }
            finally { Native.CloseHandle(h); }
            return list;
        }

        /// <summary>STOP_ATTACH_ATTEMPTS for one location: the number of
        /// attempts it canceled, or -1 when the call failed.</summary>
        public static int Stop(string host, int port, string busId)
        {
            IntPtr h = Open();
            if (h == Native.INVALID) return -1;
            try
            {
                if (Layout(h, out _, out _) is not { } l) return -1;
                var buf = new byte[l.Stop];
                BitConverter.GetBytes((uint)l.Stop).CopyTo(buf, 0);
                Location(buf, 4 + l.BusId, host, port, busId);
                if (!DeviceIoControl(h, STOP_ATTACH_ATTEMPTS, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero)) return -1;
                return BitConverter.ToInt32(buf, l.Stop - 4);
            }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>PLUGIN_HARDWARE, the attach that starts re-attach
        /// attempts when it fails. True when it attached.</summary>
        public static bool Attach(string host, int port, string busId)
        {
            IntPtr h = Open();
            if (h == Native.INVALID) return false;
            try
            {
                if (Layout(h, out _, out _) is not { } l) return false;
                var buf = new byte[l.Plugin];
                BitConverter.GetBytes((uint)l.Plugin).CopyTo(buf, 0);
                Location(buf, 4 + l.BusId, host, port, busId);
                return DeviceIoControl(h, PLUGIN_HARDWARE, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero);
            }
            finally { Native.CloseHandle(h); }
        }

        static string Z(byte[] b, int at, int max)
        {
            int end = Array.IndexOf(b, (byte)0, at, max);
            return Encoding.ASCII.GetString(b, at, (end < 0 ? at + max : end) - at);
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_Interface_ListW(ref Guid g, string? id, [Out] char[] buf, uint len, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out uint returned, IntPtr overlapped);
    }
}
