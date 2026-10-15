using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The process that owns the other end of a loopback TCP
/// connection, from the IP helper's connection table. The server admits an
/// import only from the host controller, whose connection the kernel opens:
/// usbip-win2 creates its socket from a work item with no owning process
/// (libdrv wsk_cpp.cpp wsk::socket passes OwningProcess nullptr, and
/// vhci_ioctl.cpp connect runs from the work item's complete callback), and
/// WskSocket takes the current process for a null OwningProcess (Learn,
/// PFN_WSK_SOCKET: "To specify the current process, a WSK application sets
/// this pointer to NULL"). A work item runs in the System process, PID 4,
/// which no user-mode program can own a socket in. usbip_e2e_check (S45)
/// reads the owner of a real import's connection and requires PID 4.</summary>
internal static partial class TcpConnectionOwner
{
    public const int SystemProcessId = 4;

    private const uint AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    // MIB_TCPROW_OWNER_PID: dwState, dwLocalAddr, dwLocalPort,
    // dwRemoteAddr, dwRemotePort, dwOwningPid, each a DWORD (tcpmib.h).
    private const int RowSize = 24;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const int SlackRows = 64;
    private const int MaxReads = 8;

    /// <summary>The PID that owns the client end of a connection this
    /// process accepted, or -1 when the table holds no ESTABLISHED row for
    /// each end (the client closed it, or the address is not IPv4) or the
    /// lookup fails. The
    /// server's accept loop calls it, so it never throws.</summary>
    public static int OfPeer(Socket accepted)
    {
        try
        {
            if (accepted.LocalEndPoint is not IPEndPoint local || accepted.RemoteEndPoint is not IPEndPoint remote
                || local.AddressFamily != AddressFamily.InterNetwork || remote.AddressFamily != AddressFamily.InterNetwork)
                return -1;
            // The client's row has the two ends the other way round: its
            // local end is this socket's remote one.
            uint clientAddr = InAddr(remote.Address);
            uint serverAddr = InAddr(local.Address);
            return Find(clientAddr, remote.Port, serverAddr, local.Port);
        }
        catch { return -1; }
    }

    // The address as MIB_TCPROW_OWNER_PID stores it, "the same format as
    // the in_addr structure": the four bytes in network order in memory.
    private static uint InAddr(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return MemoryMarshal.Read<uint>(bytes);
    }

    private static unsafe int Find(uint localAddr, int localPort, uint remoteAddr, int remotePort)
    {
        uint size = 0;
        void* table = null;
        try
        {
            // The size the call asks for is the table's size at that call,
            // and connections opened anywhere on the machine before the next
            // one grow it. Each read gets room for SlackRows more rows than
            // asked, and a read that still falls short asks again.
            for (int attempt = 0; ; attempt++)
            {
                uint err = GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (err == 0) break;
                if (err != ERROR_INSUFFICIENT_BUFFER || attempt == MaxReads) return -1;
                NativeMemory.Free(table);
                table = null;
                size += SlackRows * RowSize;
                table = NativeMemory.Alloc(size);
            }
            if (table == null) return -1;
            // Both ends of the connection, ESTABLISHED in one read of the
            // table: a client end that is closing, or one left in TIME_WAIT
            // (owned by PID 0), belongs to no live connection.
            uint count = *(uint*)table;
            int pid = -1;
            bool serverEnd = false;
            byte* row = (byte*)table + 4;
            for (uint i = 0; i < count; i++, row += RowSize)
            {
                uint* r = (uint*)row;
                if (r[0] != MIB_TCP_STATE_ESTAB) continue;
                if (r[1] == localAddr && Port(r[2]) == localPort && r[3] == remoteAddr && Port(r[4]) == remotePort)
                    pid = (int)r[5];
                else if (r[1] == remoteAddr && Port(r[2]) == remotePort && r[3] == localAddr && Port(r[4]) == localPort)
                    serverEnd = true;
            }
            return serverEnd ? pid : -1;
        }
        finally { NativeMemory.Free(table); }
    }

    // The port in network byte order in the DWORD's low 16 bits
    // (MIB_TCPROW_OWNER_PID dwLocalPort, dwRemotePort).
    private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(void* table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint af, int tableClass, uint reserved);
}
