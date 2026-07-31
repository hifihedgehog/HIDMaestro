// USB/IP isochronous pacing spike (issue #39).
//
// The issue names this as the single largest untested risk and gates the
// design on it: "A minimal USB/IP server that answers a UAC OUT endpoint
// at 1 ms for a sustained period, measured on the Z8350, answers the
// question for a fraction of the cost of building the whole thing."
//
// What this measures, and what it deliberately does not:
//
//   MEASURED. Whether a C# USB/IP server can receive an isochronous
//   USBIP_CMD_SUBMIT, decode the 16-byte big-endian iso packet
//   descriptors, and return a well-formed USBIP_RET_SUBMIT inside the
//   1 ms service interval that bInterval 4 implies, sustained, over a
//   loopback TCP socket. That is exactly the work the real backend does
//   per URB. The wire format here is the real one, byte for byte, so
//   the encode/decode cost measured is the cost the backend pays.
//
//   NOT MEASURED. usbip-win2's kernel-side cost, the USB stack above it,
//   or real audio. No kernel driver is installed and none is needed:
//   the question is whether MANAGED CODE can hold the cadence, and a
//   kernel driver in the path can only add to the numbers below, never
//   subtract. If managed code misses here, it misses there too.
//
// The model is deliberately pessimistic: one URB in flight at a time,
// one iso packet per URB, every 1 ms. Real usbaudio.sys keeps several
// URBs in flight and batches packets, which is strictly easier to
// serve. If the synchronous shape holds, the real shape holds.
//
// Exit 0 always: this is a measurement, not a pass/fail gate. The
// verdict lines say what the numbers mean.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    // ── USB/IP wire format (kernel Documentation/usb/usbip_protocol.rst) ──

    const uint USBIP_CMD_SUBMIT = 0x00000001;
    const uint USBIP_RET_SUBMIT = 0x00000003;

    /// <summary>Fixed 48-byte USB/IP command header, all fields big-endian.</summary>
    const int HeaderSize = 48;

    /// <summary>Per-packet isochronous descriptor: offset, length,
    /// actual_length, status. Four big-endian u32s.</summary>
    const int IsoDescSize = 16;

    // DualSense USB composite, from the descriptor dump quoted in the issue:
    // interface 1 alt 1 is the OUT stream (speaker ch 1/2 + haptics ch 3/4)
    // at wMaxPacketSize 392; interface 2 alt 1 is the microphone IN stream
    // at wMaxPacketSize 196. bInterval 4 at high speed is a 1 ms interval.
    const int OutPacketBytes = 392;
    const int InPacketBytes = 196;
    const int ServiceIntervalUs = 1000;

    // ── High-resolution waitable timer ────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
                                        IntPtr routine, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    const uint TIMER_ALL_ACCESS = 0x1F0003;

    static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    static int Main(string[] args)
    {
        int seconds = 60;
        bool inDirection = false;
        foreach (var a in args)
        {
            if (a.StartsWith("--seconds=")) int.TryParse(a.Substring(10), out seconds);
            if (a == "--in") inDirection = true;
        }

        Console.WriteLine("=== USB/IP isochronous pacing spike (issue #39) ===");
        Console.WriteLine($"  direction:        {(inDirection ? "IN (microphone, 196 B/packet)" : "OUT (speaker + haptics, 392 B/packet)")}");
        Console.WriteLine($"  service interval: {ServiceIntervalUs} us (bInterval 4, high speed)");
        Console.WriteLine($"  duration:         {seconds} s  ({seconds * 1000} URBs, 1 packet each, 1 in flight)");
        Console.WriteLine($"  machine:          {Environment.MachineName}, {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"  runtime:          .NET {Environment.Version}, server GC {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine();

        // Loopback listener, the socket pair the real backend would use.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Socket? server = null;
        var accepted = new ManualResetEventSlim(false);
        var serverThread = new Thread(() =>
        {
            server = listener.Accept();
            server.NoDelay = true;
            accepted.Set();
            ServeLoop(server, inDirection);
        })
        { IsBackground = true, Priority = ThreadPriority.Highest };
        serverThread.Start();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        client.NoDelay = true;
        accepted.Wait(5000);

        int urbs = seconds * 1000;
        var roundTripUs = new double[urbs];
        var intervalUs = new double[urbs];

        // Client-side buffers, preallocated exactly as the driver's would be.
        int payload = inDirection ? 0 : OutPacketBytes;
        var cmd = new byte[HeaderSize + payload + IsoDescSize];
        var retBuf = new byte[HeaderSize + (inDirection ? InPacketBytes : 0) + IsoDescSize];

        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        bool haveTimer = timer != IntPtr.Zero;
        if (!haveTimer)
            Console.WriteLine("  [warn] high-resolution timer unavailable, falling back to spin-wait");

        var proc = Process.GetCurrentProcess();
        TimeSpan cpuBefore = proc.TotalProcessorTime;

        long tPrev = Stopwatch.GetTimestamp();
        long tStart = tPrev;
        int deadlineMisses = 0;

        for (int i = 0; i < urbs; i++)
        {
            // Pace to the next 1 ms boundary from the run's start, so drift
            // cannot accumulate the way a sleep-per-iteration loop drifts.
            long target = tStart + (long)((i + 1) * (ServiceIntervalUs / TicksToUs));
            WaitUntil(target, timer, haveTimer);

            long t0 = Stopwatch.GetTimestamp();
            intervalUs[i] = (t0 - tPrev) * TicksToUs;
            tPrev = t0;

            int cmdLen = BuildSubmit(cmd, (uint)(i + 1), inDirection, payload);
            SendAll(client, cmd, cmdLen);
            int retLen = HeaderSize + (inDirection ? InPacketBytes : 0) + IsoDescSize;
            ReceiveAll(client, retBuf, retLen);

            long t1 = Stopwatch.GetTimestamp();
            double rt = (t1 - t0) * TicksToUs;
            roundTripUs[i] = rt;
            if (rt > ServiceIntervalUs) deadlineMisses++;

            // Validate the reply once, so a broken decode cannot pass as fast.
            if (i == 0)
            {
                uint cmdWord = BinaryPrimitives.ReadUInt32BigEndian(retBuf.AsSpan(0, 4));
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(retBuf.AsSpan(4, 4));
                // RET_SUBMIT layout: status@20, actual_length@24,
                // start_frame@28, number_of_packets@32, error_count@36.
                int nPackets = BinaryPrimitives.ReadInt32BigEndian(retBuf.AsSpan(32, 4));
                int actual = BinaryPrimitives.ReadInt32BigEndian(retBuf.AsSpan(24, 4));
                bool ok = cmdWord == USBIP_RET_SUBMIT && seq == 1 && nPackets == 1
                          && actual == (inDirection ? InPacketBytes : OutPacketBytes);
                Console.WriteLine($"  wire check: RET_SUBMIT={cmdWord == USBIP_RET_SUBMIT}, seqnum={seq}, " +
                                  $"packets={nPackets}, actual_length={actual}  -> {(ok ? "VALID" : "INVALID")}");
                if (!ok) { Console.WriteLine("  [FAIL] malformed reply, numbers below are meaningless"); return 1; }
                Console.WriteLine();
            }
        }

        TimeSpan cpuAfter = proc.TotalProcessorTime;
        if (haveTimer) CloseHandle(timer);

        double wall = (Stopwatch.GetTimestamp() - tStart) * TicksToUs / 1_000_000.0;
        double cpuPct = (cpuAfter - cpuBefore).TotalSeconds / wall * 100.0;

        Report("URB round trip (submit to reply, the backend's whole job)", roundTripUs);
        Report("Service interval actually achieved (pacing fidelity)", intervalUs);

        Console.WriteLine($"  URBs:                 {urbs} over {wall:F1} s");
        Console.WriteLine($"  Deadline misses:      {deadlineMisses}  ({100.0 * deadlineMisses / urbs:F3} % of URBs over {ServiceIntervalUs} us)");
        Console.WriteLine($"  CPU while sustaining: {cpuPct:F1} % of one core (both roles in this process)");
        Console.WriteLine($"  GC collections:       gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}");
        Console.WriteLine();

        var sorted = (double[])roundTripUs.Clone();
        Array.Sort(sorted);
        double p999 = Pct(sorted, 99.9);
        Console.WriteLine(p999 < ServiceIntervalUs
            ? $"  VERDICT: managed code holds the cadence. p99.9 round trip {p999:F0} us is inside the {ServiceIntervalUs} us interval."
            : $"  VERDICT: managed code does NOT hold the cadence. p99.9 round trip {p999:F0} us exceeds the {ServiceIntervalUs} us interval.");
        Console.WriteLine("  Note: one URB in flight, one packet each, is the pessimistic shape.");
        Console.WriteLine("  Real usbaudio.sys pipelines several URBs and batches packets per URB.");

        return 0;
    }

    static void WaitUntil(long targetTicks, IntPtr timer, bool haveTimer)
    {
        long now = Stopwatch.GetTimestamp();
        double remainUs = (targetTicks - now) * TicksToUs;
        if (remainUs <= 0) return;

        // Sleep the bulk on the high-resolution timer, then spin the last
        // 150 us. The timer alone lands within ~0.5 ms on stock Windows;
        // the short spin is what a real 1 ms service loop would do too.
        if (haveTimer && remainUs > 200)
        {
            long due = -(long)((remainUs - 150) * 10);   // 100 ns units, relative
            if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(timer, 100);
        }
        while (Stopwatch.GetTimestamp() < targetTicks) Thread.SpinWait(8);
    }

    /// <summary>Encode a USBIP_CMD_SUBMIT carrying one isochronous packet.</summary>
    static int BuildSubmit(byte[] buf, uint seqnum, bool inDirection, int payload)
    {
        Array.Clear(buf, 0, HeaderSize);
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(0, 4), USBIP_CMD_SUBMIT);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(4, 4), seqnum);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(8, 4), 0x0001_0001);            // devid
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(12, 4), inDirection ? 1u : 0u); // direction
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(16, 4), inDirection ? 2u : 1u); // endpoint
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(20, 4), 0);                     // transfer_flags
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(24, 4), inDirection ? InPacketBytes : OutPacketBytes);
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(28, 4), 0);                      // start_frame
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(32, 4), 1);                      // number_of_packets
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(36, 4), 4);                      // interval (bInterval)

        int off = HeaderSize + payload;                 // OUT payload rides here
        var d = s.Slice(off, IsoDescSize);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(0, 4), 0);                                        // offset
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(4, 4), (uint)(inDirection ? InPacketBytes : OutPacketBytes)); // length
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(8, 4), 0);                                        // actual_length
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(12, 4), 0);                                       // status
        return off + IsoDescSize;
    }

    /// <summary>The backend under test. Decode a submit, do the per-URB work,
    /// encode the return. This is the loop the real USB/IP server runs.</summary>
    static void ServeLoop(Socket sock, bool inDirection)
    {
        var hdr = new byte[HeaderSize];
        var body = new byte[4096];
        var reply = new byte[HeaderSize + InPacketBytes + IsoDescSize];
        try
        {
            while (true)
            {
                ReceiveAll(sock, hdr, HeaderSize);
                uint cmd = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4, 4));
                uint dir = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(12, 4));
                int len = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(24, 4));
                int packets = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(32, 4));
                if (cmd != USBIP_CMD_SUBMIT) return;

                // OUT: read the audio payload the host is streaming to the
                // pad (speaker channels 1/2, haptics channels 3/4).
                if (dir == 0 && len > 0) ReceiveAll(sock, body, len);
                // Both directions carry one iso descriptor per packet.
                ReceiveAll(sock, body, packets * IsoDescSize);

                Array.Clear(reply, 0, HeaderSize);
                var r = reply.AsSpan();
                BinaryPrimitives.WriteUInt32BigEndian(r.Slice(0, 4), USBIP_RET_SUBMIT);
                BinaryPrimitives.WriteUInt32BigEndian(r.Slice(4, 4), seq);
                BinaryPrimitives.WriteUInt32BigEndian(r.Slice(12, 4), dir);
                BinaryPrimitives.WriteInt32BigEndian(r.Slice(20, 4), 0);              // status
                BinaryPrimitives.WriteInt32BigEndian(r.Slice(24, 4), inDirection ? InPacketBytes : len);
                BinaryPrimitives.WriteInt32BigEndian(r.Slice(28, 4), 0);              // start_frame
                BinaryPrimitives.WriteInt32BigEndian(r.Slice(32, 4), packets);        // number_of_packets
                BinaryPrimitives.WriteInt32BigEndian(r.Slice(36, 4), 0);              // error_count

                int off = HeaderSize;
                if (inDirection)
                {
                    // Microphone: the backend supplies a frame per interval.
                    // Zeroing stands in for the real capture source and costs
                    // what the real memcpy from a ring buffer would.
                    reply.AsSpan(off, InPacketBytes).Clear();
                    off += InPacketBytes;
                }
                var d = r.Slice(off, IsoDescSize);
                BinaryPrimitives.WriteUInt32BigEndian(d.Slice(0, 4), 0);
                BinaryPrimitives.WriteUInt32BigEndian(d.Slice(4, 4), (uint)(inDirection ? InPacketBytes : len));
                BinaryPrimitives.WriteUInt32BigEndian(d.Slice(8, 4), (uint)(inDirection ? InPacketBytes : len));
                BinaryPrimitives.WriteUInt32BigEndian(d.Slice(12, 4), 0);
                SendAll(sock, reply, off + IsoDescSize);
            }
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    static void SendAll(Socket s, byte[] buf, int count)
    {
        int sent = 0;
        while (sent < count) sent += s.Send(buf, sent, count - sent, SocketFlags.None);
    }

    static void ReceiveAll(Socket s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Receive(buf, got, count - got, SocketFlags.None);
            if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
            got += n;
        }
    }

    static double Pct(double[] sorted, double p)
    {
        int i = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    static void Report(string title, double[] samples)
    {
        var s = (double[])samples.Clone();
        Array.Sort(s);
        double mean = 0;
        foreach (var v in s) mean += v;
        mean /= s.Length;
        Console.WriteLine($"  {title}:");
        Console.WriteLine($"    mean {mean,8:F1} us | p50 {Pct(s, 50),8:F1} | p95 {Pct(s, 95),8:F1} | " +
                          $"p99 {Pct(s, 99),8:F1} | p99.9 {Pct(s, 99.9),8:F1} | max {s[^1],8:F1}");
        Console.WriteLine();
    }
}
