using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>Issue #72: a profile whose extendedReport.send is "clock"
/// reports on its own clock. The catalog's steam-deck-composite runs as the
/// SDK's controller offline, on an input section of process memory. Its
/// clock is stopped once the constructor has run, and the check calls the
/// clock's tick itself, so every count below is exact and no check depends
/// on how the threads are scheduled. A copy of the profile without the key
/// is the control: there a submit writes at once.</summary>
internal static class SendClock
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal sealed class Offline : IDisposable
    {
        public readonly HMController C;
        public readonly IntPtr View;
        public readonly Action Tick;
        readonly int _index;

        public Offline(string json, int index)
        {
            _index = index;
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json))!;
            p.ValidateDeviceModel();
            View = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, View, SharedMemoryIO.SHARED_INPUT_SIZE);
            Cache("s_inputViews")[index] = View;
            Cache("s_outputViews")[index] = IntPtr.Zero;
            var ctor = typeof(HMController).GetConstructor(Instance, new[]
            {
                typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
            }) ?? throw new MissingMethodException("HMController's private constructor changed.");
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            C = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
            Tick = typeof(HMController).GetMethod("RepeatLastFrame", Instance)!.CreateDelegate<Action>(C);
        }

        public IDeviceClock? Clock => (IDeviceClock?)typeof(HMController).GetField("_idleClock", Instance)!.GetValue(C);

        public uint Seq => (uint)Marshal.ReadInt32(View, 0);

        public byte[] Ext()
        {
            int len = Marshal.ReadInt32(View, SharedMemoryIO.EXTENDED_SIZE_OFFSET);
            var e = new byte[Math.Clamp(len, 0, SharedMemoryIO.EXTENDED_DATA_CAPACITY)];
            Marshal.Copy(View + SharedMemoryIO.EXTENDED_DATA_OFFSET, e, 0, e.Length);
            return e;
        }

        public void Dispose()
        {
            Clock?.Stop(1000);
            // With no output section the reader ends at once and disposes
            // its cancellation source.
            try { ((CancellationTokenSource)typeof(HMController).GetField("_outputCts", Instance)!.GetValue(C)!).Cancel(); }
            catch (ObjectDisposedException) { }
            ((Thread?)typeof(HMController).GetField("_outputThread", Instance)!.GetValue(C))?.Join(5000);
            Cache("s_inputViews").Remove(_index);
            Cache("s_outputViews").Remove(_index);
            Marshal.FreeHGlobal(View);
        }
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;

    public static void Run()
    {
        Console.WriteLine("-- a profile that reports on its own clock (extendedReport.send \"clock\")");
        string json = Device.Catalog("steam-deck-composite");
        var spec = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json))!.ExtendedReport!;
        int counterAt = spec.Fields.First(f => f.Semantic == "packetNum").Byte!.Value;
        int stickAt = spec.Fields.First(f => f.Semantic == "leftStickX").Byte!.Value;
        uint Counter(byte[] e) => BitConverter.ToUInt32(e, counterAt);
        short Stick(byte[] e) => BitConverter.ToInt16(e, stickAt);
        Check("the catalog's steam-deck-composite sends on its clock, every 4 ms", spec.Send == "clock" && spec.IdleFrameIntervalMs == 4);

        // The first report at create, on a copy whose period is a minute: the
        // clock's first tick is a minute after the constructor's write, and
        // the check reads the section well inside that minute, so only the
        // constructor can have written it. Each write moves the section's
        // sequence number by 2.
        var slow = JsonNode.Parse(json)!.AsObject();
        slow["extendedReport"]!["idleFrameIntervalMs"] = 60_000;
        var made = System.Diagnostics.Stopwatch.StartNew();
        using (var d = new Offline(slow.ToJsonString(), 7953))
        {
            uint atCreate = d.Seq;
            int extLength = d.Ext().Length;
            bool inside = made.ElapsedMilliseconds < 60_000;
            d.Clock?.Stop(1000);
            Check("the first report goes out at create, before the clock's first tick",
                inside && atCreate == 2 && extLength == spec.Size, $"seq {atCreate}, {extLength} bytes, read {made.ElapsedMilliseconds} ms after the create began");
        }

        using (var d = new Offline(json, 7950))
        {
            var clock = d.Clock;
            Check("its stream runs on a grid tick, which no frame moves",
                clock is DeviceClock<StreamGridTick>, clock?.GetType().Name ?? "no clock");
            clock?.Stop(1000);
            Check("the clock stops when asked", clock != null && !clock.Thread.IsAlive);
            uint created = d.Seq;

            var axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0f, [HMAxis.Y] = 0.5f, [HMAxis.Rx] = 0.5f, [HMAxis.Ry] = 0.5f };
            d.C.SubmitState(new HMGamepadState { Axes = axes });
            Check("SubmitState sends nothing", d.Seq == created, $"seq {created} then {d.Seq}");
            // The consumer reuses its dictionary before the clock's tick.
            axes[HMAxis.X] = 1f;
            d.Tick();
            uint ticked = d.Seq;
            var first = d.Ext();
            Check("a tick right after a submit sends it: no tick waits on a recent submit", ticked != created);
            Check("the tick carries the state as submitted, not the dictionary the consumer changed after",
                Stick(first) <= -32000, $"leftStickX {Stick(first)}");
            d.Tick();
            var second = d.Ext();
            Check("each tick is one report, its counter one on", d.Seq != ticked && Counter(second) == Counter(first) + 1,
                $"counter {Counter(first)} then {Counter(second)}");

            var raw = new byte[spec.Size];
            raw[1] = 0x01; raw[3] = 0x09; raw[4] = 0x40;
            raw[^4] = 0xA5; raw[^3] = 0x5A; raw[^2] = 0xC3; raw[^1] = 0x3C;
            uint beforeRaw = d.Seq;
            d.C.SubmitRawExtendedReport(raw);
            Check("SubmitRawExtendedReport sends nothing", d.Seq == beforeRaw);
            d.Tick();
            Check("the tick sends the raw frame byte for byte", d.Ext().AsSpan().SequenceEqual(raw), Checks.Hex(d.Ext()));
            d.Tick();
            Check("the next tick repeats it until the next submit", d.Ext().AsSpan().SequenceEqual(raw));
            // A state after raw frames: the next tick sends the state, not
            // the raw frame.
            uint beforeState = d.Seq;
            d.C.SubmitState(new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0f } });
            Check("SubmitState after raw frames sends nothing", d.Seq == beforeState);
            d.Tick();
            var afterRaw = d.Ext();
            Check("the tick after it sends the state, not the raw frame",
                !afterRaw.AsSpan().SequenceEqual(raw) && Stick(afterRaw) <= -32000 && Counter(afterRaw) != Counter(raw),
                $"leftStickX {Stick(afterRaw)}, counter {Counter(afterRaw)}");

            var a = new HMGamepadState { Buttons = HMButton.A, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.25f } };
            var b = new HMGamepadState { Buttons = HMButton.B, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.75f } };
            for (int i = 0; i < 2000; i++) { d.C.SubmitState(i % 2 == 0 ? a : b); d.Tick(); }
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) d.C.SubmitState(i % 2 == 0 ? a : b);
            long submitBytes = GC.GetAllocatedBytesForCurrentThread() - b0;
            b0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) d.Tick();
            long tickBytes = GC.GetAllocatedBytesForCurrentThread() - b0;
            Check("10,000 SubmitState calls allocate nothing", submitBytes == 0, $"{submitBytes} bytes");
            Check("10,000 ticks allocate nothing", tickBytes == 0, $"{tickBytes} bytes");
        }

        // The control: the same profile without the key sends each submit at
        // once, as every profile without it does.
        var o = JsonNode.Parse(json)!.AsObject();
        o["extendedReport"]!.AsObject().Remove("send");
        using (var d = new Offline(o.ToJsonString(), 7951))
        {
            var clock = d.Clock;
            Check("without the key the stream is the idle repeat", clock is DeviceClock<StreamTick>, clock?.GetType().Name ?? "no clock");
            clock?.Stop(1000);
            uint before = d.Seq;
            d.C.SubmitState(new HMGamepadState { Buttons = HMButton.A });
            Check("without the key SubmitState sends at once", d.Seq != before, $"seq {before} then {d.Seq}");
        }

        Stamps();

        var tick = new StreamGridTick(1000, 123, () => { });
        Check("the grid tick runs from its origin and nothing moves it",
            tick.Running(out long origin) && origin == 123 && tick.NotBefore == 0 && tick.Period == 1000);
    }

    /// <summary>The catalog's steam-controller-2, whose IMU stamp is a
    /// "clock": "accumulate" field (byte 30, uint32-le, divisor 3), on a
    /// clock the check scripts: 4 ms a report. Its IMU register's hold is
    /// lifted, as SDL's IMU write lifts it on the device. While the
    /// consumer's stamps are 0 the field moves 4000 us a report, on the
    /// SDK's clock. Then stamped states 8 ms apart, half the tick rate, two
    /// ticks each: the first tick after a new stamp moves the field by the
    /// stamp's 8000 us advance, the second repeats it, and the field's
    /// whole movement is the consumer's time.</summary>
    static void Stamps()
    {
        string json = Device.Catalog("steam-controller-2");
        var spec = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json))!.ExtendedReport!;
        var field = spec.Fields.First(f => f.Semantic == "sensorTimestamp");
        Check("the catalog's steam-controller-2 accumulates its stamp and sends on its clock",
            field.Clock == "accumulate" && field.Byte == 30 && spec.Send == "clock");
        using var d = new Offline(json, 7952);
        d.Clock?.Stop(1000);
        var enc = (VendorBlobCodec.EncoderState)typeof(HMController).GetField("_extEncoderState", Instance)!.GetValue(d.C)!;
        enc.Hold = null;
        // The constructor's report started the accumulator on the Stopwatch,
        // so the scripted clock continues from the Stopwatch's now, in its
        // units, and moves 4 ms a tick.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long fourMs = System.Diagnostics.Stopwatch.Frequency * 4 / 1000;
        enc.TimestampClock = () => now;
        uint Stamp() => BitConverter.ToUInt32(d.Ext(), 30);
        void Tick() { now += fourMs; d.Tick(); }

        d.C.SubmitState(new HMGamepadState());
        Tick();
        var own = new List<long>();
        for (int i = 0; i < 6; i++) { uint s0 = Stamp(); Tick(); own.Add(Stamp() - (long)s0); }
        Check("on the SDK's clock (stamp 0) the stamp moves 4000 us every report",
            own.All(x => x == 4000), string.Join(" ", own));

        const uint Step = 24_000;   // 8 ms of 1/3 microsecond ticks
        uint consumer = 0x0100_0000;
        d.C.SubmitState(new HMGamepadState { SensorTimestamp = consumer });
        Tick();   // the switch to the consumer's stamps adds the clock's time once
        var firsts = new List<long>();
        var seconds = new List<long>();
        uint start = Stamp();
        const int Submits = 10;
        for (int k = 1; k <= Submits; k++)
        {
            d.C.SubmitState(new HMGamepadState { SensorTimestamp = consumer + (uint)k * Step });
            uint s0 = Stamp();
            Tick();
            uint s1 = Stamp();
            Tick();
            firsts.Add(s1 - (long)s0);
            seconds.Add(Stamp() - (long)s1);
        }
        long total = Stamp() - (long)start;
        Check("stamped submits at half the tick rate: the first tick after each new stamp moves the field by its 8000 us",
            firsts.All(x => x == 8000), string.Join(" ", firsts));
        Check("the second tick repeats the stamp already counted",
            seconds.All(x => x == 0), string.Join(" ", seconds));
        Check("the field's movement over the stamped reports is the consumer's time",
            total == Submits * 8000L, $"{total} us over {Submits} stamps 8000 us apart");
    }
}
