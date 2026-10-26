using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro.Internal.Usbip;
using static Program;

/// <summary>The USB/IP device's loops: its input pump, its audio pump and
/// audio event thread, and the server's accept and connection
/// threads.</summary>
internal static class Usbip
{
    /// <summary>A device built without its device, its section and its
    /// constructor, as usbip_server_check's offline input pump builds it,
    /// with the input pump started on it under <paramref name="name"/>.
    /// With no view the pump reads no frame.</summary>
    static (UsbipEmulatedDevice Device, Thread Pump) Pump(string name, IntPtr waitEvent, IntPtr view)
    {
        var t = typeof(UsbipEmulatedDevice);
        var dev = (UsbipEmulatedDevice)RuntimeHelpers.GetUninitializedObject(t);
        t.GetField("_lifetimeLock", Private)!.SetValue(dev, new object());
        t.GetField("_publishLock", Private)!.SetValue(dev, new object());
        t.GetField("_inputWaitEvent", Private)!.SetValue(dev, waitEvent);
        t.GetField("_inputView", Private)!.SetValue(dev, view);
        var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), dev, t.GetMethod("InputPumpLoop", Private)!);
        var pump = new Thread(loop) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
        pump.Start();
        return (dev, pump);
    }

    /// <summary>Stops the pump as Dispose stops it, _stop and then its
    /// event, which the pump closes as it ends.</summary>
    static void StopPump(UsbipEmulatedDevice dev, Thread pump, IntPtr waitEvent)
    {
        typeof(UsbipEmulatedDevice).GetField("_stop", Private)!.SetValue(dev, true);
        SetEvent(waitEvent);
        Checks.Check("the pump ends once stopped", pump.Join(HangMs));
    }

    /// <summary>The input pump on an event handle no event holds: each wait
    /// fails at once, and with no frame in the section the pass found
    /// nothing and went round again with no wait. Then the wait works
    /// again on an event of the probe's own.</summary>
    public static void InputPumpWait()
    {
        if (!NoObjectFails()) return;
        const string name = "HMUsbipInput_7962";
        var (dev, pump) = Pump(name, NoObject, IntPtr.Zero);
        bool bounded = CheckBounded("an input pump whose every wait fails at once", Measure());
        bool logged = Diag.Failures(name, "a wait failed with Win32 error 6", wait: bounded);
        IntPtr ev = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        typeof(UsbipEmulatedDevice).GetField("_inputWaitEvent", Private)!.SetValue(dev, ev);
        Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        StopPump(dev, pump, ev);
    }

    /// <summary>The input pump on a pass that throws: a view and no report
    /// builder, which no create leaves, so each read of the section throws.
    /// The pump waits on an event of the probe's own, which nothing sets,
    /// so a pass runs every 500 ms. Then the view goes, and the passes find
    /// no frame and throw nothing.</summary>
    public static void InputPumpThrow()
    {
        const string name = "HMUsbipInput_7966";
        IntPtr ev = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        IntPtr view = Marshal.AllocHGlobal(4096);
        Marshal.Copy(new byte[4096], 0, view, 4096);
        var (dev, pump) = Pump(name, ev, view);
        try
        {
            bool logged = Diag.Logged("the failure reaches the diagnostic log under the pump's thread name", name,
                "a pass threw System.NullReferenceException", wait: true, also: "(failure 1)");
            Diag.Logged("the second failure is logged too", name, "(failure 2)", logged);
            if (Diag.Logged("the fourth failure is logged too", name, "(failure 4)", logged))
                Diag.NotLogged("the third failure is not: lines come at the powers of two", name, "(failure 3)");
            typeof(UsbipEmulatedDevice).GetField("_inputView", Private)!.SetValue(dev, IntPtr.Zero);
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        }
        finally
        {
            StopPump(dev, pump, ev);
            Marshal.FreeHGlobal(view);
        }
    }

    static UsbAudioEngine.PendingIso Urb(UsbAudioEngine engine, uint seq, bool isIn, int packets)
    {
        var urb = engine.RentUrb(packets, 0);
        urb.Seqnum = seq;
        urb.IsIn = isIn;
        urb.TransferBufferLength = packets * 192;
        // Alternate setting 0: no stream state is inferred, and no event is
        // raised.
        urb.AltSetting = 0;
        for (int i = 0; i < packets; i++) urb.Packets[i] = ((uint)(i * 192), 192u);
        return urb;
    }

    /// <summary>The audio pump with the timer's slot of its wait on a handle
    /// no object holds, while an URB waits for a due time 8 s away: each wait
    /// fails at once, and the pass went round again with no wait. Then the
    /// slot gets the timer back.</summary>
    public static void AudioPumpWait()
    {
        if (!NoObjectFails()) return;
        var p = Profile(Catalog("dualsense-composite"));
        int microphone = 0;
        var engine = new UsbAudioEngine(p, (urb, _, _) => { if (urb.IsIn) Interlocked.Increment(ref microphone); });
        try
        {
            const string name = "HMUsbipAudioPump";
            var waitPair = (IntPtr[])typeof(UsbAudioEngine).GetField("_waitPair", Private)!.GetValue(engine)!;
            IntPtr timer = waitPair[0];
            waitPair[0] = NoObject;
            // 8,000 packets of a millisecond each.
            engine.SubmitIso(Urb(engine, 1, isIn: false, packets: 8000));
            bool bounded = CheckBounded("an audio pump whose every timer wait fails at once", Measure());
            bool logged = Diag.Failures(name, "a wait failed with Win32 error 6", wait: bounded);
            // A microphone URB, on the other direction's cursor, is due a
            // millisecond from now: the pump still completes it.
            engine.SubmitIso(Urb(engine, 2, isIn: true, packets: 1));
            bool done = WaitUntil(() => Volatile.Read(ref microphone) >= 1);
            Checks.Check("the pump still completes an URB while its timer wait fails", done, $"{Volatile.Read(ref microphone)} completion(s)");
            waitPair[0] = timer;
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        }
        finally { engine.Dispose(); }
    }

    /// <summary>The audio pump on a completion that throws: the exception
    /// ended the pump's thread, and the process with it.</summary>
    public static void AudioPumpThrow()
    {
        var p = Profile(Catalog("dualsense-composite"));
        int calls = 0;
        var engine = new UsbAudioEngine(p, (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("loop_containment_check: a completion that throws");
        });
        try
        {
            const string name = "HMUsbipAudioPump";
            engine.SubmitIso(Urb(engine, 1, isIn: false, packets: 1));
            bool first = WaitUntil(() => Volatile.Read(ref calls) >= 1);
            engine.SubmitIso(Urb(engine, 2, isIn: false, packets: 1));
            bool next = WaitUntil(() => Volatile.Read(ref calls) >= 2);
            Checks.Check("the pump completes the next URB after a completion that threw", first && next, $"{Volatile.Read(ref calls)} completion(s)");
            // The line went out before the pass that completed the next URB.
            bool logged = Diag.Logged("the failure reaches the diagnostic log under the pump's thread name", name,
                "a pass threw System.InvalidOperationException: loop_containment_check: a completion that throws (failure 1)", wait: false);
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        }
        finally { engine.Dispose(); }
    }

    /// <summary>The audio event thread on a raise that throws, in the
    /// engine's own code around a consumer's handlers: the exception ended
    /// the thread, and the process with it.</summary>
    public static void AudioEventsThrow()
    {
        var p = Profile(Catalog("dualsense-composite"));
        var engine = new UsbAudioEngine(p, (_, _, _) => { });
        int raised = 0;
        engine.ControlChanged += (_, _, _, _) =>
        {
            if (Interlocked.Increment(ref raised) == 1) throw new InvalidOperationException("loop_containment_check: a raise that throws");
        };
        try
        {
            const string name = "HMUsbipAudioEvents";
            // Two volume writes on the speaker's feature unit, the second
            // once the first is raised, so the queue does not merge them.
            engine.HandleUacSet(2, 2, 0, new byte[] { 0x00, 0xF0 });
            bool first = WaitUntil(() => Volatile.Read(ref raised) >= 1);
            engine.HandleUacSet(2, 2, 0, new byte[] { 0x00, 0xF8 });
            bool next = WaitUntil(() => Volatile.Read(ref raised) >= 2);
            Checks.Check("the event thread raises the next change after a raise that threw", first && next, $"{Volatile.Read(ref raised)} raise(s)");
            bool logged = Diag.Logged("the failure reaches the diagnostic log under the event thread's name", name,
                "a pass threw System.InvalidOperationException: loop_containment_check: a raise that throws (failure 1)", wait: false);
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        }
        finally { engine.Dispose(); }
    }

    /// <summary>A USB/IP server of this process alone, on a loopback port,
    /// started as GetOrStart starts one, without the owner record a create
    /// writes, which needs elevation.</summary>
    static UsbipServer Server() =>
        (UsbipServer)typeof(UsbipServer).GetConstructor(Private, Type.EmptyTypes)!.Invoke(null);

    /// <summary>An OP_REQ_DEVLIST on a new connection, and whether the
    /// server answered OP_REP_DEVLIST. Any program may ask it.</summary>
    static bool DeviceList(int port, out string detail)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveTimeout = HangMs };
        s.Connect(IPAddress.Loopback, port);
        var request = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(request, UsbipProtocol.Version);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), UsbipProtocol.OpReqDevlist);
        s.Send(request);
        var reply = new byte[12];
        int got = 0;
        try
        {
            while (got < reply.Length)
            {
                int n = s.Receive(reply, got, reply.Length - got, SocketFlags.None);
                if (n <= 0) break;
                got += n;
            }
        }
        catch (SocketException e)
        {
            detail = $"{e.SocketErrorCode} after {got} byte(s)";
            return false;
        }
        ushort code = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2));
        detail = $"{got} byte(s), reply 0x{code:X4}";
        return got == reply.Length && code == UsbipProtocol.OpRepDevlist;
    }

    /// <summary>The accept loop on a listener that stops listening: the
    /// accept that waits fails, and every one after it fails at once. The
    /// first failure ended the loop, and the server took no connection after
    /// it while it stayed the process's server. Then the loop gets a new
    /// listener, as no path of the SDK's gives it, to show it accepts
    /// again.</summary>
    public static void AcceptFail()
    {
        var server = Server();
        var accept = (Thread)typeof(UsbipServer).GetField("_acceptThread", Private)!.GetValue(server)!;
        var field = typeof(UsbipServer).GetField("_listener", Private)!;
        const string name = "HMUsbipAccept";
        try
        {
            bool before = DeviceList(server.Port, out string d1);
            Checks.Check("the server answers a device list", before, d1);
            ((TcpListener)field.GetValue(server)!).Stop();
            bool settled = WaitUntil(() => Diag.Find(name, "(failure 2)") != null || !accept.IsAlive);
            Checks.Check("the accept loop outlives a failed accept", settled && accept.IsAlive,
                $"the accept thread {(accept.IsAlive ? "runs" : "ended")}");
            if (!accept.IsAlive)
            {
                Diag.Logged("the failure reaches the diagnostic log under the accept thread's name", name, "(failure 1)", wait: false);
                Checks.Check("the loop accepts again on a listener that works", false, "not tried: the accept thread ended");
                return;
            }
            bool bounded = CheckBounded("an accept loop whose every accept fails at once", Measure());
            bool logged = Diag.Failures(name, "a pass threw", wait: bounded);
            var replacement = new TcpListener(IPAddress.Loopback, 0);
            replacement.Start();
            field.SetValue(server, replacement);
            int port = ((IPEndPoint)replacement.LocalEndpoint).Port;
            bool after = DeviceList(port, out string d2);
            Checks.Check("the loop accepts again on a listener that works", after, d2);
            // The loop writes the end as it begins the pass after an accept
            // a second or more after the last failure: the second device
            // list, which waits that second out first.
            Thread.Sleep(LoopFailuresQuietMs + 100);
            bool again = DeviceList(port, out string d3);
            Checks.Check("the loop accepts the connection after it", again, d3);
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        }
        finally
        {
            // Stops whichever listener the server holds now.
            server.Dispose();
            Checks.Check("Dispose ends the accept loop", accept.Join(HangMs));
        }
    }

    /// <summary>A second, as LoopFailures counts it: the first pass a
    /// second after the last failure writes the end.</summary>
    const int LoopFailuresQuietMs = 1000;

    /// <summary>A connection that ends on an exception of the server's own,
    /// not of its socket: the connection ended and said nothing. The device
    /// list throws on a device registered with no descriptors, which no
    /// create leaves.</summary>
    public static void ConnectionReport()
    {
        var server = Server();
        try
        {
            var t = typeof(UsbipEmulatedDevice);
            var broken = (UsbipEmulatedDevice)RuntimeHelpers.GetUninitializedObject(t);
            t.GetField("<BusId>k__BackingField", Private)!.SetValue(broken, "1-7964");
            server.Register(broken);
            bool answered = DeviceList(server.Port, out string d1);
            Checks.Check("a connection whose device list throws ends without an answer", !answered, d1);
            // The connection logs before it closes its socket, and the
            // device list above returned once it was closed.
            Diag.Logged("the exception that ended the connection reaches the diagnostic log under its thread name", "HMUsbipConn",
                "ended by System.NullReferenceException", wait: false);
            server.Unregister(broken);
            bool after = DeviceList(server.Port, out string d2);
            Checks.Check("the server answers the next connection", after, d2);
            // A connection the client closes before it asks anything ends on
            // its socket, which is no failure and writes nothing.
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                s.Connect(IPAddress.Loopback, server.Port);
            var connections = (System.Collections.ICollection)typeof(UsbipServer).GetField("_connections", Private)!.GetValue(server)!;
            bool ended = WaitUntil(() => { lock (connections) return connections.Count == 0; });
            Checks.Check("a connection the client closes ends", ended);
            Diag.NotLogged("a connection that ends on its socket writes nothing", "HMUsbipConn", "ended by System.Net.Sockets");
        }
        finally { server.Dispose(); }
    }
}
