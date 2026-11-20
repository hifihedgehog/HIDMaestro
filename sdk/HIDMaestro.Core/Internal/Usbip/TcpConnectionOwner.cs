using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

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
    // How many reads of the table a lookup makes before it answers that
    // the connection has no owner.
    private const int Lookups = 3;

    /// <summary>How often a read of the table ran out of room on every
    /// try, and how often a read held no ESTABLISHED row for both ends of
    /// the connection asked for. A check that sees a lookup miss prints
    /// them, so the miss says which it was.</summary>
    internal static int ReadsOutOfRoom, ReadsWithoutConnection;

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

    // The size the last read of the table used, which a successful call
    // writes back to pdwSize whatever the buffer's size (tested on the bench
    // machine), plus SlackRows rows. The next read starts with that much
    // room, so one call reads the table while the machine has not opened
    // more connections than the slack, the import's own two included, where
    // a call that only asks the size would walk the table first.
    private static uint s_lastSize;

    // The table is read while other programs open and close connections.
    // On 2026-11-20 two of 100 lookups of a connection that stayed open
    // answered "no owner" while sixteen threads opened and closed loopback
    // connections beside them (S43, Windows 10 19044, 4 cores), where
    // 4,000 lookups in forty later runs of the same check missed none.
    // Whether those two reads ran out of room or lacked the row was not
    // recorded, and the two counters record it from now on. A lookup that
    // finds no owner reads the table again, up to Lookups times, so a
    // connection that is there has three reads to show in, and one whose
    // peer has closed costs two more reads of a few milliseconds.
    private static int Find(uint localAddr, int localPort, uint remoteAddr, int remotePort)
    {
        for (int look = 0; look < Lookups; look++)
        {
            int pid = FindOnce(localAddr, localPort, remoteAddr, remotePort);
            if (pid >= 0) return pid;
        }
        return -1;
    }

    private static unsafe int FindOnce(uint localAddr, int localPort, uint remoteAddr, int remotePort)
    {
        uint size = Volatile.Read(ref s_lastSize);
        void* table = size > 0 ? NativeMemory.Alloc(size) : null;
        try
        {
            // The size the call asks for is the table's size at that call,
            // and connections opened anywhere on the machine before the next
            // one grow it. Each read gets room for more rows than asked, and
            // a read that still falls short asks again with twice the room
            // to spare: 64 rows, then 128, up to 8,192 on the last try.
            uint slack = SlackRows * RowSize;
            for (int attempt = 0; ; attempt++)
            {
                uint err = GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (err == 0)
                {
                    Volatile.Write(ref s_lastSize, size + SlackRows * RowSize);
                    break;
                }
                if (err != ERROR_INSUFFICIENT_BUFFER || attempt == MaxReads)
                {
                    if (err == ERROR_INSUFFICIENT_BUFFER) Interlocked.Increment(ref ReadsOutOfRoom);
                    return -1;
                }
                NativeMemory.Free(table);
                table = null;
                size += slack;
                slack *= 2;
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
            if (!serverEnd || pid < 0) Interlocked.Increment(ref ReadsWithoutConnection);
            return serverEnd ? pid : -1;
        }
        finally { NativeMemory.Free(table); }
    }

    // The port in network byte order in the DWORD's low 16 bits
    // (MIB_TCPROW_OWNER_PID dwLocalPort, dwRemotePort).
    private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    // 127.0.0.1 as the table stores an address: its four bytes in memory.
    private const uint Loopback = 0x0100007F;
    private const uint MIB_TCP_STATE_DELETE_TCB = 12;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    /// <summary>Ends each connection the host controller still holds to a
    /// USB/IP server whose process has exited, and returns how many. The
    /// host controller's attach request waits, with no timeout, on the
    /// import handshake over a connection it opens to the server. A program
    /// killed inside that handshake leaves the request waiting, and the
    /// request runs on a thread of the killed program, so the program never
    /// finishes exiting, its end of the connection never closes, and the
    /// host controller attaches nothing more for any program on the machine.
    /// S17 on the ARM64 test PC, 2026-11-19: a program killed 60 ms into a
    /// swap stayed listed with one thread in a kernel wait, the kernel held
    /// an established connection to its port, and the next program's attach
    /// waited until that connection was ended, when both went on at once.
    /// Ending the kernel's end fails the handshake, the request returns and
    /// the program finishes exiting. A connection is ended only when all of
    /// these hold: the System process owns one end, the other end is a
    /// loopback port <paramref name="isServerPort"/> accepts, and the
    /// process that owns that end has an exit code. A live program's
    /// handshake is never touched. Never throws.</summary>
    public static unsafe int EndConnectionsToExitedServers(Func<int, bool> isServerPort)
    {
        void* table = null;
        try
        {
            uint size = Volatile.Read(ref s_lastSize);
            if (size > 0) table = NativeMemory.Alloc(size);
            // Twice the room to spare on each try, as FindOnce reads it.
            uint slack = SlackRows * RowSize;
            for (int attempt = 0; ; attempt++)
            {
                uint err = GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (err == 0)
                {
                    Volatile.Write(ref s_lastSize, size + SlackRows * RowSize);
                    break;
                }
                if (err != ERROR_INSUFFICIENT_BUFFER || attempt == MaxReads) return 0;
                NativeMemory.Free(table);
                table = null;
                size += slack;
                slack *= 2;
                table = NativeMemory.Alloc(size);
            }
            if (table == null) return 0;
            uint count = *(uint*)table;
            byte* rows = (byte*)table + 4;
            int ended = 0;
            // MIB_TCPROW is a row's first five DWORDs (tcpmib.h).
            uint* row = stackalloc uint[5];
            for (uint i = 0; i < count; i++)
            {
                uint* k = (uint*)(rows + i * RowSize);
                if (k[0] != MIB_TCP_STATE_ESTAB || k[5] != SystemProcessId || k[3] != Loopback || !isServerPort(Port(k[4]))) continue;
                // The server's end of the same connection names its process.
                uint serverPid = 0;
                for (uint j = 0; j < count; j++)
                {
                    uint* s = (uint*)(rows + j * RowSize);
                    if (s[1] == k[3] && s[2] == k[4] && s[3] == k[1] && s[4] == k[2]) { serverPid = s[5]; break; }
                }
                if (serverPid <= SystemProcessId || !HasExitCode(serverPid)) continue;
                row[0] = MIB_TCP_STATE_DELETE_TCB; row[1] = k[1]; row[2] = k[2]; row[3] = k[3]; row[4] = k[4];
                uint rc = SetTcpEntry(row);
                DeviceOrchestrator.LogDiag($"    usbip: the host controller held a connection to port {Port(k[4])}, whose program (PID {serverPid}) had exited. Ending it returned {rc}.");
                if (rc == 0) ended++;
            }
            return ended;
        }
        catch { return 0; }
        finally { NativeMemory.Free(table); }
    }

    /// <summary>True for a process that still exists and has an exit code:
    /// one that was ended and has not finished exiting.</summary>
    private static bool HasExitCode(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code != STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetExtendedTcpTable(void* table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint af, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint SetTcpEntry(uint* row);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
