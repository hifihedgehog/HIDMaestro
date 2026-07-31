using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Talks to usbip-win2's vhci host controller through its public
/// device-interface ioctl API (issue #39). Grounded in the pinned
/// 0.9.7.7 sources: the interface GUID and every struct layout are
/// <c>include/usbip/vhci.h</c>, which the driver documents as a public
/// API whose input/output data stay stable for the lifetime of each
/// IOCTL code.
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
internal static class VhciClient
{
    // include/usbip/vhci.h GUID_DEVINTERFACE_USB_HOST_CONTROLLER
    private static readonly Guid VhciInterfaceGuid = new(0xB4030C06, 0xDC5F, 0x4FCC,
        0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0);

    // CTL_CODE(FILE_DEVICE_UNKNOWN, fn, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
    private const uint PLUGIN_HARDWARE = 0x0022E000;         // fn 0x800
    private const uint PLUGOUT_HARDWARE = 0x0022E004;        // fn 0x801
    private const uint GET_IMPORTED_DEVICES = 0x0022E008;    // fn 0x802
    private const uint STOP_ATTACH_ATTEMPTS = 0x0022E014;    // fn 0x805
    private const uint PLUGIN_HARDWARE_ONCE = 0x0022E018;    // fn 0x806

    private const int BusIdSize = 32;    // consts.h BUS_ID_SIZE
    private const int ServiceSize = 32;  // NI_MAXSERV
    private const int HostSize = 1025;   // NI_MAXHOST

    // vhci::ioctl::plugin_hardware: ULONG size; int port; busid[32];
    // service[32]; host[1025]; natural alignment pads the 1097 payload
    // bytes to 1100.
    private const int LocationOffset = 8;
    private const int PluginStructSize = 1100;
    // vhci::ioctl::stop_attach_attempts adds int count after host; 1101
    // payload bytes pad to 1104.
    private const int StopStructSize = 1104;
    private const int PlugoutStructSize = 8;

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

    private static SafeHandleWrapper Open()
    {
        string path = TryGetInterfacePath()
            ?? throw new NotSupportedException(
                "usbip-win2's vhci host controller is not present. Install usbip-win2 0.9.7.7 to opt in.");
        IntPtr h = CreateFileW(path, 0xC0000000 /* GENERIC_READ|WRITE */, 0, IntPtr.Zero,
            3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile('{path}') failed.");
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

    /// <summary>Attach one exported device. Blocks until the driver has
    /// connected to the server, completed the import handshake, and
    /// plugged the UDE device in. Returns the vhci port for detach.</summary>
    public static int Attach(string host, int port, string busid)
    {
        using var h = Open();
        var buf = new byte[PluginStructSize];
        BitConverter.GetBytes((uint)PluginStructSize).CopyTo(buf, 0);
        WriteLocation(buf, LocationOffset, busid, port.ToString(), host);

        if (!DeviceIoControl(h.Handle, PLUGIN_HARDWARE_ONCE, buf, (uint)buf.Length,
                buf, (uint)buf.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"usbip-win2 attach of {busid} at {host}:{port} failed.");

        int vhciPort = BitConverter.ToInt32(buf, 4);
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

    /// <summary>Cancel the driver's background re-attach attempts for one
    /// exact location. Safe to call when none exist.</summary>
    public static void StopAttachAttempts(string host, int port, string busid)
    {
        try
        {
            using var h = Open();
            var buf = new byte[StopStructSize];
            BitConverter.GetBytes((uint)StopStructSize).CopyTo(buf, 0);
            WriteLocation(buf, LocationOffset, busid, port.ToString(), host);
            DeviceIoControl(h.Handle, STOP_ATTACH_ATTEMPTS, buf, (uint)buf.Length,
                buf, (uint)buf.Length, out _, IntPtr.Zero);
        }
        catch { /* cleanup path; absence of the driver is fine */ }
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
            // imported_device = location (4 + 32 + 32 + 1025 = 1093) +
            // properties (4 devid + 4 speed + 2 + 2 = 12) = 1105, padded
            // to 1108. get_imported_devices = 4 size + pad? The struct is
            // { ULONG size; imported_device devices[]; } with the array at
            // natural alignment 4 → offset 4.
            const int RowSize = 1108;
            const int HeaderSize = 4;
            var buf = new byte[HeaderSize + RowSize * 16];
            // The driver validates r->size against sizeof(get_imported_devices),
            // which is the header plus ONE ANYSIZE_ARRAY row (1112), not the
            // caller's buffer length (vhci_ioctl.cpp get_imported_devices).
            BitConverter.GetBytes((uint)(HeaderSize + RowSize)).CopyTo(buf, 0);
            if (!DeviceIoControl(h.Handle, GET_IMPORTED_DEVICES, buf, (uint)buf.Length,
                    buf, (uint)buf.Length, out uint written, IntPtr.Zero))
                return result;
            int rows = written >= HeaderSize ? (int)((written - HeaderSize) / RowSize) : 0;
            for (int i = 0; i < rows; i++)
            {
                int off = HeaderSize + i * RowSize;
                int port = BitConverter.ToInt32(buf, off);
                string busid = ReadUtf8(buf, off + 4, BusIdSize);
                string service = ReadUtf8(buf, off + 4 + BusIdSize, ServiceSize);
                string host = ReadUtf8(buf, off + 4 + BusIdSize + ServiceSize, HostSize);
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

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid interfaceClassGuid,
        string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId,
        [Out] char[] buffer, uint bufferLen, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr sa,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
        byte[]? inBuffer, uint inSize, byte[]? outBuffer, uint outSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioControlCode,
        byte[]? inBuffer, uint inSize, IntPtr outBuffer, uint outSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
