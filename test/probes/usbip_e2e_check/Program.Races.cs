// Races around an attach that only a real import shows: a device attached
// while the create's stale sweep runs, a device that reuses a disposed
// device's bus id during that sweep, and a device imported between its
// registration and its own attach. Each holds the SDK at the point of the
// race through a hook, and each creates one device at a time.

using System;
using System.Diagnostics;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    // Far from the indices a running program takes, and from the ones
    // usbip_server_check uses.
    const int SweepIndex = 62, StrayIndex = 63, ReuseIndex = 65;

    static void AttachDuringStaleSweep(HMContext ctx)
    {
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        var server = UsbipServer.GetOrStart();

        // A device registered and attached on another thread while the
        // stale sweep of this server's port runs, from its start. The hook
        // holds the sweep until that attach is done, 3 s at most. Register
        // waits for the sweep, so the attach lands after it, and either way
        // the sweep must leave the new import.
        Console.WriteLine("\n-- An attach during the stale sweep --");
        UsbipEmulatedDevice? swept = null;
        Exception? sweptError = null;
        Thread? other = null;
        // Set once the other thread's Register returns, read when the hook's
        // wait ends: a Register that does not wait for the sweep shows here
        // however long its attach takes.
        int registered = 0;
        bool registeredInSweep = false;
        using var attached = new ManualResetEventSlim();
        UsbipBackend.DuringStaleSweep = phase =>
        {
            if (phase != "start") return;
            other = new Thread(() =>
            {
                try
                {
                    var d = new UsbipEmulatedDevice(profile, SweepIndex);
                    swept = d;
                    server.Register(d);
                    Volatile.Write(ref registered, 1);
                    VhciClient.Attach("127.0.0.1", server.Port, d.BusId);
                }
                catch (Exception ex) { sweptError = ex; }
                finally { attached.Set(); }
            }) { IsBackground = true, Name = "e2e attach during sweep" };
            other.Start();
            attached.Wait(3000);
            registeredInSweep = Volatile.Read(ref registered) == 1;
        };
        try { UsbipBackend.SweepStale(server); }
        finally { UsbipBackend.DuringStaleSweep = null; }
        other?.Join(15000);
        try
        {
            Check("its Register waited for the sweep to end", !registeredInSweep);
            Check("the attach that started during the sweep succeeded", swept != null && sweptError == null,
                  sweptError?.Message ?? "");
            Check("and the sweep left its import in place",
                  swept != null && VhciClient.FindLocation("127.0.0.1", server.Port, swept.BusId) > 0);
        }
        finally
        {
            if (swept != null)
            {
                VhciClient.DetachLocation("127.0.0.1", server.Port, swept.BusId);
                server.Unregister(swept);
                swept.Dispose();
            }
            SharedMemoryIO.DestroyController(SweepIndex);
        }
    }

    static void BusIdReusedDuringStaleSweep(HMContext ctx)
    {
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        var server = UsbipServer.GetOrStart();

        // A device attached before the sweep lists the imports and disposed
        // after, then a new device on another thread that takes the same
        // transport index, and so the same bus id, after the sweep reads
        // the exports. The old row now names the new device's location.
        // The hook gives that attach 3 s. Its Register must wait for the
        // sweep, as in AttachDuringStaleSweep, and the sweep must not
        // detach it.
        Console.WriteLine("\n-- A bus id reused during the stale sweep --");
        UsbipEmulatedDevice? first = null, second = null;
        Exception? secondError = null;
        Thread? other = null;
        int registered = 0;
        bool registeredInSweep = false;
        using var attached = new ManualResetEventSlim();
        try
        {
            first = new UsbipEmulatedDevice(profile, ReuseIndex);
            server.Register(first);
            VhciClient.Attach("127.0.0.1", server.Port, first.BusId);
            UsbipBackend.DuringStaleSweep = phase =>
            {
                if (phase == "listed")
                {
                    // The first device's teardown, in its handle's order.
                    VhciClient.DetachLocation("127.0.0.1", server.Port, first.BusId);
                    server.Unregister(first);
                    first.Dispose();
                    SharedMemoryIO.DestroyController(ReuseIndex);
                }
                else if (phase == "exports read")
                {
                    other = new Thread(() =>
                    {
                        try
                        {
                            var d = new UsbipEmulatedDevice(profile, ReuseIndex);
                            second = d;
                            server.Register(d);
                            Volatile.Write(ref registered, 1);
                            VhciClient.Attach("127.0.0.1", server.Port, d.BusId);
                        }
                        catch (Exception ex) { secondError = ex; }
                        finally { attached.Set(); }
                    }) { IsBackground = true, Name = "e2e attach on a reused bus id" };
                    other.Start();
                    attached.Wait(3000);
                    registeredInSweep = Volatile.Read(ref registered) == 1;
                }
            };
            try { UsbipBackend.SweepStale(server); }
            finally { UsbipBackend.DuringStaleSweep = null; }
            other?.Join(15000);
            Check("its Register waited for the sweep to end", !registeredInSweep);
            Check("the new device on the reused bus id attached", second != null && secondError == null,
                  secondError?.Message ?? "");
            Check("and the sweep left its import in place",
                  second != null && VhciClient.FindLocation("127.0.0.1", server.Port, second.BusId) > 0);
        }
        finally
        {
            foreach (var d in new[] { first, second })
            {
                if (d == null || d.IsDisposed) continue;
                VhciClient.DetachLocation("127.0.0.1", server.Port, d.BusId);
                server.Unregister(d);
                d.Dispose();
            }
            SharedMemoryIO.DestroyController(ReuseIndex);
        }
    }

    static void ImportAheadOfAttach(HMContext ctx)
    {
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        var server = UsbipServer.GetOrStart();

        // A device imported between its registration and its own attach,
        // as a re-attach attempt the host controller still runs for the
        // location can import it.
        Console.WriteLine("\n-- A device imported ahead of its attach --");
        string busId = UsbipEmulatedDevice.BusIdFor(StrayIndex);
        int strayPort = 0;
        bool threw = false;
        UsbipBackend.BeforeAttach = d => strayPort = VhciClient.Attach("127.0.0.1", server.Port, d.BusId);
        try { UsbipBackend.CreateDevice(profile, StrayIndex).Dispose(); }
        catch { threw = true; }
        finally { UsbipBackend.BeforeAttach = null; }
        try
        {
            Check("the create whose device was imported first fails", threw && strayPort > 0, $"stray import on port {strayPort}");
            var clock = Stopwatch.StartNew();
            while (VhciClient.FindLocation("127.0.0.1", server.Port, busId) > 0 && clock.ElapsedMilliseconds < 3000)
                Thread.Sleep(50);
            Check("and the import that took its device is gone",
                  VhciClient.FindLocation("127.0.0.1", server.Port, busId) == 0);
        }
        finally
        {
            VhciClient.DetachLocation("127.0.0.1", server.Port, busId);
            VhciClient.StopAttachAttempts("127.0.0.1", server.Port, busId);
            SharedMemoryIO.DestroyController(StrayIndex);
        }
    }
}
