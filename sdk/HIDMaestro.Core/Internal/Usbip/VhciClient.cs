using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Talks to usbip-win2's vhci host controller through its public
/// device-interface ioctl API (issue #39). The interface GUID and every
/// struct layout are <c>include/usbip/vhci.h</c>.
///
/// <para>The layouts are not stable across releases, so the client speaks
/// three of them and asks the driver which one it has. 0.9.8.0 appended a
/// serial and a flag to the attach request and to each imported-device
/// row. 0.9.8.1 put a <c>location_hash</c> after <c>port</c> in the
/// location that attach, stop and every row carry, which moves busid,
/// service and host 4 bytes on. Each size and offset in
/// <see cref="Layouts"/> was checked by compiling that tag's own header
/// with MSVC for x64 and ARM64. The driver compares every request's size
/// field with its own <c>sizeof</c> and refuses a mismatch before acting
/// on it (vhci_ioctl.cpp in all three tags), so a probe with the wrong
/// size is harmless.</para>
///
/// <para>Attach uses PLUGIN_HARDWARE_ONCE (function 0x806): one attempt,
/// no background retry loop, because this SDK owns the server lifecycle
/// and a failed attach should surface as an exception, not as the
/// driver's own persistent-device machinery. Detach is PLUGOUT_HARDWARE.
/// STOP_ATTACH_ATTEMPTS exists for crash recovery: when a prior process
/// died without a plugout, the driver's socket-loss path queues re-attach
/// attempts against the dead loopback server (device.cpp detach →
/// start_attach_attempts) and this cancels them by exact location.</para>
///
/// <para>Presence of the device interface doubles as backend
/// availability detection: no usbip-win2, no interface, no backend.</para></summary>
internal static partial class VhciClient
{
    // include/usbip/vhci.h GUID_DEVINTERFACE_USB_HOST_CONTROLLER, renamed
    // GUID_DEVINTERFACE_USBIP_VHCI in 0.9.8.1 with the same value.
    private static readonly Guid VhciInterfaceGuid = new(0xB4030C06, 0xDC5F, 0x4FCC,
        0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0);

    // CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA),
    // the same in every tag. 0x806 is plugin_hardware_internal in 0.9.7.x
    // and plugin_hardware_once from 0.9.8.0: one attempt, no retry loop.
    private const uint PLUGIN_HARDWARE = 0x0022E000;         // fn 0x800
    private const uint PLUGOUT_HARDWARE = 0x0022E004;        // fn 0x801
    private const uint GET_IMPORTED_DEVICES = 0x0022E008;    // fn 0x802
    private const uint STOP_ATTACH_ATTEMPTS = 0x0022E014;    // fn 0x805
    private const uint PLUGIN_HARDWARE_ONCE = 0x0022E018;    // fn 0x806

    private const int BusIdSize = 32;    // consts.h BUS_ID_SIZE
    private const int ServiceSize = 32;  // NI_MAXSERV
    private const int HostSize = 1025;   // NI_MAXHOST

    // Every request starts with ULONG size, and the location that follows
    // starts with int port, so the port an attach returns is at offset 4
    // in all three layouts, and so is the first imported-device row.
    private const int HeaderSize = 4;
    private const int PlugoutStructSize = 8;

    // vhci.cpp set_usb_ports_cnt: MAX_TOTAL_PORTS is 255. The driver fails
    // GET_IMPORTED_DEVICES with STATUS_BUFFER_TOO_SMALL when more devices
    // are attached than the buffer holds, so a buffer this size cannot
    // make the layout probe fail for that reason.
    private const int MaxPorts = 255;

    /// <summary>One release family's layout. <c>PluginSize</c>,
    /// <c>StopSize</c> and <c>RowSize</c> are sizeof
    /// plugin_hardware, stop_attach_attempts and imported_device.
    /// <c>BusIdOffset</c> is where busid starts inside the location.</summary>
    internal sealed record Layout(string Name, int PluginSize, int StopSize, int RowSize, int BusIdOffset);

    /// <summary>Newest first, which is the order the probe tries them.</summary>
    internal static readonly Layout[] Layouts =
    {
        new("0.9.8.1", PluginSize: 1124, StopSize: 1108, RowSize: 1132, BusIdOffset: 8),
        new("0.9.8.0", PluginSize: 1120, StopSize: 1104, RowSize: 1128, BusIdOffset: 4),
        new("0.9.7.x", PluginSize: 1100, StopSize: 1104, RowSize: 1108, BusIdOffset: 4),
    };

