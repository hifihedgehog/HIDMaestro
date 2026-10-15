// The handshake: who may import, how long a connection may take to finish
// its handshake, how many may wait in it at once, and an import whose reply
// fails to send.
//
// The probe's sockets belong to this process, so with
// UsbipServer.AdmitImportsFromThisProcess clear they stand for any program
// on the machine other than the host controller, whose connection the
// System process owns (usbip_e2e_check reads that owner on a real import).

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static void Handshake(UsbipServer server, UsbipEmulatedDevice device)
    {
        Console.WriteLine("\n-- Handshake --");
        Check("the device is free before the handshake checks", WaitFree(device, 3000));

        // An import from a program other than the host controller.
        UsbipServer.AdmitImportsFromThisProcess = false;
        try
        {
            using (var s = Connect(server.Port))
            {
                SendImport(s, device.BusId);
                var op = ReadOrNull(s, 8, 3000);
                uint status = op == null ? uint.MaxValue : BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4));
                Check("an import from user mode is answered ST_NA", status == 1,
                      op == null ? "no reply" : $"status {status}");
                Check("and the device stays free", !Claimed(device));
            }
            Check("the device is free after it", WaitFree(device, 3000));

            using (var s = Connect(server.Port))
            {
                SendOp(s, 0x8005);
                var head = ReadOrNull(s, 12, 3000);
                Check("the device list still answers user mode",
                      head != null && BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2)) == 0x0005
                      && BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)) == 0
                      && BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8)) == 1);
            }

            // A connection that sends nothing, MaxHandshakes of them at once,
            // and one more.
            int deadline = UsbipServer.HandshakeDeadlineMs;
            Thread.Sleep(300);
            var idle = new List<Socket>();
            var opened = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < UsbipServer.MaxHandshakes; i++) idle.Add(Connect(server.Port));
                Thread.Sleep(300);
                int closedEarly = idle.FindAll(s => WaitClosed(s, 0) >= 0).Count;
                Check($"{UsbipServer.MaxHandshakes} idle connections are held through their handshake", closedEarly == 0,
                      $"{closedEarly} closed early");
                using (var extra = Connect(server.Port))
                {
                    long at = WaitClosed(extra, 1000);
                    Check("one more is closed at once", at >= 0, at >= 0 ? $"{at} ms" : "still open after 1 s");
                }
                var closedAt = new List<long>();
                foreach (var s in idle)
                {
                    long left = deadline + 1500 - opened.ElapsedMilliseconds;
                    closedAt.Add(WaitClosed(s, (int)Math.Max(0, left)) >= 0 ? opened.ElapsedMilliseconds : -1);
                }
                long first = closedAt.Count > 0 ? closedAt.Min() : -1;
                Check($"an idle connection is closed at its {deadline} ms handshake deadline",
                      !closedAt.Contains(-1) && first >= deadline - 500,
                      closedAt.Contains(-1) ? $"{closedAt.FindAll(t => t < 0).Count} still open at {deadline + 1500} ms"
                                            : $"closed from {first} to {closedAt.Max()} ms");
            }
            finally { foreach (var s in idle) s.Dispose(); }

            // A request sent one byte a second: the deadline is the
            // handshake's, not each read's.
            using (var s = Connect(server.Port))
            {
                var req = new byte[8];
                BinaryPrimitives.WriteUInt16BigEndian(req, 0x0111);
                BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), 0x8005);
                var clock = Stopwatch.StartNew();
                long closed = -1;
                for (int i = 0; i < req.Length && closed < 0; i++)
                {
                    try { s.Send(req.AsSpan(i, 1)); } catch (SocketException) { closed = clock.ElapsedMilliseconds; break; }
                    long at = WaitClosed(s, 1000);
                    if (at >= 0) closed = clock.ElapsedMilliseconds;
                }
                if (closed < 0) closed = WaitClosed(s, 1500) >= 0 ? clock.ElapsedMilliseconds : -1;
                Check("a request sent a byte a second is closed at the deadline",
                      closed >= 0 && closed < deadline + 1500,
                      closed >= 0 ? $"{closed} ms" : "answered or still open");
            }
        }
        finally { UsbipServer.AdmitImportsFromThisProcess = true; }

        Check("the device is free after the deadline checks", WaitFree(device, 3000));
        Check("an import from this process is admitted while the probe allows it",
              ImportStatus(server.Port, device.BusId) == 0);
        WaitFree(device, 3000);

        // An import whose reply fails to send after the claim.
        int fired = 0;
        UsbipServer.BeforeImportReply = _ =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0) throw new IOException("the import reply fails to send");
        };
        try
        {
            using (var s = Connect(server.Port))
            {
                SendImport(s, device.BusId);
                var op = ReadOrNull(s, 8, 3000);
                Check("an import whose reply fails gets no reply", fired == 1 && op == null);
            }
            Check("and leaves the device free", WaitFree(device, 3000));
            Check("so the next import of the bus id is answered ST_OK",
                  ImportStatus(server.Port, device.BusId) == 0);
        }
        finally { UsbipServer.BeforeImportReply = null; }
        WaitFree(device, 3000);
    }

    static Socket Connect(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        s.Connect(IPAddress.Loopback, port);
        return s;
    }

    static void SendOp(Socket s, ushort code)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(b, 0x0111);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), code);
        s.Send(b);
    }

    static void SendImport(Socket s, string busid)
    {
        SendOp(s, 0x8003);
        var b = new byte[32];
        System.Text.Encoding.UTF8.GetBytes(busid).CopyTo(b, 0);
        s.Send(b);
    }

    /// <summary>The import's status, then the connection closes. uint.MaxValue
    /// when no reply came.</summary>
    static uint ImportStatus(int port, string busid)
    {
        using var s = Connect(port);
        SendImport(s, busid);
        var op = ReadOrNull(s, 8, 3000);
        return op == null ? uint.MaxValue : BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4));
    }

    static byte[]? ReadOrNull(Socket s, int n, int ms)
    {
        var b = new byte[n];
        var clock = Stopwatch.StartNew();
        for (int got = 0; got < n;)
        {
            long left = ms - clock.ElapsedMilliseconds;
            try
            {
                if (left <= 0 || !s.Poll((int)left * 1000, SelectMode.SelectRead)) return null;
                int r = s.Receive(b, got, n - got, SocketFlags.None);
                if (r <= 0) return null;
                got += r;
            }
            catch (SocketException) { return null; }
        }
        return b;
    }

    /// <summary>Milliseconds until the server closed the connection, -1
    /// when it is still open after <paramref name="ms"/>.</summary>
    static long WaitClosed(Socket s, int ms)
    {
        var clock = Stopwatch.StartNew();
        var sink = new byte[64];
        while (true)
        {
            long left = ms - clock.ElapsedMilliseconds;
            try
            {
                if (!s.Poll((int)Math.Max(0, left) * 1000, SelectMode.SelectRead)) return -1;
                if (s.Receive(sink) <= 0) return clock.ElapsedMilliseconds;
            }
            catch (SocketException) { return clock.ElapsedMilliseconds; }
            catch (ObjectDisposedException) { return clock.ElapsedMilliseconds; }
        }
    }

    static readonly FieldInfo s_connection = typeof(UsbipEmulatedDevice)
        .GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!;

    static bool Claimed(UsbipEmulatedDevice device) => s_connection.GetValue(device) != null;

    static bool WaitFree(UsbipEmulatedDevice device, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (Claimed(device))
        {
            if (clock.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(10);
        }
        return true;
    }
}
