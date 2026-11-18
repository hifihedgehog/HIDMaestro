using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

// The host controller's USB 2.0 ports: how many it has, as its root hub
// reports them, and how many hold an import, as the driver lists them.
internal static partial class VhciClient
{
    // ch9.h usb_device_speed: USB_SPEED_SUPER and up take a USB 3 port
    // (vhci.cpp get_port_range), every slower speed a USB 2.0 port.
    internal const int UsbSpeedSuper = 5;

    /// <summary>Where <c>speed</c> sits in one imported_device row: the
    /// location rounded up to 4 bytes, then UINT32 devid, then the speed
    /// (vhci.h imported_device_properties, the same in all three
    /// layouts).</summary>
    internal static int SpeedOffset(Layout layout) =>
        ((layout.BusIdOffset + BusIdSize + ServiceSize + HostSize + 3) & ~3) + 4;

    /// <summary>The speed of each import the host controller lists, every
    /// program's. Empty when the transport does not answer.</summary>
    public static List<int> GetImportedSpeeds()
    {
        var result = new List<int>();
        try
        {
            using var h = Open();
            Layout? layout = Volatile.Read(ref s_layout);
            byte[] buf;
            uint written;
            if (layout == null || !TryList(h.Handle, layout, CensusRows, out buf, out written))
            {
                layout = Probe(h.Handle, out buf, out written);
                Volatile.Write(ref s_layout, layout);
            }
            int rows = written >= HeaderSize ? (int)((written - HeaderSize) / layout.RowSize) : 0;
            int at = SpeedOffset(layout);
            for (int i = 0; i < rows; i++)
            {
                int off = HeaderSize + i * layout.RowSize;
                if (BitConverter.ToInt32(buf, off) >= 1) result.Add(BitConverter.ToInt32(buf, off + at));
            }
        }
        catch { /* best-effort, as GetImportedDevices is */ }
        return result;
    }

    // usbiodef.h: CTL_CODE(FILE_DEVICE_USB (0x22), USB_GET_NODE_INFORMATION
    // (258) and USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 (279),
    // METHOD_BUFFERED, FILE_ANY_ACCESS).
    private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408;
    private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;
    private static readonly Guid UsbHubInterfaceGuid = new(0xF18A0E88, 0xC30C, 0x11D0, 0x88, 0x15, 0x00, 0xA0, 0xC9, 0x06, 0xBE, 0xD8);
    // usbioctl.h USB_PROTOCOLS.
    private const uint Usb200 = 0x2, Usb300 = 0x4;

    /// <summary>The USB 2.0 ports the host controller runs with, as its root
    /// hub reports them: the ports whose supported protocols include USB 2.0
    /// and not USB 3 (USB_NODE_CONNECTION_INFORMATION_EX_V2). That is the
    /// count the driver loaded with, after its own limits (vhci.cpp
    /// set_usb_ports_cnt). Null when the root hub does not answer.</summary>
    internal static int? Usb20PortsFromRootHub()
    {
        try
        {
            if (RootHub() is not (_, string hubId)) return null;

            var guid = UsbHubInterfaceGuid;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, hubId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || len <= 1) return null;
            var list = new char[len];
            if (CM_Get_Device_Interface_ListW(ref guid, hubId, list, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0) return null;
            string path = new string(list).Split('\0')[0];
            if (path.Length == 0) return null;

            IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE) return null;
            try
            {
                // USB_NODE_INFORMATION: NodeType, then the hub descriptor,
                // whose bNumberOfPorts is its third byte.
                var node = new byte[256];
                if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_INFORMATION, node, (uint)node.Length, node, (uint)node.Length, out uint got, IntPtr.Zero) || got < 7)
                    return null;
                int ports = node[6], usb2 = 0;
                var v2 = new byte[16];
                for (int port = 1; port <= ports; port++)
                {
                    Array.Clear(v2);
                    BitConverter.TryWriteBytes(v2.AsSpan(0), (uint)port);
                    BitConverter.TryWriteBytes(v2.AsSpan(4), (uint)v2.Length);
                    BitConverter.TryWriteBytes(v2.AsSpan(8), 0x7u);   // the protocols this caller reads
                    if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, v2, (uint)v2.Length, v2, (uint)v2.Length, out _, IntPtr.Zero))
                        return null;
                    uint protocols = BitConverter.ToUInt32(v2, 8);
                    if ((protocols & Usb200) != 0 && (protocols & Usb300) == 0) usb2++;
                }
                return usb2 > 0 ? usb2 : null;
            }
            finally { CloseHandle(h); }
        }
        catch { return null; }
    }

    /// <summary>The host controller's root hub: the one child of the
    /// devnode that carries the vhci interface. Null when the transport does
    /// not answer.</summary>
    private static (uint DevInst, string Id)? RootHub()
    {
        string? vhci = TryGetInterfacePath();
        if (vhci == null) return null;
        var key = DEVPKEY_Device_InstanceId;
        uint size = 2048;
        var idBuf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(vhci, ref key, out _, idBuf, ref size, 0) != 0) return null;
        string vhciId = Encoding.Unicode.GetString(idBuf, 0, (int)size).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out uint dev, vhciId, 0) != 0) return null;
        if (CM_Get_Child(out uint hub, dev, 0) != 0) return null;
        string? hubId = DeviceId(hub);
        return hubId == null ? null : (hub, hubId);
    }

    private static string? DeviceId(uint devInst)
    {
        var buf = new char[512];
        if (CM_Get_Device_IDW(devInst, buf, buf.Length, 0) != 0) return null;
        return new string(buf).TrimEnd('\0');
    }

    /// <summary>The instance id Windows gave the USB device on the root
    /// hub's port <paramref name="port"/>, the port an attach returned, or
    /// null while no device is there. A device's address on its hub is its
    /// port number (CM_DRP_ADDRESS). Windows names a device by its serial
    /// unless the usbflags key for its VID and PID says to ignore the serial
    /// (IgnoreHWSerNum), and then by the port, so this is the one name that
    /// holds either way.</summary>
    internal static string? DeviceOnRootHubPort(int port)
    {
        if (port < 1) return null;
        try
        {
            if (RootHub() is not (uint hub, _)) return null;
            if (CM_Get_Child(out uint dev, hub, 0) != 0) return null;
            do
            {
                uint address = 0, size = 4, type;
                if (CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_ADDRESS, out type, ref address, ref size, 0) == 0 && address == (uint)port)
                    return DeviceId(dev);
            }
            while (CM_Get_Sibling(out dev, dev, 0) == 0);
        }
        catch { }
        return null;
    }

    private const uint CM_DRP_ADDRESS = 0x1D;   // cfgmgr32.h

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // devpkey.h DEVPKEY_Device_InstanceId.
    private static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new()
    {
        fmtid = new Guid(0x78C34FC8, 0x104A, 0x4ACA, 0x9E, 0xA4, 0x52, 0x4D, 0x52, 0x99, 0x6E, 0x57),
        pid = 256,
    };

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Child(out uint child, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial uint CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint type, ref uint buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, int bufferLen, uint flags);
}