    /// <summary>True when usbip-win2's vhci controller is present and
    /// running. Requires no elevation.</summary>
    public static bool IsAvailable() => TryGetInterfacePath() != null;

    public static string? TryGetInterfacePath()
    {
        var guid = VhciInterfaceGuid;
        uint cr = CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (cr != 0 || len <= 1) return null;
        var buf = new char[len];
        cr = CM_Get_Device_Interface_ListW(ref guid, null, buf, len,
            CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
        if (cr != 0) return null;
        int end = Array.IndexOf(buf, '\0');
        if (end <= 0) return null;
        return new string(buf, 0, end);
    }

    // The host controller's interface path and the request layout it
    // answered, kept once found. Looking the path up takes two configuration
    // manager calls, about 40 microseconds at p50 on the bench, and the
    // census that lists the imports runs on every create. A failed open
    // looks the path up again, and a failed request on the kept layout
    // probes again, so a controller reinstalled while this process runs is
    // found anew.
    private static string? s_path;
    private static Layout? s_layout;

    // Rows a census reads on the kept layout. With more imports than this
    // the driver answers STATUS_BUFFER_TOO_SMALL, and the full probe reads
    // them all.
    private const int CensusRows = 16;

    private static SafeHandleWrapper Open()
    {
        string? kept = Volatile.Read(ref s_path);
        if (kept != null)
        {
            IntPtr k = CreateFileW(kept, 0xC0000000 /* GENERIC_READ|WRITE */, 0, IntPtr.Zero,
                3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (k != new IntPtr(-1)) return new SafeHandleWrapper(k);
        }
        string path = TryGetInterfacePath()
            ?? throw new InvalidOperationException(
                "The virtual USB host controller is not present. It ships inside HIDMaestro.Core.dll " +
                "and installs on first use; see UsbipDriverInstaller.EnsureInstalled.");
        IntPtr h = CreateFileW(path, 0xC0000000 /* GENERIC_READ|WRITE */, 0, IntPtr.Zero,
            3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile('{path}') failed.");
        Volatile.Write(ref s_path, path);
        return new SafeHandleWrapper(h);
    }

    private static void WriteLocation(byte[] buf, int offset, string busid, string service, string host)
    {
        Encoding.UTF8.GetBytes(busid).AsSpan(0, Math.Min(busid.Length, BusIdSize - 1))
            .CopyTo(buf.AsSpan(offset));
        Encoding.UTF8.GetBytes(service).AsSpan(0, Math.Min(service.Length, ServiceSize - 1))
            .CopyTo(buf.AsSpan(offset + BusIdSize));
        Encoding.UTF8.GetBytes(host).AsSpan(0, Math.Min(host.Length, HostSize - 1))
            .CopyTo(buf.AsSpan(offset + BusIdSize + ServiceSize));
    }

    /// <summary>Find the layout the installed driver speaks. Every tag
    /// answers GET_IMPORTED_DEVICES only when the size field equals its own
    /// sizeof(get_imported_devices), the header plus one row, and the call
    /// changes nothing, so the first candidate that succeeds is the one.
    /// The rows it returned are handed back for <see cref="GetImportedDevices"/>.</summary>
    internal static Layout Probe(IntPtr handle, out byte[] rows, out uint written)
    {
        int lastError = 0;
        foreach (var layout in Layouts)
        {
            var buf = new byte[HeaderSize + layout.RowSize * MaxPorts];
            BitConverter.GetBytes((uint)(HeaderSize + layout.RowSize)).CopyTo(buf, 0);
            if (DeviceIoControl(handle, GET_IMPORTED_DEVICES, buf, (uint)buf.Length,
                    buf, (uint)buf.Length, out written, IntPtr.Zero))
            {
                rows = buf;
                return layout;
            }
            lastError = Marshal.GetLastWin32Error();
        }
        throw new InvalidOperationException(
            "The usbip-win2 host controller accepted none of the request layouts this SDK knows " +
            $"(0.9.8.1, 0.9.8.0, 0.9.7.x). Last error 0x{lastError:X8}.");
    }

    /// <summary>GET_IMPORTED_DEVICES on one layout with room for
    /// <paramref name="rows"/> rows.</summary>
    private static bool TryList(IntPtr handle, Layout layout, int rows, out byte[] buf, out uint written)
    {
        buf = new byte[HeaderSize + layout.RowSize * rows];
        BitConverter.GetBytes((uint)(HeaderSize + layout.RowSize)).CopyTo(buf, 0);
        return DeviceIoControl(handle, GET_IMPORTED_DEVICES, buf, (uint)buf.Length,
            buf, (uint)buf.Length, out written, IntPtr.Zero);
    }

    /// <summary>The layout the installed driver speaks, for diagnostics
    /// and tests. Null when no host controller answers.</summary>
    public static string? InstalledLayout()
    {
        try
        {
            using var h = Open();
            return Probe(h.Handle, out _, out _).Name;
        }
        catch { return null; }
    }

    /// <summary>Attach one exported device. Blocks until the driver has
    /// connected to the server, completed the import handshake, and
    /// plugged the UDE device in. Returns the vhci port for detach.</summary>
    public static int Attach(string host, int port, string busid)
    {
        using var h = Open();
        var layout = Probe(h.Handle, out _, out _);
        var buf = new byte[layout.PluginSize];
        BitConverter.GetBytes((uint)layout.PluginSize).CopyTo(buf, 0);
        WriteLocation(buf, HeaderSize + layout.BusIdOffset, busid, port.ToString(), host);
        // 0.9.8.x serial and wsk_events stay zero. An empty serial leaves
        // the device's own serial string in place (wsk_receive.cpp only
        // substitutes a non-empty one), and wsk_events false keeps the IRP
        // receive path rather than WSK event callbacks (vhci_ioctl.cpp).

        if (!DeviceIoControl(h.Handle, PLUGIN_HARDWARE_ONCE, buf, (uint)buf.Length,
                buf, (uint)buf.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"usbip-win2 {layout.Name} attach of {busid} at {host}:{port} failed.");

        int vhciPort = BitConverter.ToInt32(buf, HeaderSize);
        if (vhciPort < 1)
            throw new InvalidOperationException($"usbip-win2 attach of {busid} returned port {vhciPort}.");
        return vhciPort;
    }

    /// <summary>Detach the device on a vhci port. portOrZero &lt;= 0
    /// detaches every imported device (used only by explicit cleanup).</summary>
    public static void Detach(int portOrZero)
    {
        using var h = Open();
        var buf = new byte[PlugoutStructSize];
        BitConverter.GetBytes((uint)PlugoutStructSize).CopyTo(buf, 0);
        BitConverter.GetBytes(portOrZero).CopyTo(buf, 4);
        DeviceIoControl(h.Handle, PLUGOUT_HARDWARE, buf, (uint)buf.Length,
            IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    /// <summary>Detach the imports of one exact location, wherever the
    /// driver plugged them in. A vhci port number names whatever device
    /// holds the port now: an import whose connection dropped is plugged
    /// out, and the driver's own re-attach attempt plugs it in again on the
    /// lowest free port (vhci.cpp claim_roothub_port), so a port a program
    /// remembers can hold another program's device by then. Returns how
    /// many it detached.</summary>
    public static int DetachLocation(string host, int port, string busid)
    {
        int detached = 0;
        foreach (var row in GetImportedDevices())
        {
            if (row.Host != host || !SamePort(row.Service, port) || row.BusId != busid) continue;
            Detach(row.Port);
            detached++;
        }
        return detached;
    }

    /// <summary>The vhci port the import of one exact location is on, or
    /// 0 when nothing is imported there.</summary>
    public static int FindLocation(string host, int port, string busid)
    {
        foreach (var row in GetImportedDevices())
            if (row.Host == host && SamePort(row.Service, port) && row.BusId == busid) return row.Port;
        return 0;
    }

    /// <summary>Whether an import's service string names this port. The
    /// string is whatever the PLUGIN request carried, so "018509" or
    /// " 18509" can name port 18509 as well as "18509" does. Compared as a
    /// number, the way the sweeps decide a row is on the SDK's ports
    /// (UsbipBackend), so every import a sweep picks is one it can detach.
    /// Re-attach attempts are another matter: the driver keys each by a
    /// hash of the literal host, service and bus id strings
    /// (persistent.cpp hash_location), and STOP_ATTACH_ATTEMPTS hashes the
    /// strings it is given, so <see cref="StopAttachAttempts(string, int, string)"/>
    /// cancels only attempts written the SDK's way, as
    /// <c>port.ToString()</c>.</summary>
    public static bool SamePort(string service, int port) =>
        int.TryParse(service, out int parsed) && parsed == port;

    /// <summary>The vhci port of one exact location's import, looked up as
    /// often as a caller polls without allocating: the location's bytes are
    /// encoded once, the listing goes into one buffer kept across lookups,
    /// and the rows are compared as bytes. The device is opened for each
    /// lookup and closed after it, as <see cref="FindLocation"/> does, so no
    /// lookup holds the host controller open across a driver change.
    /// usbip-win2 allows any number of opens: device_file_create adds each
    /// file object to a list (vhci.cpp:526-544, registered at :600-608).
    /// One instance is used on one thread at a time.</summary>
    internal sealed class LocationLookup
    {
        private readonly byte[] _busId;
        private readonly byte[] _service;
        private readonly byte[] _host;
        private byte[]? _buf;
        // Lookups left to skip after the interface path could not be found:
        // a failed path lookup allocates, so it is tried again only every
        // ninth lookup, 225 ms apart at the server's 25 ms poll, and never
        // given up on.
        private int _skip;
        private const int SkipAfterFailedOpen = 8;

        /// <summary>Path lookups to treat as failed before the next real
        /// one. usbip_server_check sets it through reflection: no real
        /// failure of the host controller's interface lookup can be caused
        /// safely. Zero outside the probe.</summary>
        internal static int FailPathLookups;

        public LocationLookup(string host, int port, string busid)
        {
            _host = Encoding.UTF8.GetBytes(host);
            _service = Encoding.UTF8.GetBytes(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _busId = Encoding.UTF8.GetBytes(busid);
        }

        /// <summary>The port, or 0 when nothing is imported there or no
        /// host controller answers.</summary>
        public int Find()
        {
            string? path = Volatile.Read(ref s_path);
            if (path == null)
            {
                if (_skip > 0)
                {
                    _skip--;
                    return 0;
                }
                // The first lookup in this process finds the interface path,
                // which Open keeps, and so does the first after a failed
                // open cleared it.
                bool opened;
                if (Volatile.Read(ref FailPathLookups) > 0)
                {
                    Interlocked.Decrement(ref FailPathLookups);
                    opened = false;
                }
                else
                {
                    try { Open().Dispose(); opened = true; }
                    catch { opened = false; }
                }
                if (!opened)
                {
                    _skip = SkipAfterFailedOpen;
                    return 0;
                }
                path = Volatile.Read(ref s_path);
                if (path == null) return 0;
            }
            IntPtr h = CreateFileW(path, 0xC0000000 /* GENERIC_READ|WRITE */, 0, IntPtr.Zero,
                3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                // A reinstalled controller has a new path. Look it up next
                // time.
                Interlocked.CompareExchange(ref s_path, null, path);
                return 0;
            }
            try
            {
                Layout? layout = Volatile.Read(ref s_layout);
                var buf = _buf;
                uint written = 0;
                bool listed = false;
                if (layout != null)
                {
                    if (buf == null || buf.Length < HeaderSize + layout.RowSize * CensusRows)
                        _buf = buf = new byte[HeaderSize + layout.RowSize * CensusRows];
                    BitConverter.TryWriteBytes(buf, (uint)(HeaderSize + layout.RowSize));
                    listed = DeviceIoControl(h, GET_IMPORTED_DEVICES, buf, (uint)buf.Length,
                        buf, (uint)buf.Length, out written, IntPtr.Zero);
                }
                if (!listed)
                {
                    // No kept layout, a driver changed under it, or more
                    // imports than the buffer holds: the probe's full-size
                    // listing, kept for the lookups after this one.
                    layout = Keep(Probe(h, out buf, out written));
                    _buf = buf;
                }
                int rows = written >= HeaderSize ? (int)((written - HeaderSize) / layout!.RowSize) : 0;
                for (int i = 0; i < rows; i++)
                {
                    int off = HeaderSize + i * layout!.RowSize;
                    int port = BitConverter.ToInt32(buf!, off);
                    int at = off + layout.BusIdOffset;
                    if (port >= 1
                        && Field(buf.AsSpan(at, BusIdSize)).SequenceEqual(_busId)
                        && Field(buf.AsSpan(at + BusIdSize, ServiceSize)).SequenceEqual(_service)
                        && Field(buf.AsSpan(at + BusIdSize + ServiceSize, HostSize)).SequenceEqual(_host))
                        return port;
                }
                return 0;
            }
            catch { return 0; /* best-effort, as GetImportedDevices is */ }
            finally { CloseHandle(h); }
        }

        /// <summary>A NUL-terminated field's bytes, as ReadUtf8 reads it.</summary>
        private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> field)
        {
            int end = field.IndexOf((byte)0);
            return end < 0 ? field : field[..end];
        }
    }

    /// <summary>Cancel the driver's background re-attach attempts for one
    /// exact location. Safe to call when none exist. Returns how many it
    /// canceled, or -1 when no host controller answered.</summary>
    public static int StopAttachAttempts(string host, int port, string busid) =>
        StopAttachAttempts(host, new[] { (port, busid) });

    /// <summary>The same for several locations on one handle. The driver
    /// keys an attempt by the hash of host, port and bus id and cancels by
    /// that exact hash (persistent.cpp reattach_req_remove), so every
    /// location is one request. It reports what it canceled in the
    /// request's last field, <c>int count</c> (vhci.h
    /// stop_attach_attempts, the same in all three tags).</summary>
    public static int StopAttachAttempts(string host, IReadOnlyCollection<(int Port, string BusId)> locations)
    {
        if (locations.Count == 0) return 0;
        try
        {
            using var h = Open();
            Layout layout = Volatile.Read(ref s_layout) ?? Keep(Probe(h.Handle, out _, out _));
            bool probed = false;
            var buf = new byte[layout.StopSize];
            int total = 0;
            foreach (var (port, busid) in locations)
            {
                if (!TryStop(h.Handle, layout, buf, host, port, busid))
                {
                    // A kept layout the driver no longer speaks fails the
                    // size check. Probe once and try again.
                    if (probed) continue;
                    probed = true;
                    layout = Keep(Probe(h.Handle, out _, out _));
                    buf = new byte[layout.StopSize];
                    if (!TryStop(h.Handle, layout, buf, host, port, busid)) continue;
                }
                total += BitConverter.ToInt32(buf, layout.StopSize - 4);
            }
            return total;
        }
        catch { return -1; /* cleanup path; absence of the driver is fine */ }
    }

    private static Layout Keep(Layout layout)
    {
        Volatile.Write(ref s_layout, layout);
        return layout;
    }

    private static bool TryStop(IntPtr handle, Layout layout, byte[] buf, string host, int port, string busid)
    {
        Array.Clear(buf);
        BitConverter.GetBytes((uint)layout.StopSize).CopyTo(buf, 0);
        WriteLocation(buf, HeaderSize + layout.BusIdOffset, busid, port.ToString(), host);
        return DeviceIoControl(handle, STOP_ATTACH_ATTEMPTS, buf, (uint)buf.Length,
            buf, (uint)buf.Length, out _, IntPtr.Zero);
    }

    /// <summary>Rows from GET_IMPORTED_DEVICES: (port, busid, service,
    /// host). Used by crash recovery to find and plug out stale imports
    /// that point at this SDK's server range.</summary>
    public static List<(int Port, string BusId, string Service, string Host)> GetImportedDevices()
    {
        var result = new List<(int, string, string, string)>();
        try
        {
            using var h = Open();
            // A successful call is the listing: a header of ULONG size, then
            // one imported_device row per attached device. The kept layout
            // goes first, with room for CensusRows rows. The probe answers
            // when there is no kept layout, when the driver changed under it,
            // and when more devices are attached.
            Layout? layout = Volatile.Read(ref s_layout);
            byte[] buf;
            uint written;
            if (layout == null || !TryList(h.Handle, layout, CensusRows, out buf, out written))
            {
                layout = Probe(h.Handle, out buf, out written);
                Volatile.Write(ref s_layout, layout);
            }
            int rows = written >= HeaderSize ? (int)((written - HeaderSize) / layout.RowSize) : 0;
            for (int i = 0; i < rows; i++)
            {
                int off = HeaderSize + i * layout.RowSize;
                int port = BitConverter.ToInt32(buf, off);
                int busidAt = off + layout.BusIdOffset;
                string busid = ReadUtf8(buf, busidAt, BusIdSize);
                string service = ReadUtf8(buf, busidAt + BusIdSize, ServiceSize);
                string host = ReadUtf8(buf, busidAt + BusIdSize + ServiceSize, HostSize);
                if (port >= 1) result.Add((port, busid, service, host));
            }
        }
        catch { /* detection is best-effort */ }
        return result;
    }

    private static string ReadUtf8(byte[] buf, int offset, int max)
    {
        int end = Array.IndexOf(buf, (byte)0, offset, max);
        int len = end < 0 ? max : end - offset;
        return Encoding.UTF8.GetString(buf, offset, len);
    }

    private sealed class SafeHandleWrapper : IDisposable
    {
        public IntPtr Handle { get; }
        public SafeHandleWrapper(IntPtr h) => Handle = h;
        public void Dispose() => CloseHandle(Handle);
    }

    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, [Out] char[] buffer, uint bufferLen, uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr sa, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(IntPtr device, uint ioControlCode, byte[]? inBuffer, uint inSize, byte[]? outBuffer, uint outSize, out uint bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(IntPtr device, uint ioControlCode, byte[]? inBuffer, uint inSize, IntPtr outBuffer, uint outSize, out uint bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);
}
