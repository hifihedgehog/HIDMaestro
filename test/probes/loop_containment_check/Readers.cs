using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
using static Program;

/// <summary>The wait for a frame's delivery (SharedMemoryIO.WaitInputDelivered),
/// which a keyboard's frames and a protocol host's replies wait in. It waits
/// on the acknowledgement event in steps of up to 50 ms until its timeout:
/// on a handle that fails at once, each step returned at once and the wait
/// spun to its timeout.</summary>
internal static class Delivery
{
    public static void Wait()
    {
        if (!NoObjectFails()) return;
        const int index = 7963;
        var sm = typeof(SharedMemoryIO);
        var gate = sm.GetField("s_inputViews", PrivateStatic)!.GetValue(null)!;
        var views = (Dictionary<int, IntPtr>)sm.GetField("s_inputAckViews", PrivateStatic)!.GetValue(null)!;
        var events = (Dictionary<int, IntPtr>)sm.GetField("s_inputAckEvents", PrivateStatic)!.GetValue(null)!;
        IntPtr ack = Marshal.AllocHGlobal(SharedMemoryIO.INPUT_ACK_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.INPUT_ACK_SIZE], 0, ack, SharedMemoryIO.INPUT_ACK_SIZE);
        IntPtr ev = IntPtr.Zero;
        lock (gate)
        {
            views[index] = ack;
            events[index] = NoObject;
        }
        try
        {
            const string name = "LCC delivery wait";
            Thread.CurrentThread.Name = name;
            // Frame 2 is never delivered: the section says 0.
            bool delivered = true;
            var m = Measure(() => delivered = SharedMemoryIO.WaitInputDelivered(index, 2, WindowMs));
            Checks.Check("the wait for a frame nothing delivers ends at its timeout", !delivered);
            bool bounded = CheckBounded("a delivery wait whose every wait on its event fails at once", m);
            bool logged = Diag.Logged("the failed wait reaches the diagnostic log under the waiting thread's name", name,
                "the wait for controller 7963's frame delivery failed with Win32 error 6 (failure 1)", wait: false);
            // The event works again, an event of the probe's own that
            // nothing sets. Each wait for frame 2 waits on it in steps of up
            // to 50 ms, and a step that comes a second or more after the
            // failure, and after another step, writes the end.
            ev = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
            lock (gate) events[index] = ev;
            bool timedOut = !SharedMemoryIO.WaitInputDelivered(index, 2, 120);
            if (logged && bounded)
                WaitUntil(() =>
                {
                    if (Diag.Find(name, EndText) != null) return true;
                    timedOut &= !SharedMemoryIO.WaitInputDelivered(index, 2, 120);
                    return !timedOut;
                });
            Checks.Check("a wait on an event that works ends at its timeout too", timedOut);
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, wait: false);
        }
        finally
        {
            lock (gate)
            {
                views.Remove(index);
                events.Remove(index);
            }
            Marshal.FreeHGlobal(ack);
            if (ev != IntPtr.Zero) CloseHandle(ev);
        }
    }
}

/// <summary>The VR haptic reader, which reads the haptic ring the OpenVR
/// driver writes and raises HapticReceived. A pass that threw ended the
/// reader's thread, and the process with it. The controller is built
/// without SteamVR and without its constructor, which makes a global
/// section: the channel is an unnamed section and an unnamed event.</summary>
internal static class Haptic
{
    const uint PAGE_READWRITE = 0x04, FILE_MAP_ALL_ACCESS = 0xF001F;

    static unsafe void Publish(VrSharedMemory.Channel ch, ulong seq)
    {
        byte* v = (byte*)ch.View;
        byte* slot = v + VrSharedMemory.HapticRingOffset + (int)((seq - 1) % VrSharedMemory.HapticSlots) * VrSharedMemory.HapticReplySize;
        *(uint*)(slot + 8) = 0;           // left hand
        *(float*)(slot + 12) = 0.01f;     // duration
        *(float*)(slot + 16) = 160f;      // frequency
        *(float*)(slot + 20) = 0.5f;      // amplitude
        *(ulong*)(slot + 24) = 1;         // timestamp
        Volatile.Write(ref *(ulong*)slot, seq);
        Volatile.Write(ref *(ulong*)(v + VrSharedMemory.HapticHeadOffset), seq);
        SetEvent(ch.HapticEvent);
    }

