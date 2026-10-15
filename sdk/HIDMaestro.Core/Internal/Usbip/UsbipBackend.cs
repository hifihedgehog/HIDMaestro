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
        SweepStaleOnce(server);

        var device = new UsbipEmulatedDevice(profile, index, identity);
        if (identity != null)
            device.VhciPortChanged = port => MachineClaims.SetControllerValue(identity.Token, "VhciPort", port);
        server.Register(device);
        try
        {
            BeforeAttach?.Invoke(device);
            device.SetVhciPortIfAttached(VhciClient.Attach("127.0.0.1", server.Port, device.BusId));
            return new UsbipBackendHandle(server, device);
        }
        catch
        {
            // The device was exported from Register on, so a re-attach
            // attempt the host controller still runs for this location can
            // have imported it ahead of this attach, which then failed
            // ST_DEV_BUSY. Remove that import with the device, the way the
            // handle's Dispose does, or it stays plugged in on a device
            // that no longer answers.
            try { VhciClient.DetachLocation("127.0.0.1", server.Port, device.BusId); } catch { }
            VhciClient.StopAttachAttempts("127.0.0.1", server.Port, device.BusId);
            server.Unregister(device);
            device.Dispose();
            throw;
        }
    }

    /// <summary>Called on the creating thread after the device is
    /// registered and before its attach. usbip_e2e_check (S45) sets it to
    /// import the device ahead of the attach, as a stray re-attach attempt
    /// can: the window is too short to hit any other way. Null outside the
    /// probe.</summary>
    internal static Action<UsbipEmulatedDevice>? BeforeAttach;

    /// <summary>Once per process: plug out imports whose server is gone and
    /// cancel their re-attach attempts. The vhci driver starts re-attach
    /// attempts whenever a connection drops without a plugout (usbip-win2
    /// device.cpp detach path), and those would spin against dead ports
    /// forever. A server is gone when nothing listens on its loopback port.
    /// An import on this process's own port that none of its controllers
    /// made was left by the program that had the port before. Another
    /// running program's imports, on a port its server still listens on,
    /// are never touched.</summary>
    private static void SweepStaleOnce(UsbipServer server)
    {
        if (Interlocked.Exchange(ref s_staleSweepDone, 1) != 0) return;
        SweepStale(server);
    }

    /// <summary>The sweep <see cref="SweepStaleOnce"/> runs. Creates on
    /// other threads pass the once-flag at once, so one can register and
    /// attach while this runs. The imports are listed before the exports
    /// are read. A device is registered before anything can import it, so
    /// an import a create of this process made is of a device the export
    /// read still sees, unless that device has gone since, and its own
    /// teardown detaches it. Registrations wait for the whole sweep
    /// (<see cref="UsbipServer.HoldingRegistrations"/>), so a new device
    /// that takes a gone device's bus id is never attached at a location
    /// the sweep then detaches.</summary>
    internal static void SweepStale(UsbipServer server)
    {
        try
        {
            server.HoldingRegistrations(() =>
            {
                DuringStaleSweep?.Invoke("start");
                var rows = VhciClient.GetImportedDevices();
                DuringStaleSweep?.Invoke("listed");
                var own = new HashSet<string>();
                foreach (var device in server.SnapshotDevices()) own.Add(device.BusId);
                DuringStaleSweep?.Invoke("exports read");
                // By location, as every sweep here detaches: the listing
                // ages while the loop plugs devices out, and a vhci port it
                // names can hold another program's device by the time its
                // row comes up (VhciClient.DetachLocation). The port may be
                // outside the SDK's range when the range was full
                // (UsbipServer), so the match is on the port alone.
                foreach (var row in rows)
                    if (row.Host == "127.0.0.1" && VhciClient.SamePort(row.Service, server.Port) && !own.Contains(row.BusId))
                        VhciClient.DetachLocation(row.Host, server.Port, row.BusId);
            });
        }
        catch { /* best-effort recovery */ }
        DetachImportsOfDeadServers();
    }

    /// <summary>Called inside <see cref="SweepStale"/> with registrations
    /// held, at "start" before the import listing, at "listed" after it and
    /// at "exports read" before the detach loop. usbip_e2e_check (S45) sets
    /// it to attach and dispose devices from other threads at those points.
    /// Null outside the probe.</summary>
    internal static Action<string>? DuringStaleSweep;

    /// <summary>Plug out every import in the SDK's port range, or on a
    /// port an owner record names, whose server no longer listens, and
    /// cancel the re-attach attempts of servers that are gone. Covers
    /// programs of every version: a v1.12.0 server listens the same way.
    /// This process's own port counts as gone for the
    /// attempts, except for the bus ids its server exports: an attempt for
    /// one of those is its own controller coming back after a dropped
    /// connection, and any other was left by the program that held the
    /// port before.</summary>
    internal static void DetachImportsOfDeadServers(SweepView? view = null)
    {
        try
        {
            if (!VhciClient.IsAvailable()) return;
            view ??= new SweepView();
            var listening = view.Listening;
            int own = UsbipServer.Current?.Port ?? 0;
            var recorded = RecordedPorts();
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (!IsOurLocation(row.Host, row.Service, recorded, out int port)) continue;
                if (port == own || listening.Contains(port)) continue;
                VhciClient.DetachLocation(row.Host, port, row.BusId);
            }
            var locations = AttemptLocations(port => port == own || !listening.Contains(port), deadOnly: true);
            VhciClient.StopAttachAttempts("127.0.0.1", WithoutLiveExports(locations, view));
        }
        catch { /* best-effort recovery */ }
    }

    // The bus ids a server before v1.13.0 canceled in its own sweep.
    private static readonly string[] s_legacyBusIds = { "1-1", "1-2", "1-3", "1-4", "1-5", "1-6", "1-7", "1-8" };

    /// <summary>The locations whose re-attach attempts a sweep cancels. A
    /// record names its server's port and each composite's transport index,
    /// whose bus id is 1-&lt;index+1&gt;, any of 1-1 to 1-4096
    /// (<see cref="ControllerLease"/>). The driver lists no attempts, so
    /// once an import has dropped nothing else names its bus id. A server
    /// before v1.13.0 leaves no record, and for the ports
    /// <paramref name="portGone"/> picks, the bus ids that release canceled
    /// itself are added. An attempt none of these name is canceled by a
    /// server this SDK runs when the attempt reaches it with a bus id the
    /// server does not export, and by nothing before that, and only while
    /// the driver still lists it: one an earlier cancel caught in its
    /// import handshake is listed nowhere and runs until the driver gives
    /// up (<see cref="UsbipServer"/>). <paramref name="deadOnly"/> false
    /// takes live programs' records too, for the sweep that removes
    /// everything.</summary>
    private static List<(int Port, string BusId)> AttemptLocations(Func<int, bool> portGone, bool deadOnly)
    {
        var list = new List<(int, string)>();
        foreach (var rec in MachineClaims.ReadAll())
        {
            if ((deadOnly && rec.Live) || rec.UsbipPort <= 0) continue;
            foreach (var c in rec.Controllers)
                if (c.TransportIndex >= 0 && string.Equals(c.Backend, "usbip", StringComparison.OrdinalIgnoreCase))
                    list.Add((rec.UsbipPort, UsbipEmulatedDevice.BusIdFor(c.TransportIndex)));
        }
        for (int port = UsbipServer.BasePort; port < UsbipServer.BasePort + UsbipServer.PortRange; port++)
        {
            if (!portGone(port)) continue;
            foreach (string busId in s_legacyBusIds) list.Add((port, busId));
        }
        return list;
    }

    /// <summary>Drop the locations a live server exports from a list of
    /// re-attach attempts to cancel. A dead program's port and transport
    /// index both go free when it ends, so a program started since can
    /// export the same location, and the driver keys an attempt by location
    /// alone: canceling it would cancel that program's own controller
    /// coming back. A port whose server does not say what it exports keeps
    /// all its attempts.</summary>
    private static List<(int Port, string BusId)> WithoutLiveExports(
        List<(int Port, string BusId)> locations, SweepView view)
    {
        var kept = new List<(int, string)>(locations.Count);
        int own = UsbipServer.Current?.Port ?? 0;
        foreach (var (port, busId) in locations)
        {
            if (port != own && !view.Listening.Contains(port)) { kept.Add((port, busId)); continue; }
            var set = view.ExportsOf(port);
            if (set != null && !set.Contains(busId)) kept.Add((port, busId));
        }
        return kept;
    }

    /// <summary>One sweep's view of the SDK's ports: which have a listener,
    /// and what each listener exports, asked once per port however many
    /// locations the sweep checks there.</summary>
    internal sealed class SweepView
    {
        private HashSet<int>? _listening;
        private readonly Dictionary<int, HashSet<string>?> _exports = new();

        public HashSet<int> Listening => _listening ??= ListeningLoopbackPorts();

        public HashSet<string>? ExportsOf(int port)
        {
            if (!_exports.TryGetValue(port, out var set)) _exports[port] = set = ExportedBusIds(port);
            return set;
        }
    }

    // A server this SDK runs exports one device per transport index, and
    // those stop at 4096 (ControllerLease). A list that claims more is not
    // one of its lists.
    private const int MaxExports = 4096;

    // The whole device-list exchange, from connect to the last byte.
    private const int ExportsDeadlineMs = 1000;

    /// <summary>The bus ids the server on a loopback port exports now: this
    /// process's own from its server, another program's from its device
    /// list (OP_REQ_DEVLIST, which the server of every release answers,
    /// proto_op.h op_devlist_reply). Null when the exchange does not finish
    /// within one second all told, or the list claims more devices than a
    /// server exports. Any program can listen on these ports, so neither
    /// its pace nor its count may hold a sweep.</summary>
    private static HashSet<string>? ExportedBusIds(int port)
    {
        var server = UsbipServer.Current;
        if (server != null && server.Port == port)
        {
            var own = new HashSet<string>();
            foreach (var device in server.SnapshotDevices()) own.Add(device.BusId);
            return own;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.SendTimeout = ExportsDeadlineMs;
            var connect = socket.ConnectAsync(System.Net.IPAddress.Loopback, port);
            if (!connect.Wait(ExportsDeadlineMs)) return null;
            Span<byte> head = stackalloc byte[8];
            UsbipProtocol.WriteOpCommon(head, UsbipProtocol.OpReqDevlist, 0);
            socket.Send(head);
            Span<byte> reply = stackalloc byte[12];
            if (!ReceiveExactly(socket, reply, clock)) return null;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(reply[2..]) != UsbipProtocol.OpRepDevlist
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reply[4..]) != UsbipProtocol.StOk)
                return null;
            uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reply[8..]);
            if (count > MaxExports) return null;
            var set = new HashSet<string>();
            var device = new byte[UsbipProtocol.UsbipUsbDeviceSize];
            var interfaces = new byte[4];
            for (uint i = 0; i < count; i++)
            {
                if (!ReceiveExactly(socket, device, clock)) return null;
                set.Add(UsbipProtocol.ReadFixedUtf8(device.AsSpan(UsbipProtocol.DevPathMax, UsbipProtocol.BusIdSize)));
                // bNumInterfaces is the block's last byte, and one
                // usbip_usb_interface of 4 bytes follows per interface.
                for (int n = device[UsbipProtocol.UsbipUsbDeviceSize - 1]; n > 0; n--)
                    if (!ReceiveExactly(socket, interfaces, clock)) return null;
            }
            return set;
        }
        catch { return null; }
    }

    /// <summary>Fill <paramref name="buffer"/> before the exchange's
    /// deadline. Each wait is the time left, through Poll, which takes
    /// microseconds: a receive timeout under 500 ms is raised to 500 ms.</summary>
    private static bool ReceiveExactly(System.Net.Sockets.Socket socket, Span<byte> buffer, System.Diagnostics.Stopwatch clock)
    {
        for (int got = 0; got < buffer.Length;)
        {
            long left = ExportsDeadlineMs - clock.ElapsedMilliseconds;
            if (left <= 0 || !socket.Poll((int)left * 1000, System.Net.Sockets.SelectMode.SelectRead)) return false;
            int n = socket.Receive(buffer[got..]);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>One dead program's composite: its import, found by the
    /// server port and bus id it was attached with, and the re-attach
    /// attempts against it. The location alone names it: the vhci port the
    /// record kept may have changed since. False when the attempt was left
    /// because the port's listener did not say what it exports: the record
    /// is then the one thing that names it, and stays for the next
    /// sweep.</summary>
    internal static bool DetachDeadImport(int serverPort, int transportIndex, SweepView view)
    {
        if (serverPort <= 0 || transportIndex < 0) return true;
        try
        {
            if (!VhciClient.IsAvailable()) return true;
            string busId = UsbipEmulatedDevice.BusIdFor(transportIndex);
            // A port with a listener has a new, live server, whose imports
            // stay. The attempt goes all the same unless that server exports
            // the location: canceling one never touches an import, and this
            // record is the last thing that names the location.
            int own = UsbipServer.Current?.Port ?? 0;
            if (serverPort != own && !view.Listening.Contains(serverPort))
                VhciClient.DetachLocation("127.0.0.1", serverPort, busId);
            else if (view.ExportsOf(serverPort) is not { } exports)
                return false;
            else if (exports.Contains(busId))
                return true;
            VhciClient.StopAttachAttempts("127.0.0.1", serverPort, busId);
            return true;
        }
        catch { return false; }
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
    /// detach their imports and cancel any re-attach attempt against
    /// them.</summary>
    internal static void DetachOwn()
    {
        try
        {
            var server = UsbipServer.Current;
            if (server == null) return;
            var locations = new List<(int, string)>();
            foreach (var device in server.SnapshotDevices())
            {
                try { server.Unregister(device); device.Dispose(); } catch { }
                try { VhciClient.DetachLocation("127.0.0.1", server.Port, device.BusId); } catch { }
                locations.Add((server.Port, device.BusId));
            }
            VhciClient.StopAttachAttempts("127.0.0.1", locations);
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
    /// <c>DeviceOrchestrator.RemoveEverything</c> walks the ROOT
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
            var recorded = RecordedPorts();
            foreach (var row in VhciClient.GetImportedDevices())
            {
                if (!IsOurLocation(row.Host, row.Service, recorded, out int port)) continue;
                VhciClient.DetachLocation(row.Host, port, row.BusId);
            }
            VhciClient.StopAttachAttempts("127.0.0.1", AttemptLocations(_ => true, deadOnly: false));
        }
        catch { /* best-effort: eviction must never throw out of a sweep */ }
    }

    /// <summary>A loopback import on the SDK's port range, or on a port an
    /// owner record names: a server binds outside the range when every port
    /// in it is taken, and its record carries the port (UsbipServer).</summary>
    private static bool IsOurLocation(string host, string service, HashSet<int> recorded, out int port) =>
        int.TryParse(service, out port) && host == "127.0.0.1" && (IsOurPort(port) || recorded.Contains(port));

    /// <summary>The server ports every owner record names, this process's
    /// own included.</summary>
    private static HashSet<int> RecordedPorts()
    {
        var set = new HashSet<int>();
        foreach (var rec in MachineClaims.ReadAll())
            if (rec.UsbipPort > 0) set.Add(rec.UsbipPort);
        if (UsbipServer.Current is { } server) set.Add(server.Port);
        return set;
    }

    private static bool IsOurPort(int port) =>
        port >= UsbipServer.BasePort && port < UsbipServer.BasePort + UsbipServer.PortRange;
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

    /// <summary>The vhci port the device is on now, 0 while it is not
    /// attached.</summary>
    public int VhciPort => Device.VhciPort;

    /// <summary>The loopback port of the server that exports the device.
    /// With the device's bus id it is the import's location.</summary>
    public int ServerPort => _server.Port;
    private int _disposed;

    internal UsbipBackendHandle(UsbipServer server, UsbipEmulatedDevice device)
    {
        _server = server;
        Device = device;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // By location: after a dropped connection the driver plugs the
        // device in again on a new port, and the port it had may hold
        // another program's device.
        try { VhciClient.DetachLocation("127.0.0.1", _server.Port, Device.BusId); } catch { }
        // An import that dropped while the controller lived left an attempt
        // for this location, and no record names it once the controller's
        // entry goes.
        VhciClient.StopAttachAttempts("127.0.0.1", _server.Port, Device.BusId);
        _server.Unregister(Device);
        Device.Dispose();
    }
}
