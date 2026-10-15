// A check that needs no elevation and creates no device, which the
// `offline` argument runs alone: the owner lookup the server admits an
// import by, while other connections open and close as fast as threads can
// make them and once the peer has closed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static int OfflineOnly()
    {
        Console.WriteLine("=== USB/IP server: owner lookup ===");
        OwnerLookup();
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static void OwnerLookup()
    {
        Console.WriteLine("\n-- Connection owner lookup --");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(IPAddress.Loopback, port);
        using var accepted = listener.AcceptSocket();
        listener.Stop();
        int self = Environment.ProcessId;
        Check("the lookup finds this process as the owner of its own connection",
              TcpConnectionOwner.OfPeer(accepted) == self);

        // A flood: threads that open loopback connections, hold fifty each,
        // close them and start again, so the machine's table grows
        // and shrinks between every two calls of the lookup.
        var sink = new TcpListener(IPAddress.Loopback, 0);
        sink.Start(1024);
        int sinkPort = ((IPEndPoint)sink.LocalEndpoint).Port;
        var stop = new ManualResetEventSlim();
        var held = new List<Socket>();
        var acceptor = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                try
                {
                    var s = sink.AcceptSocket();
                    s.LingerState = new LingerOption(true, 0);
                    lock (held) held.Add(s);
                }
                catch { return; }
            }
        }) { IsBackground = true, Name = "lookup flood acceptor" };
        acceptor.Start();
        var churners = new List<Thread>();
        for (int t = 0; t < 16; t++)
        {
            var th = new Thread(() =>
            {
                var mine = new List<Socket>();
                while (!stop.IsSet)
                {
                    try
                    {
                        // Closed with a reset, so the flood leaves no
                        // TIME_WAIT rows behind to use up ephemeral ports.
                        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                        { LingerState = new LingerOption(true, 0) };
                        s.Connect(IPAddress.Loopback, sinkPort);
                        mine.Add(s);
                    }
                    catch { }
                    if (mine.Count >= 50)
                    {
                        foreach (var s in mine) s.Dispose();
                        mine.Clear();
                    }
                }
                foreach (var s in mine) s.Dispose();
            }) { IsBackground = true, Name = "lookup flood" };
            churners.Add(th);
            th.Start();
        }
        int runs = 0, wrong = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            Thread.Sleep(300);
            for (; runs < 100; runs++)
                if (TcpConnectionOwner.OfPeer(accepted) != self) wrong++;
        }
        finally
        {
            stop.Set();
            sink.Stop();
            foreach (var th in churners) th.Join(5000);
            acceptor.Join(5000);
            lock (held) foreach (var s in held) s.Dispose();
        }
        Check("under a flood of loopback connections every lookup still finds the owner", wrong == 0,
              $"{wrong} of {runs} lookups missed, {clock.ElapsedMilliseconds} ms");

        // The peer closes: its end leaves ESTABLISHED, and a connection
        // that is closing is no one's to import over.
        client.Shutdown(SocketShutdown.Both);
        client.Close();
        Thread.Sleep(200);
        int after = TcpConnectionOwner.OfPeer(accepted);
        Check("once the peer has closed, its end matches no owner", after == -1, $"pid {after}");
    }
}
