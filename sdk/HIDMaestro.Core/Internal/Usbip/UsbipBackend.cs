using System;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Orchestrates one usbip-backend controller's lifecycle
/// (issue #39): deploy the bundled transport if this machine does not
/// have it yet, start the in-process server, register the emulated
/// device, sweep any stale state a crashed prior process left in the
/// vhci driver, then attach and hand back a handle whose disposal
/// detaches and tears everything down in order.
///
/// <para>Composite personas just work. The usbip-win2 transport ships
/// inside HIDMaestro.Core.dll and installs itself on the first composite
/// create, exactly the way the UMDF2 driver does: no separate package,
/// no second download, nothing for a user to go find. See
/// <see cref="UsbipDriverInstaller"/>.</para></summary>
internal static class UsbipBackend
{
    /// <summary>True when the transport is already installed and a
    /// composite create needs no install step. Creating a composite
    /// controller does NOT require this to be true: it installs on
    /// demand. Consumers use this only to decide whether to warn about
    /// the one-time install.</summary>
    public static bool IsAvailable => VhciClient.IsAvailable();

    private static int s_staleSweepDone;

    public static UsbipBackendHandle CreateDevice(ControllerProfile profile, int index,
                                                  Action<string>? progress = null)
    {
        // Deploy on demand. Idempotent, and a no-op on every machine that
        // already has it.
        UsbipDriverInstaller.EnsureInstalled(progress);

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
