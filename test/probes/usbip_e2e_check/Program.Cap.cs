// A real import while user-mode connections hold every handshake slot. The
// server closes a new connection past MaxHandshakes unless the System
// process owns it, so the host controller's connection must still get
// through. The idle connections are this process's own, and this probe does
// not set UsbipServer.AdmitImportsFromThisProcess, so they count as any
// other program's.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    const int PastCapIndex = 64;

    static void ImportPastFullHandshakes(HMContext ctx)
    {
        Console.WriteLine("\n-- A real import with every handshake slot held --");
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        var server = UsbipServer.GetOrStart();
        var idle = new List<Socket>();
        UsbipEmulatedDevice? device = null;
        var opened = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < UsbipServer.MaxHandshakes; i++)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Connect(IPAddress.Loopback, server.Port);
                idle.Add(s);
            }
            using (var extra = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                extra.Connect(IPAddress.Loopback, server.Port);
                Check("every handshake slot is held: one more user-mode connection is closed",
                      ClosedWithin(extra, 1000));
            }

            device = new UsbipEmulatedDevice(profile, PastCapIndex);
            server.Register(device);
            var clock = Stopwatch.StartNew();
            int port = 0;
            string error = "";
            try { port = VhciClient.Attach("127.0.0.1", server.Port, device.BusId); }
            catch (Exception ex) { error = ex.Message; }
            Check("the host controller's import still attaches", port > 0,
                  port > 0 ? $"vhci port {port} in {clock.ElapsedMilliseconds} ms" : error);
            // The idle connections' deadlines started at their accept. If
            // they had already freed the slots, the attach would pass
            // without the exemption, so every one must still be open.
            int closed = idle.FindAll(s => ClosedWithin(s, 0)).Count;
            Check("and every idle connection still held its slot after the attach", closed == 0,
                  $"{closed} of {idle.Count} closed, {opened.ElapsedMilliseconds} ms after the first connect");
        }
        finally
        {
            foreach (var s in idle) s.Dispose();
            if (device != null)
            {
                VhciClient.DetachLocation("127.0.0.1", server.Port, device.BusId);
                server.Unregister(device);
                device.Dispose();
            }
            SharedMemoryIO.DestroyController(PastCapIndex);
        }
    }

    static bool ClosedWithin(Socket s, int ms)
    {
        var sink = new byte[16];
        try
        {
            if (!s.Poll(ms * 1000, SelectMode.SelectRead)) return false;
            return s.Receive(sink) <= 0;
        }
        catch (SocketException) { return true; }
    }
}