    public static void Throw()
    {
        var t = typeof(HMVRController);
        var vr = (HMVRController)RuntimeHelpers.GetUninitializedObject(t);
        var ch = new VrSharedMemory.Channel();
        ch.Mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, (uint)VrSharedMemory.SectionSize, IntPtr.Zero);
        ch.View = MapViewOfFile(ch.Mapping, FILE_MAP_ALL_ACCESS, 0, 0, (nuint)VrSharedMemory.SectionSize);
        ch.HapticEvent = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        if (ch.Mapping == IntPtr.Zero || ch.View == IntPtr.Zero || ch.HapticEvent == IntPtr.Zero)
        {
            Checks.Check("an unnamed section and event for the channel", false, $"Win32 error {Marshal.GetLastPInvokeError()}");
            return;
        }
        var channel = t.GetField("_channel", Private)!;
        channel.SetValue(vr, ch);
        t.GetField("_cts", Private)!.SetValue(vr, new CancellationTokenSource());
        t.GetField("_lifetimeLock", Private)!.SetValue(vr, new object());
        t.GetField("_frame", Private)!.SetValue(vr, new byte[VrSharedMemory.InputFrameSize]);
        int got = 0;
        vr.HapticReceived += (_, _) => Interlocked.Increment(ref got);
        const string name = "HMVRController.HapticLoop";
        var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), vr, t.GetMethod("HapticLoop", Private)!);
        var reader = new Thread(loop) { IsBackground = true, Name = name };
        t.GetField("_hapticThread", Private)!.SetValue(vr, reader);
        reader.Start();
        try
        {
            Publish(ch, 1);
            Checks.Check("the reader raises a haptic the ring holds", WaitUntil(() => Volatile.Read(ref got) >= 1), $"{Volatile.Read(ref got)} haptic(s)");
            // Each pass now throws: the controller's channel is gone, which no
            // path of the SDK's leaves it.
            channel.SetValue(vr, null);
            SetEvent(ch.HapticEvent);
            bool second = WaitUntil(() => Diag.Find(name, "(failure 2)") != null || !reader.IsAlive);
            Checks.Check("the reader runs another pass after one that threw", second && reader.IsAlive,
                $"the reader {(reader.IsAlive ? "runs" : "ended")}");
            bool bounded = CheckBounded("a haptic reader whose every pass throws", Measure());
            bool logged = Diag.Failures(name, "a pass threw System.NullReferenceException", wait: false);
            channel.SetValue(vr, ch);
            Publish(ch, 2);
            Checks.Check("once its passes stop throwing, the reader raises the next haptic", WaitUntil(() => Volatile.Read(ref got) >= 2),
                $"{Volatile.Read(ref got)} haptic(s)");
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged && bounded);
        }
        finally
        {
            channel.SetValue(vr, ch);
            // Dispose ends the reader, which closes the channel's section
            // and event as it ends.
            vr.Dispose();
            Checks.Check("Dispose ends the reader", reader.Join(HangMs));
        }
    }
}

/// <summary>The output reader (HMController.OutputPollLoop), which drains
/// the output ring and raises OutputReceived. A drain that threw was retried
/// 8 ms later, and said nothing. The controller runs offline on process
/// memory, as protocol_engine_check's Host builds one: its reader polls the
/// ring every 8 ms, since no driver made its event.</summary>
internal static class Output
{
    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, PrivateStatic)!.GetValue(null)!;

    static void Write(IntPtr ring, uint seq, byte id)
    {
        int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)((seq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, (byte)HMOutputSource.HidOutput);
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, id);
        Marshal.WriteInt16(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, 1);
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, 0x5A);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)seq);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(ring, 0, (int)seq);
    }

    public static void Report()
    {
        const int index = 7965;
        var p = Profile(Catalog("xbox-360-wired"));
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        IntPtr ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
        Cache("s_inputViews")[index] = view;
        Cache("s_outputViews")[index] = ring;
        SharedMemoryIO.MarkOutputStart(index);
        var ctor = typeof(HMController).GetConstructor(Private, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
        }) ?? throw new MissingMethodException("HMController's private constructor changed.");
        var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        var c = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
        int received = 0;
        c.OutputReceived += (_, _) => Interlocked.Increment(ref received);
        var ports = typeof(HMController).GetField("<Ports>k__BackingField", Private)!;
        object? kept = ports.GetValue(c);
        string name = $"HMOutputReader_{index}";
        try
        {
            // The drain throws after it raises OutputReceived: the
            // controller's ports are gone, which no path of the SDK's leaves
            // them. The second packet is drained by the pass after the one
            // that threw.
            ports.SetValue(c, null);
            Write(ring, 1, 0x01);
            Write(ring, 2, 0x02);
            bool both = WaitUntil(() => Volatile.Read(ref received) >= 2);
            Checks.Check("the reader drains the next packet after a drain that threw", both, $"{Volatile.Read(ref received)} packet(s)");
            // The first line went out before the pass that raised the
            // second packet. The second goes out after that raise.
            bool logged = Diag.Logged("the failure reaches the diagnostic log under the reader's thread name", name,
                "a pass threw System.NullReferenceException", wait: false, also: "(failure 1)");
            Diag.Logged("the second failure is logged too", name, "(failure 2)", logged);
            ports.SetValue(c, kept);
            Write(ring, 3, 0x03);
            Checks.Check("once the drain stops throwing, the reader raises the next packet", WaitUntil(() => Volatile.Read(ref received) >= 3),
                $"{Volatile.Read(ref received)} packet(s)");
            Diag.Logged("the end of the failures reaches the diagnostic log", name, EndText, logged);
        }
        finally
        {
            ports.SetValue(c, kept);
            // The reader and the idle repeat stop as Dispose stops them,
            // without the context's bookkeeping, which an offline controller
            // has none of, and before the memory under them goes.
            var reader = (Thread?)typeof(HMController).GetField("_outputThread", Private)!.GetValue(c);
            c.StopThreads();
            Checks.Check("the reader ends once stopped", reader?.Join(HangMs) ?? false);
            Cache("s_inputViews").Remove(index);
            Cache("s_outputViews").Remove(index);
            Marshal.FreeHGlobal(view);
            Marshal.FreeHGlobal(ring);
        }
    }
}
