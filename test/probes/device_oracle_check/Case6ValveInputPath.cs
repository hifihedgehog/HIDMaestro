// Case 6: the whole USB/IP input path of the three Valve composites at
// 9bd6279, from SubmitState to the interrupt IN transfer a host reads, with
// the host's feature writes between frames.
//
// The controller is the SDK's own, built as case 1 builds it
// (Case1Submit.Create): its constructor runs against an input section this
// case owns, and its idle pump is off (idleFrameIntervalMs set to 0 on the
// process's catalog, as case 1 does). The case calls the idle pump's repeat,
// the controller's own RepeatLastFrame (HMController.cs:1611-1628), by
// reflection where the pump's 4 ms clock would call it. So that a submit remembers its
// state for the repeat (HMController.cs:715-722, which runs only while an
// idle thread exists), the controller holds a thread object the case never
// starts.
//
// The device is a UsbipEmulatedDevice built without its constructor, as
// case 4 builds it, with the fields its input path reads set as the
// constructor sets them (UsbipEmulatedDevice.cs:272-347). It reads the same
// section the controller writes. Its input pump does the per-frame work
// inline (InputPump, UsbipEmulatedDevice.cs:458-558): the seqlock read
// (TryReadInputFrame, 564-621), then the frame either completes a parked
// read or joins the frame queue. No method holds that second half, so the
// case runs the pump itself, on its thread, woken by the controller's own
// SetEvent (SharedMemoryIO.cs:908-911) on an unnamed event the case puts in
// SharedMemoryIO's per-index cache. The case waits for the pump to finish
// each frame before it does anything else, so no two frames are ever in
// flight and the bytes do not depend on thread timing. Interrupt IN reads
// and the feature writes go in through HandleSubmit and come back as
// RET_SUBMITs over a local socket pair, as in case 4.
//
// The feature writes are SDL's Triton writes, lizard mode off
// (SDL_hidapi_steam_triton.c:127-142) and SETTING_IMU_MODE on (0x18) and
// off (0) (653-677), sent to every profile as SDL sends them: a 64-byte
// buffer whose byte 0 is report ID 1. The 2015 controller also gets SDL's
// own driver's sensor writes (SDL_hidapi_steam.c:1316-1337, a 65-byte
// buffer with report ID 0). SDL's Deck driver writes nothing for sensors
// (SDL_hidapi_steamdeck.c:469-473). The controller's output reader, which
// would read these from the ring, is not run: it opens the named output
// event. Its only effect on the input path is the arming rule
// (HMController.cs:1876-1890), and all three profiles are alwaysArmed with
// no armOn list. The case records what the device publishes.
//
// Consumer stamps. A state whose SensorTimestamp is 0 gets a stamp, as in
// case 1, except in the streams named "-clock". Those submit every state
// with stamp 0. The codec reads its own clock only for a uint16-le field
// with semantic sensorTimestamp (VendorBlobCodec.cs:185-186,
// VendorBlobProgram.cs:191, 553). The case masks those bytes of each IN
// report to zero, from the profile's field list, and records separately
// whether they changed from the previous report. None of the three
// profiles declares such a field at 9bd6279, so the mask is empty and is
// recorded as such: the Triton's byte 30 is a uint32-rolling counter
// (steam-controller-2.json, VendorBlobCodec.cs:365-381), and no Valve field
// reads the consumer's stamp or the clock. A tree whose encoder reads a
// clock a test can set gets the scripted one case 1 gives it
// (Case1Submit.Create, ScriptedClock), so the bytes stamped from it are
// the same in every run.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal sealed class Case6ValveInputPath : IOracleCase
{
    public string Name => "case6-valve-input-path";

    /// <summary>The case's own stream of the run's generator.</summary>
    const ulong RngStream = 0x5641_4C56_4549_4E36; // "VALVEIN6"

    static readonly string[] ProfileIds = { "steam-controller-2", "steam-controller-composite", "steam-deck-composite" };

    /// <summary>Transport indexes of the case's controllers, above case 1's.
    /// Only SharedMemoryIO's in-process dictionaries see them.</summary>
    const int FirstIndex = 7800;

    const int RandomStates = 10_000;
    const int IdleRepeats = 50;
    const uint StampStep = 12000;   // 4 ms of 1/3 microsecond ticks, case 1's step

    static readonly HMAxis[] SixAxes = { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz };

    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    public void Record(GoldenCase golden, Options options)
    {
        var rng = new Rng(options.Seed, RngStream);
        var db = ProfileDatabase.LoadEmbedded();
        string sock = Path.Combine(Path.GetTempPath(), $"hmoracle6-{Environment.ProcessId}.sock");
        using var host = new Host(sock);
        int index = FirstIndex;
        foreach (var id in ProfileIds)
        {
            var p = db.All.FirstOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException($"No profile {id} in the catalog.");
            // No idle pump: the case repeats frames itself, on a controller
            // Case1Submit.Create builds without one.
            var mask = ClockBytes(p);
            string at = $"valve-input/{id}";

            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                Scripted(golden, at + "/scripted", r, stamped: true);
            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                Scripted(golden, at + "/scripted-clock", r, stamped: false);
            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                Idle(golden, at + "/idle-stamped", r, stamped: true);
            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                Idle(golden, at + "/idle-unstamped-clock", r, stamped: false);
            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                StampRestart(golden, at + "/stamp-restart", r);
            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                StampWrap(golden, at + "/stamp-wrap", r);
            using (var r = new Rig(golden, host, p, index++, mask, id == "steam-controller-composite"))
                Random(golden, at + "/random", r, rng);
        }
    }

    /// <summary>The bytes of the IN report the codec fills from its own
    /// clock when a state carries no stamp: each uint16-le field with
    /// semantic sensorTimestamp, two bytes at its offset.</summary>
    static int[] ClockBytes(ControllerProfile p)
    {
        var bytes = new List<int>();
        foreach (var f in p.ExtendedReport?.Fields ?? new List<FieldSpec>())
            if (f.Type == "uint16-le" && f.Semantic == "sensorTimestamp" && f.Byte is int b)
            {
                bytes.Add(b);
                bytes.Add(b + 1);
            }
        return bytes.ToArray();
    }

    // ── Sequences ────────────────────────────────────────────────────────

    /// <summary>States.Scripted over the six axes in three runs: IMU on,
    /// IMU off, IMU on again. Lizard mode off first, as SDL's Triton driver
    /// opens the device (SDL_hidapi_steam_triton.c:497).</summary>
    static void Scripted(GoldenCase g, string name, Rig r, bool stamped)
    {
        g.Stream(name);
        g.Explainer = new UsbipReportExplainer(r.Controller, 4);
        r.MaskHeader(clock: !stamped);
        r.LizardOff();
        var scratch = new Dictionary<HMAxis, float>();
        foreach (bool imu in new[] { true, false, true })
        {
            r.Imu(imu);
            foreach (var s in States.Scripted(SixAxes, scratch))
            {
                var st = s;
                if (!stamped) st.SensorTimestamp = 0;
                r.Submit(st, stamped);
            }
        }
    }

    /// <summary>The idle repeat after a submit: IMU on, one state, then the
    /// repeats the 4 ms clock would send, then IMU off and more repeats, then
    /// IMU on, a second state, and repeats again.</summary>
    static void Idle(GoldenCase g, string name, Rig r, bool stamped)
    {
        g.Stream(name);
        g.Explainer = new UsbipReportExplainer(r.Controller, 4);
        r.MaskHeader(clock: !stamped);
        r.LizardOff();
        r.Imu(true);
        var first = new HMGamepadState { Buttons = HMButton.A, AccelGY = 1f, GyroDpsX = 10f, SensorTimestamp = stamped ? 0x00012345u : 0u };
        r.Submit(first, stamped);
        for (int i = 0; i < IdleRepeats; i++) r.Repeat();
        r.Imu(false);
        for (int i = 0; i < IdleRepeats; i++) r.Repeat();
        r.Imu(true);
        var second = new HMGamepadState { Buttons = HMButton.B, AccelGZ = -1f, SensorTimestamp = stamped ? 0x00012345u + 99 * StampStep : 0u };
        r.Submit(second, stamped);
        for (int i = 0; i < IdleRepeats; i++) r.Repeat();
    }

    /// <summary>A consumer clock that restarts: large stamps, then small
    /// ones, with repeats between.</summary>
    static void StampRestart(GoldenCase g, string name, Rig r)
    {
        g.Stream(name);
        g.Explainer = new UsbipReportExplainer(r.Controller, 4);
        r.MaskHeader();
        r.Imu(true);
        for (uint k = 0; k < 20; k++) r.Submit(new HMGamepadState { GyroDpsY = k, SensorTimestamp = 0xF0000000u + k * StampStep }, stamped: true);
        r.Repeat();
        for (uint k = 0; k < 20; k++) r.Submit(new HMGamepadState { GyroDpsY = k, SensorTimestamp = 1 + k * StampStep }, stamped: true);
        r.Repeat();
    }

    /// <summary>A consumer clock that wraps at 2^32.</summary>
    static void StampWrap(GoldenCase g, string name, Rig r)
    {
        g.Stream(name);
        g.Explainer = new UsbipReportExplainer(r.Controller, 4);
        r.MaskHeader();
        r.Imu(true);
        uint t = 0xFFFFFFFFu - 10 * StampStep;
        for (int k = 0; k < 20; k++)
        {
            r.Submit(new HMGamepadState { AccelGX = k / 20f, SensorTimestamp = t }, stamped: true);
            if (k % 5 == 4) r.Repeat();
            t = unchecked(t + StampStep);
        }
    }

    /// <summary>10^4 random states with random IMU writes, idle repeats and
    /// parked reads between them.</summary>
    static void Random(GoldenCase g, string name, Rig r, Rng rng)
    {
        g.Stream(name);
        g.Explainer = new UsbipReportExplainer(r.Controller, 4);
        r.MaskHeader();
        r.LizardOff();
        var scratch = new Dictionary<HMAxis, float>();
        for (int n = 0; n < RandomStates; n++)
        {
            if (rng.Next(20) == 0) r.Imu(rng.Bool());
            if (rng.Next(8) == 0) r.Park();
            var s = States.Random(rng, SixAxes, scratch);
            r.Submit(s, stamped: true);
            int repeats = rng.Next(16) == 0 ? 1 + rng.Next(4) : 0;
            for (int k = 0; k < repeats; k++)
            {
                if (rng.Next(4) == 0) r.Park();
                r.Repeat();
            }
        }
    }

    // ── The rig: controller, device and host for one stream ──────────────

    sealed class Rig : IDisposable
    {
        readonly GoldenCase _g;
        readonly Host _host;
        readonly int _index;
        readonly int[] _mask;
        readonly bool _sdlSteamSensors;
        readonly IntPtr _view, _ring, _event;
        readonly HMController _c;
        readonly UsbipEmulatedDevice _dev;
        readonly Thread _pump;
        readonly object _hidLock;
        readonly System.Collections.ICollection _frameQueue;
        readonly MethodInfo _repeat;
        readonly byte _hidInterface, _inEndpoint;
        readonly int _inLength;
        uint _parkedSeq;
        uint _stamp;
        uint _published;
        byte[]? _lastReport;
        bool _clock;

        /// <summary>The controller, for --explain.</summary>
        public HMController Controller => _c;

        public Rig(GoldenCase g, Host host, ControllerProfile p, int index, int[] mask, bool sdlSteamSensors)
        {
            _g = g;
            _host = host;
            _index = index;
            _mask = mask;
            _sdlSteamSensors = sdlSteamSensors;
            _view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, _view, SharedMemoryIO.SHARED_INPUT_SIZE);
            _ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, _ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
            // The controller's SetEvent handle and the pump's wait handle:
            // one unnamed auto-reset event, as the named one is for both.
            _event = CreateEventW(IntPtr.Zero, false, false, null);
            if (_event == IntPtr.Zero) throw new InvalidOperationException("CreateEventW failed.");
            InputEvents()[index] = _event;

            _c = Case1Submit.Create(p, index, _view);
            // Remember each submit for the repeat, as a controller with an
            // idle pump does (HMController.cs:715-722). Never started.
            typeof(HMController).GetField("_idleThread", Instance)!.SetValue(_c, new Thread(() => { }) { Name = "case 6 idle pump, never started" });
            _repeat = typeof(HMController).GetMethod("RepeatLastFrame", Instance)
                ?? throw new MissingMethodException("HMController.RepeatLastFrame is gone: update case 6.");

            _dev = NewDevice(p, _view, _ring, _event, host.Connection, out _inEndpoint, out _inLength);
            // v2.0.0: a create's controller reads its device's protocol
            // registers through its USB/IP handle, for a sensor timestamp
            // that holds while one says so (the Triton's, while SDL has its
            // IMU off). This controller has no handle, so the case hands the
            // codec the registers as the constructor would.
            if (_dev.Protocol != null
                && typeof(HMController).GetField("_extEncoderState", Instance)!.GetValue(_c) is VendorBlobCodec.EncoderState enc)
                enc.HoldRegisters = _dev.Protocol.RegisterWords;
            _hidInterface = _dev.Descriptors.HidInterfaceNumber;
            var t = typeof(UsbipEmulatedDevice);
            _hidLock = t.GetField("_hidLock", Instance)!.GetValue(_dev)!;
            _frameQueue = (System.Collections.ICollection)t.GetField("_frameQueue", Instance)!.GetValue(_dev)!;
            var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), _dev, t.GetMethod("InputPumpLoop", Instance)!);
            _pump = new Thread(loop) { IsBackground = true, Name = "case 6 input pump" };
            t.GetField("_inputThread", Instance)!.SetValue(_dev, _pump);
            _pump.Start();
        }

        /// <summary>The fields the input and control paths read, set as the
        /// constructor sets them (UsbipEmulatedDevice.cs:272-347). Transport
        /// index 0 for the descriptors and the device. No feature source for
        /// Steam's firmware: no GET runs here.</summary>
        static UsbipEmulatedDevice NewDevice(ControllerProfile p, IntPtr view, IntPtr ring, IntPtr ev,
                                             UsbipServer.Connection connection, out byte inEndpoint, out int inLength)
        {
            var t = typeof(UsbipEmulatedDevice);
            var dev = (UsbipEmulatedDevice)RuntimeHelpers.GetUninitializedObject(t);
            void Set(string field, object? value) =>
                (t.GetField(field, Instance | BindingFlags.Public) ?? throw new MissingFieldException(t.Name, field)).SetValue(dev, value);
            var descriptors = new UsbDescriptorSet(p, 0);
            OracleProtocol.Set(dev, p, descriptors, 0);
            Set("<Descriptors>k__BackingField", descriptors);
            Set("_index", 0);
            Set("_stubs", FeatureStubTable.From(p, 0, descriptors.SerialString, _ => null));
            Set("_lastFeatureParam", -1);
            Set("_lastFeaturePayload", Array.Empty<byte>());
            inEndpoint = 0;
            inLength = 64;
            foreach (var kv in descriptors.Endpoints)
            {
                var e = kv.Value;
                if (e.TransferType == 3 && e.IsIn && e.InterfaceNumber == descriptors.HidInterfaceNumber)
                {
                    inEndpoint = e.Address;
                    inLength = e.MaxPacketSize;
                    break;
                }
            }
            Set("_primaryInEndpoint", inEndpoint);
            var builder = p.GetOrBuildReportBuilder();
            Set("_builder", builder);
            int expectedSize = builder.InputReportByteSize > 0 ? builder.InputReportByteSize : 17;
            int capacity = Math.Max(expectedSize, SharedMemoryIO.EXTENDED_DATA_CAPACITY);
            Set("_reportCapacity", capacity);
            Set("_pumpReport", new byte[capacity]);
            var declared = builder.InputReports.Count > 0
                ? builder.InputReports
                : new[] { (Id: builder.InputReportId, ByteSize: builder.InputReportByteSize) };
            var slots = new int[256];
            Array.Fill(slots, -1);
            var reports = new byte[declared.Count][];
            var lengths = new int[declared.Count];
            for (int i = 0; i < declared.Count; i++)
            {
                reports[i] = new byte[capacity];
                reports[i][0] = declared[i].Id;
                lengths[i] = Math.Clamp(declared[i].ByteSize, 1, capacity);
                slots[declared[i].Id] = i;
            }
            Set("_inputReportIds", declared[0].Id != 0);
            Set("_inputSlot", slots);
            Set("_inputReports", reports);
            Set("_inputReportLength", lengths);
            Set("_inputView", view);
            Set("_outputView", ring);
            Set("_inputWaitEvent", ev);
            Set("_lifetimeLock", new object());
            Set("_publishLock", new object());
            Set("_hidLock", new object());
            Set("_frameQueue", Activator.CreateInstance(t.GetField("_frameQueue", Instance)!.FieldType));
            Set("_spareFrames", new Stack<byte[]>());
            Set("_pendingInterruptIn", new Dictionary<byte, Queue<uint>>());
            Set("_readsPerEp", new Dictionary<byte, long>());
            Set("_completionsPerEp", new Dictionary<byte, long>());
            Set("_connection", connection);
            return dev;
        }

        static Dictionary<int, IntPtr> InputEvents() =>
            (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField("s_inputEvents", Static)!.GetValue(null)!;

        /// <summary>The bytes masked as the codec's clock, first in each
        /// stream. In a "-clock" stream every IN report is followed by
        /// whether those bytes changed from the previous report, 0 when
        /// there are none.</summary>
        public void MaskHeader(bool clock = false)
        {
            _clock = clock;
            _g.Out("clock-mask", _mask.Select(b => (byte)b).ToArray());
        }

        public void Submit(HMGamepadState s, bool stamped)
        {
            if (stamped && s.SensorTimestamp == 0)
            {
                _stamp += StampStep;
                if (_stamp == 0) _stamp = StampStep;
                s.SensorTimestamp = _stamp;
            }
            _g.SetInput(in s);
            uint before = Seq();
            try { _c.SubmitState(in s); }
            catch (Exception e)
            {
                _g.Out("throw", System.Text.Encoding.ASCII.GetBytes(e.GetType().FullName ?? "?"));
            }
            Deliver(before, "in");
        }

        /// <summary>One tick of the idle clock: the controller's own repeat
        /// of its last frame.</summary>
        public void Repeat()
        {
            uint before = Seq();
            try { _repeat.Invoke(_c, null); }
            catch (TargetInvocationException e)
            {
                _g.Out("throw", System.Text.Encoding.ASCII.GetBytes(e.InnerException?.GetType().FullName ?? "?"));
            }
            Deliver(before, "in.repeat");
        }

        /// <summary>An interrupt IN read that arrives before the next frame,
        /// which the device parks (UsbipEmulatedDevice.cs:722-736).</summary>
        public void Park()
        {
            if (_parkedSeq != 0) return;
            _parkedSeq = _host.SubmitIn(_dev, _inEndpoint, _inLength);
            _g.Out("parked", ReadOnlySpan<byte>.Empty);
        }

        /// <summary>The frame the last write made, as the host reads it:
        /// the parked read the pump completes, or a read of the queued frame
        /// once the pump has queued it.</summary>
        void Deliver(uint before, string tag)
        {
            if (Seq() == before)
            {
                _g.Out("no-frame", ReadOnlySpan<byte>.Empty);
                return;
            }
            (int Status, byte[] Data) reply;
            if (_parkedSeq != 0)
            {
                reply = _host.Receive(_parkedSeq, isIn: true);
                _parkedSeq = 0;
                tag += ".parked";
            }
            else
            {
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    lock (_hidLock) if (_frameQueue.Count > 0) break;
                    if (sw.ElapsedMilliseconds > 5000)
                        throw new TimeoutException("The device's input pump did not take the frame within 5 s.");
                    Thread.Yield();
                }
                reply = _host.Receive(_host.SubmitIn(_dev, _inEndpoint, _inLength), isIn: true);
            }
            var o = new byte[4 + reply.Data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(o, reply.Status);
            reply.Data.CopyTo(o, 4);
            bool advanced = false;
            foreach (int b in _mask)
            {
                if (b >= reply.Data.Length) continue;
                if (_lastReport != null && (b >= _lastReport.Length || _lastReport[b] != reply.Data[b])) advanced = true;
                o[4 + b] = 0;
            }
            _lastReport = reply.Data;
            _g.Out(tag, o);
            if (_clock) _g.Out("clock-advanced", new[] { (byte)(advanced ? 1 : 0) });
        }

        public void LizardOff() => TritonSetting(9, 0);   // SETTING_LIZARD_MODE, LIZARD_MODE_OFF

        /// <summary>SDL's sensor writes: the Triton driver's, and on the 2015
        /// controller its own driver's.</summary>
        public void Imu(bool on)
        {
            ushort value = (ushort)(on ? 0x18 : 0);   // SEND_RAW_ACCEL | SEND_RAW_GYRO, or OFF
            TritonSetting(48, value);                 // SETTING_IMU_MODE
            if (_sdlSteamSensors)
            {
                // SDL_hidapi_steam.c:1316-1337: buf[0] report ID 0, buf[1]
                // the message, buf[2] the length, one setting of three bytes.
                var b = new byte[64];
                b[0] = 0x87;
                b[1] = 3;
                b[2] = 48;
                b[3] = (byte)value;
                b[4] = (byte)(value >> 8);
                Set(0, b);
            }
        }

        /// <summary>SDL_hidapi_steam_triton.c:127-142 and 653-677: a 64-byte
        /// buffer, report ID 1, ID_SET_SETTINGS_VALUES, length 3, one
        /// setting (controller_structs.h:36-40).</summary>
        void TritonSetting(byte setting, ushort value)
        {
            var b = new byte[64];
            b[0] = 1;
            b[1] = 0x87;
            b[2] = 3;
            b[3] = setting;
            b[4] = (byte)value;
            b[5] = (byte)(value >> 8);
            Set(1, b);
        }

        void Set(byte reportId, byte[] payload)
        {
            if (_parkedSeq != 0) throw new InvalidOperationException("A read is parked: the RET_SUBMITs would cross.");
            var (status, _) = _host.Control(_dev, 0x21, 0x09, (ushort)(0x0300 | reportId), _hidInterface, payload);
            var o = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(o, status);
            payload.CopyTo(o, 4);
            _g.Out($"set.{reportId:X2}", o);
            Published();
        }

        /// <summary>Each ring slot the device published since the last
        /// write: source, report ID, size and data (SharedMemoryIO.cs:81-89).</summary>
        void Published()
        {
            uint head = (uint)Marshal.ReadInt32(_ring);
            for (; _published != head; _published++)
            {
                int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)(_published % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
                int size = Marshal.ReadInt16(_ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE);
                var o = new byte[4 + size];
                o[0] = Marshal.ReadByte(_ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE);
                o[1] = Marshal.ReadByte(_ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID);
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(2), (ushort)size);
                Marshal.Copy(_ring + slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, o, 4, size);
                _g.Out("pub", o);
            }
        }

        uint Seq() => (uint)Marshal.ReadInt32(_view);

        /// <summary>Stop the pump through Dispose, as the offline harness
        /// does, which closes the event as the pump ends
        /// (UsbipEmulatedDevice.cs:445-456), then drop the case's cache
        /// entries and memory.</summary>
        public void Dispose()
        {
            if (_parkedSeq != 0) throw new InvalidOperationException("A stream ended with a read parked.");
            _dev.Dispose();
            if (!_pump.Join(5000)) throw new TimeoutException("The device's input pump did not end within 5 s.");
            InputEvents().Remove(_index);
            Case1Submit.Forget(_index);
            Marshal.FreeHGlobal(_view);
            Marshal.FreeHGlobal(_ring);
        }
    }

    /// <summary>The host side, as in case 4: a connected pair of local
    /// sockets, the device's end wrapped in a connection made without its
    /// constructor (UsbipServer.cs:270-291) holding the socket and the send
    /// lock (575-593, 632-644). Here a submit and its RET_SUBMIT can be
    /// apart, for a read the device parks.</summary>
    sealed class Host : IDisposable
    {
        readonly string _path;
        readonly Socket _listener, _client, _server;
        readonly byte[] _header = new byte[UsbipProtocol.HeaderSize];
        uint _seq;

        public readonly UsbipServer.Connection Connection;

        public Host(string path)
        {
            _path = path;
            if (File.Exists(path)) File.Delete(path);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            _listener.Listen(1);
            _client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _client.Connect(new UnixDomainSocketEndPoint(path));
            _server = _listener.Accept();
            // A transfer the device never answers fails the run instead of
            // hanging it.
            _client.ReceiveTimeout = 5000;
            var t = typeof(UsbipServer.Connection);
            Connection = (UsbipServer.Connection)RuntimeHelpers.GetUninitializedObject(t);
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            (t.GetField("_socket", f) ?? throw new MissingFieldException(t.Name, "_socket")).SetValue(Connection, _server);
            (t.GetField("_sendLock", f) ?? throw new MissingFieldException(t.Name, "_sendLock")).SetValue(Connection, new object());
        }

        /// <summary>An interrupt IN CMD_SUBMIT. Returns its seqnum.</summary>
        public uint SubmitIn(UsbipEmulatedDevice dev, byte endpoint, int length)
        {
            var cmd = new UsbipProtocol.CommandHeader
            {
                Command = UsbipProtocol.CmdSubmit,
                Seqnum = ++_seq,
                Direction = 1,
                Ep = (uint)(endpoint & 0x0F),
                TransferBufferLength = length,
                NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
            };
            dev.HandleSubmit(cmd, ReadOnlySpan<byte>.Empty, ReadOnlySpan<(uint, uint)>.Empty);
            return _seq;
        }

        /// <summary>A control transfer on endpoint 0 and its RET_SUBMIT.</summary>
        public (int Status, byte[] Data) Control(UsbipEmulatedDevice dev, byte bmRequestType, byte bRequest,
                                                 ushort wValue, ushort wIndex, ReadOnlySpan<byte> outPayload)
        {
            bool isIn = (bmRequestType & 0x80) != 0;
            ushort wLength = (ushort)outPayload.Length;
            var cmd = new UsbipProtocol.CommandHeader
            {
                Command = UsbipProtocol.CmdSubmit,
                Seqnum = ++_seq,
                Direction = isIn ? 1u : 0u,
                Ep = 0,
                TransferBufferLength = wLength,
                NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
                Setup = bmRequestType | ((ulong)bRequest << 8) | ((ulong)wValue << 16)
                      | ((ulong)wIndex << 32) | ((ulong)wLength << 48),
            };
            dev.HandleSubmit(cmd, outPayload, ReadOnlySpan<(uint, uint)>.Empty);
            return Receive(_seq, isIn);
        }

        /// <summary>The next RET_SUBMIT, which must answer
        /// <paramref name="seq"/>.</summary>
        public (int Status, byte[] Data) Receive(uint seq, bool isIn)
        {
            ReadExactly(_header);
            uint kind = BinaryPrimitives.ReadUInt32BigEndian(_header);
            uint got = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
            if (kind != UsbipProtocol.RetSubmit || got != seq)
                throw new InvalidDataException($"RET_SUBMIT {seq} expected, got command {kind} for {got}.");
            int status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24));
            var data = new byte[isIn ? actual : 0];
            ReadExactly(data);
            return (status, data);
        }

        void ReadExactly(Span<byte> dst)
        {
            for (int got = 0; got < dst.Length;)
            {
                int n = _client.Receive(dst[got..], SocketFlags.None);
                if (n <= 0) throw new EndOfStreamException("The device's end of the connection closed.");
                got += n;
            }
        }

        public void Dispose()
        {
            _server.Dispose();
            _client.Dispose();
            _listener.Dispose();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);
}
