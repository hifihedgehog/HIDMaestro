using System;
using System.Collections.Generic;
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
                                                  DeviceIdentity? identity = null,
                                                  Action<string>? progress = null)
    {
        // Deploy on demand. Idempotent, and a no-op on every machine that
        // already has it.
        UsbipDriverInstaller.EnsureInstalled(progress);

        var server = UsbipServer.GetOrStart();
        SweepStaleOnce(server.Port);

        var device = new UsbipEmulatedDevice(profile, index, identity);
        server.Register(device);
        try
        {
            int vhciPort = VhciClient.Attach("127.0.0.1", server.Port, device.BusId);
            device.VhciPort = vhciPort;
            return new UsbipBackendHandle(server, device, vhciPort);
        }
        catch
        {
            server.Unregister(device);
            device.Dispose();
            throw;
        }
    }

    /// <summary>Once per process: plug out imports whose server is gone and
    /// cancel their re-attach attempts. The vhci driver starts re-attach
    /// attempts whenever a connection drops without a plugout (usbip-win2
    /// device.cpp detach path), and those would spin against dead ports
    /// forever. A server is gone when nothing listens on its loopback port.
    /// An import on this process's own port that none of its controllers
    /// made was left by the program that had the port before. Another
    /// running program's imports, on a port its server still listens on,
    /// are never touched.</summary>
    private static void SweepStaleOnce(int currentPort)
    {
        if (Interlocked.Exchange(ref s_staleSweepDone, 1) != 0) return;
        try
        {
            var own = new HashSet<int>();
            var server = UsbipServer.Current;
            if (server != null)
                foreach (var device in server.SnapshotDevices()) own.Add(device.VhciPort);
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (!IsOurLocation(row.Host, row.Service)) continue;
                if (row.Service == currentPort.ToString() && !own.Contains(row.Port))
                    VhciClient.Detach(row.Port);
            }
        }
        catch { /* best-effort recovery */ }
        DetachImportsOfDeadServers();
    }

    /// <summary>Plug out every import in the SDK's port range whose server
    /// no longer listens, and cancel the re-attach attempts against those
    /// ports. Covers programs of every version: a v1.12.0 server listens the
    /// same way.</summary>
    internal static void DetachImportsOfDeadServers()
    {
        try
        {
            if (!VhciClient.IsAvailable()) return;
            var listening = ListeningLoopbackPorts();
            int own = UsbipServer.Current?.Port ?? 0;
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (!IsOurLocation(row.Host, row.Service)) continue;
                int port = int.Parse(row.Service, System.Globalization.CultureInfo.InvariantCulture);
                if (port == own || listening.Contains(port)) continue;
                VhciClient.Detach(row.Port);
            }
            for (int port = UsbipServer.BasePort; port < UsbipServer.BasePort + UsbipServer.PortRange; port++)
            {
                if (port == own || listening.Contains(port)) continue;
                for (int devnum = 1; devnum <= 8; devnum++)
                    VhciClient.StopAttachAttempts("127.0.0.1", port, $"1-{devnum}");
            }
        }
        catch { /* best-effort recovery */ }
    }

    /// <summary>One dead program's composite: its import, found by the
    /// server port and bus id it was attached with, and the re-attach
    /// attempts against it.</summary>
    internal static void DetachDeadImport(int serverPort, int transportIndex, int vhciPort)
    {
        if (serverPort <= 0 || transportIndex < 0) return;
        try
        {
            if (!VhciClient.IsAvailable()) return;
            string busId = $"1-{transportIndex + 1}";
            if (ListeningLoopbackPorts().Contains(serverPort)) return;   // the port has a new, live server
            foreach (var row in VhciClient.GetImportedDevices())
                if (row.Host == "127.0.0.1" && row.Service == serverPort.ToString() && row.BusId == busId
                    && (vhciPort <= 0 || row.Port == vhciPort))
                    VhciClient.Detach(row.Port);
            VhciClient.StopAttachAttempts("127.0.0.1", serverPort, busId);
        }
        catch { }
    }

    /// <summary>Loopback TCP ports something listens on.</summary>
    private static HashSet<int> ListeningLoopbackPorts()
    {
        var set = new HashSet<int>();
        foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (System.Net.IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(System.Net.IPAddress.Any))
                set.Add(ep.Port);
        return set;
    }

    /// <summary>This process's composites: stop the emulated devices, then
    /// detach their imports.</summary>
    internal static void DetachOwn()
    {
        try
        {
            var server = UsbipServer.Current;
            if (server == null) return;
            foreach (var device in server.SnapshotDevices())
            {
                int vhciPort = device.VhciPort;
                try { server.Unregister(device); device.Dispose(); } catch { }
                if (vhciPort > 0) try { VhciClient.Detach(vhciPort); } catch { }
            }
        }
        catch { }
    }

    /// <summary>Detach every usbip device in the SDK's port range, whoever
    /// made it, the calling session's own included (issue #44). The
    /// uninstall-grade sweep's half for USB/IP.
    ///
    /// <para>A composite persona is a USB device behind the emulated host
    /// controller, and by design it carries no HIDMAESTRO token anywhere
    /// on its own devnode (#42). The device sweep in
    /// <c>DeviceOrchestrator.RemoveAllVirtualControllers</c> walks the ROOT
    /// and SWD enumerators looking for exactly that token, so it can never
    /// see a composite: an SDK consumer that created one and exited left
    /// the device enumerated behind it.</para>
    ///
    /// <para>Distinct from <see cref="SweepStaleOnce"/>, which runs once per
    /// process at create time and deliberately SKIPS the current session's
    /// port so it cannot unplug the device it is about to use. Eviction
    /// wants the opposite, so this takes no such exclusion and is not
    /// gated to a single call. Best-effort: a machine without the transport
    /// installed has nothing to detach and must not throw.</para></summary>
    public static void DetachAllOwned()
    {
        try
        {
            // Stop this process's emulated devices FIRST. Their input pump
            // threads map the shared input section directly and read it in a
            // loop, and they take no part in the stop-event drain the UMDF2
            // controllers use. The sweep destroys those sections moments
            // later, so leaving a pump running here is not a leak, it is an
            // access violation on a background thread that kills the whole
            // process. Disposing joins the pumps before anything is unmapped.
            var server = UsbipServer.Current;
            if (server != null)
            {
                foreach (var device in server.SnapshotDevices())
                {
                    try { server.Unregister(device); device.Dispose(); } catch { }
                }
            }

            if (!VhciClient.IsAvailable()) return;
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (!IsOurLocation(row.Host, row.Service)) continue;
                VhciClient.Detach(row.Port);
            }
            for (int port = UsbipServer.BasePort; port < UsbipServer.BasePort + UsbipServer.PortRange; port++)
            {
                for (int devnum = 1; devnum <= 8; devnum++)
                    VhciClient.StopAttachAttempts("127.0.0.1", port, $"1-{devnum}");
            }
        }
        catch { /* best-effort: eviction must never throw out of a sweep */ }
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
