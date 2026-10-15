// Which process owns the TCP connection the host controller imports over.
//
// usbip-win2 opens that connection from a work item with no owning process
// (wsk_cpp.cpp wsk::socket passes OwningProcess nullptr, vhci_ioctl.cpp
// connect runs from the work item's complete callback), and WskSocket takes
// "the current process" for a null OwningProcess (Learn, PFN_WSK_SOCKET).
// The server admits an import only from such a kernel socket, so this reads
// the owner the IP helper reports for the client end of the live import, on
// its own lookup, apart from the server's.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    const int SystemProcessId = 4;

    static void ImportConnectionOwner()
    {
        Console.WriteLine("\n-- The import's connection --");
        var server = UsbipServer.Current;
        if (server == null) { Check("the process runs a USB/IP server", false); return; }
        int port = server.Port;
        var clients = new List<(int LocalPort, int State, int Pid)>();
        foreach (var row in TcpRows())
        {
            if (row.RemotePort == port && row.LocalAddr == Loopback && row.RemoteAddr == Loopback)
                clients.Add((row.LocalPort, row.State, row.Pid));
        }
        // Rows in TIME_WAIT (11) are connections already closed, earlier
        // runs' included, and carry no owner.
        foreach (var c in clients)
        {
            if (c.State == 11) continue;
            string name = "?";
            try { name = Process.GetProcessById(c.Pid).ProcessName; } catch { }
            Console.WriteLine($"  [diag] client end 127.0.0.1:{c.LocalPort} -> {port} state {c.State} pid {c.Pid} ({name})");
        }
        // State 5 is MIB_TCP_STATE_ESTAB.
        var live = clients.FindAll(c => c.State == 5);
        Check("one established connection reaches the server", live.Count == 1, $"{live.Count}");
        Check("the host controller's end is owned by the System process",
              live.Count == 1 && live[0].Pid == SystemProcessId, live.Count == 1 ? $"pid {live[0].Pid}" : "");
    }

    // 127.0.0.1 as MIB_TCPROW_OWNER_PID stores it, in_addr byte order.
    const uint Loopback = 0x0100007F;

    static List<(int State, uint LocalAddr, int LocalPort, uint RemoteAddr, int RemotePort, int Pid)> TcpRows()
    {
        var rows = new List<(int, uint, int, uint, int, int)>();
        int size = 0;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                uint err = GetExtendedTcpTable(buf, ref size, false, 2 /* AF_INET */, 4 /* TCP_TABLE_OWNER_PID_CONNECTIONS */, 0);
                if (err == 0) break;
                if (err != 122 /* ERROR_INSUFFICIENT_BUFFER */) return rows;
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
            }
            if (buf == IntPtr.Zero) return rows;
            int n = Marshal.ReadInt32(buf);
            for (int i = 0; i < n; i++)
            {
                IntPtr r = buf + 4 + i * 24;
                int Port(int offset)
                {
                    uint v = (uint)Marshal.ReadInt32(r, offset);
                    return (int)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));
                }
                rows.Add((Marshal.ReadInt32(r, 0), (uint)Marshal.ReadInt32(r, 4), Port(8),
                          (uint)Marshal.ReadInt32(r, 12), Port(16), Marshal.ReadInt32(r, 20)));
            }
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        return rows;
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
                                           uint af, int tableClass, uint reserved);
}
