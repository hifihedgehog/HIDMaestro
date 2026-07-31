using System;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Orchestrates one usbip-backend controller's lifecycle
/// (issue #39): start the in-process server, register the emulated
/// device, sweep any stale state a crashed prior process left in the
/// vhci driver, then attach through usbip-win2 and hand back a handle
/// whose disposal detaches and tears everything down in order.
///
/// <para>The backend is opt-in by decree (owner ruling on the issue,
/// 2026-07-30): nothing here installs usbip-win2, and
/// <see cref="IsAvailable"/> is a pure presence check consumers can gate
/// their pickers on. Absent driver, CreateController fails loudly with
/// install guidance and the UMDF2 profiles are untouched.</para></summary>
internal static class UsbipBackend
{
    public static bool IsAvailable => VhciClient.IsAvailable();

    private static int s_staleSweepDone;

    public static UsbipBackendHandle CreateDevice(ControllerProfile profile, int index)
    {
        if (!IsAvailable)
            throw new NotSupportedException(
                $"Profile '{profile.Id}' declares backend 'usbip', which is opt-in and not installed. " +
                "Install usbip-win2 0.9.7.7 (vadimgrn/usbip-win2) to enable composite USB personas, " +
                "or use the profile's UMDF2 sibling, which keeps working with no dependency.");

        var server = UsbipServer.GetOrStart();
        SweepStaleOnce(server.Port);

        var device = new UsbipEmulatedDevice(profile, index);
        server.Register(device);
        try
        {
            int vhciPort = VhciClient.Attach("127.0.0.1", server.Port, device.BusId);
            return new UsbipBackendHandle(server, device, vhciPort);
        }
        catch
        {
            server.Unregister(device);
            device.Dispose();
            throw;
        }
    }

    /// <summary>Once per process: cancel background re-attach attempts and
    /// plug out stale imports a crashed prior session left pointing at
    /// this SDK's loopback port range. The vhci driver starts re-attach
    /// attempts whenever a connection drops without a plugout
    /// (usbip-win2 device.cpp detach path), and those would spin against
    /// dead ports forever.</summary>
    private static void SweepStaleOnce(int currentPort)
    {
        if (Interlocked.Exchange(ref s_staleSweepDone, 1) != 0) return;
        try
        {
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (!IsOurLocation(row.Host, row.Service)) continue;
                if (row.Service == currentPort.ToString()) continue; // this session's own
                VhciClient.Detach(row.Port);
            }
            for (int port = UsbipServer.BasePort; port < UsbipServer.BasePort + UsbipServer.PortRange; port++)
            {
                for (int devnum = 1; devnum <= 8; devnum++)
                    VhciClient.StopAttachAttempts("127.0.0.1", port, $"1-{devnum}");
            }
        }
        catch { /* best-effort recovery */ }
    }

    private static bool IsOurLocation(string host, string service)
    {
        if (host != "127.0.0.1") return false;
        if (!int.TryParse(service, out int port)) return false;
        return port >= UsbipServer.BasePort && port < UsbipServer.BasePort + UsbipServer.PortRange;
    }
}

/// <summary>The live backing of one usbip-backend controller. Disposal
/// order: PLUGOUT first, which makes the driver close the socket before
/// unplugging the UDE device (usbip-win2 device.cpp detach runs
/// close_socket ahead of plugout_and_delete), so the reader thread sees
/// EOF and runs the detach path; then unregister; then tear the device
/// down, which joins its pump threads before the shared sections go
/// away.</summary>
internal sealed class UsbipBackendHandle : IDisposable
{
    private readonly UsbipServer _server;
    public UsbipEmulatedDevice Device { get; }
    public int VhciPort { get; }
    private int _disposed;

    internal UsbipBackendHandle(UsbipServer server, UsbipEmulatedDevice device, int vhciPort)
    {
        _server = server;
        Device = device;
        VhciPort = vhciPort;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { VhciClient.Detach(VhciPort); } catch { }
        _server.Unregister(Device);
        Device.Dispose();
    }
}
