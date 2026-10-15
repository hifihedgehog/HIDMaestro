// The handshake: an import whose reply fails to send.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
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
