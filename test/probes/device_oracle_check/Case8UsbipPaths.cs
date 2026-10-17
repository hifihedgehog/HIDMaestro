// Case 8: the USB/IP paths of 9bd6279 that cases 3, 4 and 6 do not reach.
//
// The Switch 2 Pro composite as a program runs it: the SDK's HMController
// built with a USB/IP handle, which turns on its Switch 2 gate and body
// plumbing (HMController.cs:579-582, 772-782), its raw-submit refusal
// (1966-1975) and its rumble decode (1827-1854), over a UsbipEmulatedDevice
// whose Switch 2 transport branches answer the host (UsbipEmulatedDevice.cs
// 172-174, 293-304, 765-1000, 1241-1264). And the raw submit paths
// (HMController.cs:1119-1153, 1206-1216, 1231-1253, 1465-1502) on the
// controller built as case 1 builds it, for the Switch 2 composite, a Valve
// composite, an XUSB profile and a Sony Bluetooth profile.
//
// The device is built without its constructor, as cases 4 and 6 build it,
// with the fields its paths read set as the constructor sets them
// (UsbipEmulatedDevice.cs:272-347), and the handle is the backend's own
// type around it with no server (UsbipBackend.cs:455-472), which the
// controller only reads the device from. The device's own input pump runs
// on its thread, woken by the controller's SetEvent as in case 6, and the
// case waits for it to take each frame before going on. Two things run
// where a thread would run them, so every byte comes in a fixed order:
// - the report clock's tick: SendSwitch2Report (863-883) with the run's
//   start time from ReportsOnSince, as Switch2Stream passes it (958-968),
// - the host: CMD_SUBMITs through HandleSubmit and HandleUnlink, each
//   RET_SUBMIT and RET_UNLINK read back over a local socket pair as in
//   case 4. The device sends only from the thread that called it here, so
//   once a call returns, what it sent is all there is.
//
// Two parts stay live only. The constructor's own gate, bulk endpoint pick,
// responder and report buffer (293-304, 326) open the controller's Global
// sections and events first (331-347), so the case applies the same gate
// itself (057E:2069) when it sets the fields. The report clock's thread
// (Switch2StreamLoop and Switch2Stream, 904-991, started at 350-363) reads
// the Stopwatch and waits on a high-resolution timer, with no clock the case
// could replace: its first tick one interval after 03/0D, its grid and its
// skipped ticks are battery S65 (wire capture, SDL with libusb, Steam), S43
// and S84.
// The device stores each bulk command's arrival time from the Stopwatch
// (790) and compares it only with itself, so the case records no time.
//
// The controller's output thread runs, as in case 7: it polls the ring
// every 8 ms, and the case waits for a sentinel slot it writes after each
// batch. The device publishes the host's interrupt OUT reports into the
// same ring (PublishOutput, 631-661), so the decode reads what the wire
// carried.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal sealed class Case8UsbipPaths : IOracleCase
{
    public string Name => "case8-usbip-paths";

    /// <summary>The case's own stream of the run's generator.</summary>
    const ulong RngStream = 0x5553_4250_4154_4838; // "USBPATH8"

    const string Switch2Id = "switch2-pro-controller-composite";
    static readonly string[] RawIds = { Switch2Id, "steam-controller-2", "xbox-360-wired", "dualsense-bt" };

    /// <summary>Transport indexes of the case's controllers, above cases 1,
    /// 6 and 7. Only SharedMemoryIO's in-process dictionaries see them.</summary>
    const int FirstIndex = 7900;

    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    public void Record(GoldenCase golden, Options options)
    {
        var rng = new Rng(options.Seed, RngStream);
        var db = ProfileDatabase.LoadEmbedded();
        ControllerProfile Get(string id) => db.All.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"No profile {id} in the catalog.");
        int index = FirstIndex;

        string sock = Path.Combine(Path.GetTempPath(), $"hmoracle8-{Environment.ProcessId}.sock");
        using (var host = new Host(sock))
        {
            var s2 = Get(Switch2Id);
            using (var r = new CompositeRig(golden, host, s2, index++)) SdlRun(golden, r, rng);
            using (var r = new CompositeRig(golden, host, s2, index++)) SteamRun(golden, r);
            using (var r = new CompositeRig(golden, host, s2, index++)) Transport(golden, r);
            using (var r = new CompositeRig(golden, host, s2, index++)) Rumble(golden, r, rng);
        }
        foreach (var id in RawIds) Raw(golden, Get(id), index++, rng);

        // Round 3. A generator of their own, so the streams above keep their
        // bytes.
        var rng3 = new Rng(options.Seed, RngStream3);
        using (var host = new Host(sock))
        {
            var s2 = Get(Switch2Id);
            using (var r = new CompositeRig(golden, host, s2, index++)) Axes(golden, r);
            foreach (var id in new[] { "dualsense-composite", "steam-controller-2" })
                using (var r = new CompositeRig(golden, host, Get(id), index++)) OtherComposite(golden, r, id);
            using (var r = new CompositeRig(golden, host, s2, index++)) Report02Sources(golden, r, rng3);
            using (var r = new CompositeRig(golden, host, s2, index++, claimed: false)) AttachDetach(golden, host, r);
        }
    }

    /// <summary>The generator of the round-3 streams.</summary>
    const ulong RngStream3 = 0x5553_4250_4154_4833; // "USBPATH3"

    // ── Round 3 ──────────────────────────────────────────────────────────

    /// <summary>States.Scripted over the six axes through the composite's
    /// own controller: its sticks (X, Y, Rx, Rz), its digital triggers
    /// (axisMap 0x32 and 0x34) and case 1's boundary floats, through
    /// ResolveSimpleSlots (HMController.cs:974-1014) into the body
    /// (772-782).</summary>
    static void Axes(GoldenCase g, CompositeRig r)
    {
        g.Stream($"usbip-path/{Switch2Id}/axes");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        var scratch = new Dictionary<HMAxis, float>();
        foreach (var s in States.Scripted(new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz }, scratch))
            r.Submit(s);
    }

    /// <summary>A composite of another vendor with a USB/IP handle, so a
    /// gate keyed on the handle alone shows: its frames from SubmitState
    /// as an interrupt IN read takes them, raw submits, which it takes, and
    /// GET_REPORT(Input) on report 0 and each declared input report, which
    /// answer from the last frame of each (UsbipEmulatedDevice.cs:1254-1272),
    /// before any frame and after.</summary>
    static void OtherComposite(GoldenCase g, CompositeRig r, string id)
    {
        g.Stream($"usbip-path/{id}/handle");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        var ids = new List<byte> { 0x00, 0x02, 0xFF };
        foreach (var rep in r.Controller.Profile.Inner.GetOrBuildReportBuilder().InputReports) if (!ids.Contains(rep.Id)) ids.Add(rep.Id);
        void Gets()
        {
            foreach (byte rid in ids)
                foreach (ushort w in new ushort[] { 0, 1, 10, 63, 64, 65, 80 }) r.GetInput(rid, w);
        }
        Gets();
        var scratch = new Dictionary<HMAxis, float>();
        foreach (var s in States.Scripted(new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz }, scratch))
            r.Submit(s);
        Gets();
        r.RawOnce();
        Gets();
    }

    /// <summary>Report 0x02 reaching the composite's controller from each
    /// source a host can send it on: SET_REPORT(Feature), which the device
    /// publishes as HidFeature, SET_REPORT(Output) and the interrupt OUT
    /// endpoint, both published as HidOutput (UsbipEmulatedDevice.cs:
    /// 1298-1327, 755-761). The decode reads HidOutput alone
    /// (HMController.cs:1827-1829).</summary>
    static void Report02Sources(GoldenCase g, CompositeRig r, Rng rng)
    {
        g.Stream($"usbip-path/{Switch2Id}/report-02-sources");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        var payloads = new List<byte[]>();
        foreach (int len in new[] { 0, 16, 31, 32, 33, 40, 63 })
        {
            var d = new byte[1 + len];
            d[0] = 0x02;
            for (int i = 1; i < d.Length; i++) d[i] = (byte)(i * 37);
            payloads.Add(d);
        }
        for (int i = 0; i < 20; i++)
        {
            var d = new byte[33];
            rng.Fill(d);
            d[0] = 0x02;
            payloads.Add(d);
        }
        foreach (var d in payloads)
        {
            r.SetReport(0x03, 0x02, d);   // Feature
            r.SetReport(0x02, 0x02, d);   // Output
            r.OutputBatch(new List<byte[]> { d });
        }
    }

    /// <summary>The connection's attach and detach (UsbipEmulatedDevice.cs:
    /// 382-440): a claim, the pad started and set up, a second claim
    /// refused, a port, a detach by a connection that does not hold the
    /// device, the holder's detach, which resets the persona and the port,
    /// and a claim again.</summary>
    static void AttachDetach(GoldenCase g, Host host, CompositeRig r)
    {
        g.Stream($"usbip-path/{Switch2Id}/attach-detach");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        var other = (UsbipServer.Connection)RuntimeHelpers.GetUninitializedObject(typeof(UsbipServer.Connection));
        r.Registers();
        r.Claim(host.Connection, "holder");
        r.Submit(new HMGamepadState { Buttons = HMButton.A, AccelGY = 1f });
        foreach (string c in new[] { "0391000D000800000100FFFFFFFFFFFF", "0391000A0004000005000000", "0C9100020004000027000000", "0C9100040004000027000000", "09910007000800000300000000000000" })
        {
            r.BulkOut(Convert.FromHexString(c));
            r.BulkIn(64);
        }
        r.Tick();
        r.InterruptIn();
        r.InterruptIn();   // parks
        r.BulkIn(64);      // parks
        r.Registers();
        r.Claim(other, "other");
        r.SetPort(host.Connection, 3);
        r.SetPort(other, 4);
        r.Registers();
        r.Detach(other, "other");
        r.Registers();
        r.Detach(host.Connection, "holder");
        r.Registers();
        r.Claim(host.Connection, "holder");
        r.Registers();
        foreach (byte id in new byte[] { 0x00, 0x05, 0x09 }) r.GetInput(id, 64);
        r.Detach(host.Connection, "holder");
        r.Claim(other, "other");
        r.Registers();
    }

    // ── The Switch 2 composite with its USB/IP handle ────────────────────

    /// <summary>SDL's start (case 3's sequence, SDL_hidapi_switch2.c:392-503)
    /// over the bulk pair, with the reads RecvBulkData makes (172-198), then
    /// the reports the clock sends to parked and late reads, with state
    /// submits between.</summary>
    static void SdlRun(GoldenCase g, CompositeRig r, Rng rng)
    {
        g.Stream($"usbip-path/{Switch2Id}/sdl-start");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        // The body plumbing first: each axis alone, each button alone, each
        // hat value, each motion field, so a body built from the wrong input
        // differs here.
        foreach (var axis in new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz })
            foreach (float v in new[] { 0f, 0.25f, 0.5f, 1f })
                r.Submit(new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [axis] = v } });
        for (int bit = 0; bit < 32; bit++) r.Submit(new HMGamepadState { Buttons = (HMButton)(1u << bit) });
        for (int h = 0; h <= 8; h++) r.Submit(new HMGamepadState { Hat = (HMHat)h });
        r.Submit(new HMGamepadState { AccelGX = 1f });
        r.Submit(new HMGamepadState { AccelGZ = 1f });
        r.Submit(new HMGamepadState { GyroDpsX = 100f });
        r.Submit(new HMGamepadState { GyroDpsY = 100f });
        r.Submit(new HMGamepadState { GyroDpsZ = 100f });
        r.Submit(new HMGamepadState { AccelGY = 1f });
        foreach (byte id in new byte[] { 0x00, 0x05, 0x09, 0x07, 0x01, 0x30 }) r.GetInput(id, 64);
        r.InterruptIn();   // parks: reports are off
        r.Tick();
        foreach (uint address in new uint[] { 0x13000, 0x13040, 0x13080, 0x130C0, 0x13100, 0x1FC040, 0x1FC080 })
        {
            var read = Convert.FromHexString("02910001000800000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(read.AsSpan(12), address);
            r.BulkOut(read);
            r.SdlReceive(0x50);
        }
        foreach (var cmd in Case3Switch2Device.SdlInitSequence)
        {
            r.BulkOut(cmd.AsSpan(0, cmd[5] + 8).ToArray());
            r.SdlReceive(0x40);
        }
        // Reports are on. The read parked before 03/0D takes the first tick.
        r.Tick();
        for (int i = 0; i < 40; i++)
        {
            int kind = rng.Next(4);
            if (kind == 0) { r.InterruptIn(); r.Tick(); }        // a read waits for the tick
            else if (kind == 1) { r.Tick(); r.InterruptIn(); }   // a tick leaves one owed, the read takes it
            else if (kind == 2) { r.Tick(); r.Tick(); r.InterruptIn(); }
            else r.Submit(RandomState(rng));
        }
        r.BulkOut(Convert.FromHexString("09910007000800000100000000000000"));
        r.SdlReceive(8);
        r.BulkOut(Convert.FromHexString("0C9100040004000023000000"));
        r.SdlReceive(12);
        for (int i = 0; i < 10; i++) { r.InterruptIn(); r.Tick(); }
        foreach (byte id in new byte[] { 0x00, 0x05, 0x09, 0x07, 0x01, 0x30 })
            foreach (ushort w in new ushort[] { 0, 10, 63, 64, 65 }) r.GetInput(id, w);
        r.Registers();
    }

    /// <summary>Steam's start as S65 recorded it (case 3's SteamS65), each
    /// command read back with 512 bytes, then reports.</summary>
    static void SteamRun(GoldenCase g, CompositeRig r)
    {
        g.Stream($"usbip-path/{Switch2Id}/steam-start");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        r.Submit(new HMGamepadState { AccelGY = 1f });
        foreach (var (_, hex) in Case3Switch2Device.SteamS65)
        {
            r.BulkOut(Convert.FromHexString(hex));
            r.BulkIn(512);
            for (int i = 0; i < 3; i++) { r.InterruptIn(); r.Tick(); }
        }
        r.Registers();
    }

    /// <summary>The transport's other branches: a bulk read parked before
    /// its command, two replies never sharing a read, a parked bulk read
    /// unlinked, a short read taking a reply in parts, a payload shorter
    /// than a header, a port reset with a read parked and a report owed,
    /// GET_REPORT(Input) before and after reports start, and raw submits on
    /// the controller, which refuse.</summary>
    static void Transport(GoldenCase g, CompositeRig r)
    {
        g.Stream($"usbip-path/{Switch2Id}/transport");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        r.Submit(new HMGamepadState { Buttons = HMButton.A });
        r.BulkIn(64);                                                        // parks
        r.BulkOut(Convert.FromHexString("0391000D000800000100FFFFFFFFFFFF")); // the parked read takes the reply
        r.BulkOut(Convert.FromHexString("0791000100000000"));
        r.BulkOut(Convert.FromHexString("0191000C00000000"));
        r.BulkIn(512);
        r.BulkIn(512);
        uint victim = r.BulkIn(64);                                          // parks, nothing waits
        r.Unlink(victim);
        r.Unlink(victim);
        r.BulkOut(Convert.FromHexString("1091000100000000"));
        for (int i = 0; i < 4; i++) r.BulkIn(8);
        r.BulkOut(Convert.FromHexString("079100"));
        r.BulkIn(64);
        r.Tick();
        r.InterruptIn();
        uint parked = r.InterruptIn();
        r.Unlink(parked);
        r.Tick();                                                            // owed: no read waits
        r.PortReset();
        r.InterruptIn();
        r.Tick();
        r.Registers();
        foreach (byte id in new byte[] { 0x00, 0x05, 0x09 }) r.GetInput(id, 64);
        r.BulkOut(Convert.FromHexString("0391000D000800000100FFFFFFFFFFFF"));
        r.BulkIn(64);
        r.Tick();
        r.InterruptIn();
        r.RawSubmits();
        r.Registers();
    }

    /// <summary>Output report 0x02 from the host on the interrupt OUT
    /// endpoint, at each length from empty to past the 32 bytes the decode
    /// reads (Switch2ProPacker.cs:160), patterned and random, and other
    /// report IDs, each batch read back from the controller's events.</summary>
    static void Rumble(GoldenCase g, CompositeRig r, Rng rng)
    {
        g.Stream($"usbip-path/{Switch2Id}/rumble");
        g.Explainer = new UsbipReportExplainer(r.Controller, 8);
        var reports = new List<byte[]>();
        for (int len = 0; len <= 40; len++)
        {
            var d = new byte[1 + len];
            d[0] = 0x02;
            for (int i = 1; i < d.Length; i++) d[i] = (byte)(i * 37);
            reports.Add(d);
        }
        for (int b = 1; b <= 32; b++)
            foreach (byte v in new byte[] { 0x01, 0x80, 0xFF })
            {
                var d = new byte[33];
                d[0] = 0x02;
                d[b] = v;
                reports.Add(d);
            }
        for (int i = 0; i < 200; i++)
        {
            var d = new byte[1 + (rng.Next(8) == 0 ? rng.Next(64) : 63)];
            rng.Fill(d);
            d[0] = rng.Next(10) == 0 ? rng.Byte() : (byte)0x02;
            reports.Add(d);
        }
        for (int i = 0; i < reports.Count; i += 16)
            r.OutputBatch(reports.Skip(i).Take(16).ToList());
    }

    static HMGamepadState RandomState(Rng rng) => new()
    {
        Buttons = (HMButton)rng.NextU32(),
        Hat = (HMHat)rng.Next(9),
        AccelGX = rng.Float01() * 4f - 2f,
        AccelGY = rng.Float01() * 4f - 2f,
        AccelGZ = rng.Float01() * 4f - 2f,
        GyroDpsX = rng.Float01() * 1000f - 500f,
        GyroDpsY = rng.Float01() * 1000f - 500f,
        GyroDpsZ = rng.Float01() * 1000f - 500f,
        Axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = rng.Float01(), [HMAxis.Y] = rng.Float01(),
            [HMAxis.Rx] = rng.Float01(), [HMAxis.Rz] = rng.Float01(),
            [HMAxis.Z] = rng.Float01(), [HMAxis.Ry] = rng.Float01(),
        },
    };

    /// <summary>A USB/IP composite as a program runs it: the SDK's
    /// controller with a USB/IP handle, over a device built as the
    /// constructor builds it, whose own input pump runs on its thread.</summary>
    sealed class CompositeRig : IDisposable
    {
        readonly GoldenCase _g;
        readonly Host _host;
        readonly int _index;
        readonly IntPtr _view, _ring, _event;
        readonly HMController _c;
        readonly UsbipEmulatedDevice _dev;
        readonly Switch2ProDevice? _s2;
        readonly Action<long>? _send;
        readonly Thread _pump;
        readonly object _hidLock;
        readonly System.Collections.ICollection _frameQueue;
        readonly FieldInfo _lastSeq;
        readonly byte _inEndpoint;
        bool _bodyStuck;
        readonly ConcurrentQueue<(string Tag, byte[] Data)> _events = new();
        // The seqnums of the reads that return an input report.
        readonly HashSet<uint> _reports = new();
        readonly SemaphoreSlim _sentinel = new(0);
        const byte SentinelSource = 0xEE, SentinelId = 0xEE;

        public HMController Controller => _c;
        public UsbipEmulatedDevice Device => _dev;

        /// <summary><paramref name="claimed"/> false leaves the device with
        /// no connection, as before an import.</summary>
        public CompositeRig(GoldenCase g, Host host, ControllerProfile p, int index, bool claimed = true)
        {
            _g = g;
            _host = host;
            _index = index;
            if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
            _view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, _view, SharedMemoryIO.SHARED_INPUT_SIZE);
            _ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, _ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
            // The controller's SetEvent handle and the pump's wait handle: one
            // unnamed auto-reset event, as the named one is for both (case 6).
            _event = CreateEventW(IntPtr.Zero, false, false, null);
            if (_event == IntPtr.Zero) throw new InvalidOperationException("CreateEventW failed.");
            InputEvents()[index] = _event;

            _dev = NewDevice(p, _view, _ring, _event, claimed ? host.Connection : null, out _inEndpoint);
            _s2 = _dev.Switch2;
            var t = typeof(UsbipEmulatedDevice);
            if (_s2 != null)
                _send = (Action<long>)Delegate.CreateDelegate(typeof(Action<long>), _dev, t.GetMethod("SendSwitch2Report", Instance)!);
            _hidLock = t.GetField("_hidLock", Instance)!.GetValue(_dev)!;
            _frameQueue = (System.Collections.ICollection)t.GetField("_frameQueue", Instance)!.GetValue(_dev)!;
            _lastSeq = t.GetField("_lastSharedSeqNo", Instance)!;

            var handle = (UsbipBackendHandle)Activator.CreateInstance(typeof(UsbipBackendHandle),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object?[] { null, _dev }, null)!;
            _c = Case1Submit.Create(p, index, _view, _ring, handle);
            _c.OutputReceived += (_, pkt) =>
            {
                if ((byte)pkt.Source == SentinelSource && pkt.ReportId == SentinelId) { _sentinel.Release(); return; }
                var o = new byte[2 + pkt.Data.Length];
                o[0] = (byte)pkt.Source;
                o[1] = pkt.ReportId;
                pkt.Data.Span.CopyTo(o.AsSpan(2));
                _events.Enqueue(("rx", o));
            };
            _c.OutputDecoded += (_, e) => _events.Enqueue(("decoded", Case7OutputDecode.Decoded(e)));

            var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), _dev, t.GetMethod("InputPumpLoop", Instance)!);
            _pump = new Thread(loop) { IsBackground = true, Name = "case 8 input pump" };
            t.GetField("_inputThread", Instance)!.SetValue(_dev, _pump);
            _pump.Start();
        }

        /// <summary>The fields the device's paths read, as the constructor
        /// sets them (UsbipEmulatedDevice.cs:272-347): transport index 0, the
        /// audio engine the controller's audio wraps, whose pump idles with no
        /// isochronous transfer and ends at Dispose, and for 057E:2069 the
        /// bulk IN endpoint, the responder and the report buffer (293-304,
        /// 326), the gate the constructor applies.</summary>
        static UsbipEmulatedDevice NewDevice(ControllerProfile p, IntPtr view, IntPtr ring, IntPtr ev,
                                             UsbipServer.Connection? connection, out byte inEndpoint)
        {
            var t = typeof(UsbipEmulatedDevice);
            var dev = (UsbipEmulatedDevice)RuntimeHelpers.GetUninitializedObject(t);
            void Set(string field, object? value) =>
                (t.GetField(field, Instance) ?? throw new MissingFieldException(t.Name, field)).SetValue(dev, value);
            var descriptors = new UsbDescriptorSet(p, 0);
            if (CompiledProtocol.Compile(p, descriptors) != null)
                throw new InvalidOperationException($"{p.Id} declares a device protocol, which this case does not build.");
            string busId = UsbipEmulatedDevice.BusIdFor(0);
            Set("<Descriptors>k__BackingField", descriptors);
            Set("<BusId>k__BackingField", busId);
            Set("<Audio>k__BackingField", new UsbAudioEngine(p, (_, _, _) => { }));
            Set("_index", 0);
            Set("_stubs", FeatureStubTable.From(p, _ => null));
            Set("_lastFeatureParam", -1);
            Set("_lastFeaturePayload", Array.Empty<byte>());
            inEndpoint = 0;
            byte bulkIn = 0;
            foreach (var kv in descriptors.Endpoints)
            {
                var e = kv.Value;
                if (inEndpoint == 0 && e.TransferType == 3 && e.IsIn && e.InterfaceNumber == descriptors.HidInterfaceNumber) inEndpoint = e.Address;
                if (bulkIn == 0 && e.TransferType == 2 && e.IsIn) bulkIn = kv.Key;
            }
            Set("_primaryInEndpoint", inEndpoint);
            bool switch2 = descriptors.VendorId == 0x057E && descriptors.ProductId == 0x2069;
            if (switch2)
            {
                Set("_bulkInEndpoint", bulkIn);
                Set("_switch2", new Switch2ProDevice(descriptors.SerialString, busId));
                Set("_switch2Report", new byte[Switch2ProDevice.ReportSize]);
            }
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
            Set("_portLock", new object());
            Set("_streamSendLock", new object());
            Set("_frameQueue", Activator.CreateInstance(t.GetField("_frameQueue", Instance)!.FieldType));
            Set("_spareFrames", new Stack<byte[]>());
            Set("_pendingInterruptIn", new Dictionary<byte, Queue<uint>>());
            Set("_parkedBulkLength", new Dictionary<uint, int>());
            Set("_readsPerEp", new Dictionary<byte, long>());
            Set("_completionsPerEp", new Dictionary<byte, long>());
            Set("_connection", connection);
            return dev;
        }

        static Dictionary<int, IntPtr> InputEvents() =>
            (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField("s_inputEvents", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

        /// <summary>SubmitState, then the device's own input pump takes the
        /// frame. The case waits until the pump has read this frame
        /// (_lastSharedSeqNo) and, on the Switch 2 composite, until the
        /// responder holds its body (SetBody, UsbipEmulatedDevice.cs:512-518),
        /// so no two frames are in flight. Recorded: the body the controller
        /// wrote (HMController.cs:772-782), or on another composite the
        /// queued frame as an interrupt IN read takes it.</summary>
        public void Submit(in HMGamepadState s)
        {
            _g.SetInput(in s);
            uint before = Seq();
            try { _c.SubmitState(in s); }
            catch (Exception e) { Out("throw", Encoding.ASCII.GetBytes(e.GetType().FullName ?? "?")); return; }
            uint seq = Seq();
            if (seq == before)
            {
                Out("no-frame", ReadOnlySpan<byte>.Empty);
                return;
            }
            if (_s2 != null)
            {
                int ext = Marshal.ReadInt32(_view, SharedMemoryIO.EXTENDED_SIZE_OFFSET);
                var body = new byte[Math.Clamp(ext, 0, SharedMemoryIO.EXTENDED_DATA_CAPACITY)];
                Marshal.Copy(_view + SharedMemoryIO.EXTENDED_DATA_OFFSET, body, 0, body.Length);
                Wait(() => (uint)_lastSeq.GetValue(_dev)! == seq, "read the frame");
                // The pump sets the body right after its read. One that does
                // not within a second is recorded with the body it holds,
                // and the rig stops waiting for bodies from then on.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!_bodyStuck && !BodyIs(body))
                {
                    if (sw.ElapsedMilliseconds > 1000) _bodyStuck = true;
                    Thread.Yield();
                }
                if (_bodyStuck)
                {
                    Out("body-not-taken", body);
                    return;
                }
                Out("body", body);
                return;
            }
            Wait(() => { lock (_hidLock) return _frameQueue.Count > 0; }, "queue the frame");
            InterruptIn();
        }

        /// <summary>Whether the responder's body is these bytes, read under
        /// its own lock (Switch2ProDevice.cs:46, 66, 125-129).</summary>
        bool BodyIs(byte[] body)
        {
            var t = typeof(Switch2ProDevice);
            var sync = t.GetField("_sync", Instance)!.GetValue(_s2)!;
            var have = (byte[])t.GetField("_body", Instance)!.GetValue(_s2)!;
            lock (sync) return have.AsSpan().SequenceEqual(body);
        }

        static void Wait(Func<bool> done, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done())
            {
                if (sw.ElapsedMilliseconds > 5000)
                    throw new TimeoutException($"The device's input pump did not {what} within 5 s.");
                Thread.Yield();
            }
        }

        uint Seq() => (uint)Marshal.ReadInt32(_view);

        /// <summary>One tick of the report clock, while reports are on, as
        /// Switch2Stream makes it.</summary>
        public void Tick()
        {
            if (_s2 == null || _send == null) throw new InvalidOperationException("Only the Switch 2 composite has a report clock.");
            if (!_s2.ReportsOnSince(out long since))
            {
                Out("tick-off", ReadOnlySpan<byte>.Empty);
                return;
            }
            _send(since);
            Out("tick", ReadOnlySpan<byte>.Empty);
            Drain();
        }

        public uint BulkOut(byte[] payload)
        {
            uint seq = _host.Submit(_dev, 0x02, isIn: false, payload.Length, payload);
            Out("bulk-out", payload);
            Drain();
            return seq;
        }

        public uint BulkIn(int length)
        {
            uint seq = _host.Submit(_dev, 0x02, isIn: true, length, ReadOnlySpan<byte>.Empty);
            Out($"bulk-in.{length}", ReadOnlySpan<byte>.Empty);
            Drain();
            return seq;
        }

        public uint InterruptIn()
        {
            uint seq = _host.Submit(_dev, (byte)(_inEndpoint & 0x0F), isIn: true, 64, ReadOnlySpan<byte>.Empty);
            _reports.Add(seq);
            Out("int-in", ReadOnlySpan<byte>.Empty);
            Drain();
            return seq;
        }

        public void Unlink(uint victim)
        {
            _host.Unlink(_dev, victim);
            Out("unlink", ReadOnlySpan<byte>.Empty);
            Drain();
        }

        /// <summary>RecvBulkData, SDL_hidapi_switch2.c:172-198: reads of at
        /// most 64 until the size or a short read.</summary>
        public void SdlReceive(int size)
        {
            while (size > 0)
            {
                int current = Math.Min(size, 64);
                uint seq = _host.Submit(_dev, 0x02, isIn: true, current, ReadOnlySpan<byte>.Empty);
                Out($"bulk-in.{current}", ReadOnlySpan<byte>.Empty);
                var got = Drain();
                int n = got.TryGetValue(seq, out int len) ? len : -1;
                if (n < 0)
                {
                    // libusb's 100 ms timeout: SDL cancels the read.
                    _host.Unlink(_dev, seq);
                    Out("unlink", ReadOnlySpan<byte>.Empty);
                    Drain();
                    return;
                }
                size -= n;
                if (n < current) break;
            }
        }

        public void GetInput(byte id, ushort wLength)
        {
            _reports.Add(_host.Submit(_dev, 0x00, isIn: true, wLength, ReadOnlySpan<byte>.Empty,
                setup: Setup(0xA1, 0x01, (ushort)(0x0100 | id), _dev.Descriptors.HidInterfaceNumber, wLength)));
            Out($"get-input.{id:X2}.w{wLength}", ReadOnlySpan<byte>.Empty);
            Drain();
        }

        /// <summary>SET_FEATURE(PORT_RESET) to the hub port, which resets
        /// the device (UsbipEmulatedDevice.cs:1048-1056).</summary>
        public void PortReset()
        {
            _host.Submit(_dev, 0x00, isIn: false, 0, ReadOnlySpan<byte>.Empty, setup: Setup(0x23, 0x03, 4, 1, 0));
            Out("port-reset", ReadOnlySpan<byte>.Empty);
            Drain();
        }

        /// <summary>The raw submits a caller might try on this persona.</summary>
        public void RawSubmits()
        {
            foreach (int len in new[] { 0, 1, 24, 63, 64 })
            {
                var b = new byte[len];
                for (int i = 0; i < len; i++) b[i] = (byte)(i + 1);
                Try($"raw.{len}", () => _c.SubmitRawReport(b));
                Try($"raw-ext.{len}", () => _c.SubmitRawExtendedReport(b));
            }
        }

        void Try(string tag, Action a)
        {
            try { a(); Out(tag + ".ok", ReadOnlySpan<byte>.Empty); }
            catch (Exception e) { Out(tag + ".throw", Encoding.ASCII.GetBytes(e.GetType().FullName ?? "?")); }
        }

        /// <summary>Host interrupt OUT reports, which the device publishes
        /// to the ring, then a sentinel slot. Once the controller's output
        /// thread reaches it, every event of the batch has run.</summary>
        public void OutputBatch(List<byte[]> reports)
        {
            foreach (var d in reports)
            {
                _host.Submit(_dev, 0x01, isIn: false, d.Length, d);
                Out("int-out", d);
                Drain();
            }
            Flush();
        }

        /// <summary>A SET_REPORT on the HID interface, then the controller's
        /// events for it.</summary>
        public void SetReport(byte type, byte id, byte[] payload)
        {
            _host.Submit(_dev, 0x00, isIn: false, payload.Length, payload,
                setup: Setup(0x21, 0x09, (ushort)((type << 8) | id), _dev.Descriptors.HidInterfaceNumber, (ushort)payload.Length));
            Out($"set.t{type}.{id:X2}", payload);
            Drain();
            Flush();
        }

        /// <summary>One raw report and one raw extended report of the
        /// declared extended size, then the frames.</summary>
        public void RawOnce()
        {
            int size = _c.Profile.Inner.ExtendedReport?.Size ?? 8;
            var b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = (byte)(0x40 + i);
            foreach (var (tag, a) in new (string, Action)[] { ("raw", () => _c.SubmitRawReport(b)), ("raw-ext", () => _c.SubmitRawExtendedReport(b)) })
            {
                uint before = Seq();
                try { a(); Out(tag + ".ok", ReadOnlySpan<byte>.Empty); }
                catch (Exception e) { Out(tag + ".throw", Encoding.ASCII.GetBytes(e.GetType().FullName ?? "?")); continue; }
                if (Seq() == before) { Out("no-frame", ReadOnlySpan<byte>.Empty); continue; }
                if (_s2 != null) continue;
                Wait(() => { lock (_hidLock) return _frameQueue.Count > 0; }, "queue the frame");
                InterruptIn();
            }
        }

        public void Claim(UsbipServer.Connection connection, string who)
        {
            bool ok = _dev.TryClaimConnection(connection);
            Out($"claim.{who}", new[] { (byte)(ok ? 1 : 0) });
            Drain();
        }

        public void Detach(UsbipServer.Connection connection, string who)
        {
            _dev.DetachConnection(connection);
            Out($"detach.{who}", new[] { (byte)_dev.VhciPort });
            Drain();
        }

        public void SetPort(UsbipServer.Connection connection, int port)
        {
            _dev.SetVhciPortFor(connection, port);
            Out("port", new[] { (byte)port, (byte)_dev.VhciPort });
        }

        /// <summary>A sentinel slot after the batch. Once the controller's
        /// output thread reaches it, every event of the batch has run.</summary>
        void Flush()
        {
            uint head = (uint)Marshal.ReadInt32(_ring);
            Case7OutputDecode.Write(_ring, ref head, SentinelSource, SentinelId, Array.Empty<byte>());
            if (!_sentinel.Wait(10_000))
                throw new TimeoutException("The controller's output thread did not reach the batch's end in 10 s.");
            while (_events.TryDequeue(out var ev)) Out(ev.Tag, ev.Data);
        }

        public void Registers()
        {
            if (_s2 == null)
            {
                Out("regs", new[] { (byte)_dev.BulkReadsCanceled, (byte)_dev.VhciPort });
                return;
            }
            var r = new byte[10];
            r[0] = (byte)(_s2.ReportsOn ? 1 : 0);
            r[1] = _s2.SelectedReport;
            r[2] = _s2.FeatureMask;
            r[3] = _s2.FeatureEnabled;
            r[4] = _s2.PlayerLeds;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(5), _s2.ReportsSent);
            r[9] = (byte)_dev.BulkReadsCanceled;
            Out("regs", r);
        }

        /// <summary>Every RET_SUBMIT and RET_UNLINK the last call sent, in
        /// order: status, actual length and data. Returns the IN lengths by
        /// seqnum.</summary>
        Dictionary<uint, int> Drain()
        {
            var got = new Dictionary<uint, int>();
            foreach (var (kind, seq, status, actual, data) in _host.DrainReplies())
            {
                var o = new byte[8 + data.Length];
                BinaryPrimitives.WriteInt32LittleEndian(o, status);
                BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), actual);
                data.CopyTo(o, 8);
                // --explain: an interrupt IN read or a GET_REPORT(Input)
                // returns an input report the device built from the frame.
                _g.NextIsReport(kind == UsbipProtocol.RetSubmit && _reports.Remove(seq));
                Out(kind == UsbipProtocol.RetUnlink ? "ret-unlink" : "ret-submit", o);
                if (kind == UsbipProtocol.RetSubmit) got[seq] = data.Length;
            }
            return got;
        }

        void Out(string tag, ReadOnlySpan<byte> data) => _g.Out(tag, data);

        /// <summary>Dispose stops the pump, which closes the event as it ends
        /// (UsbipEmulatedDevice.cs:445-456).</summary>
        public void Dispose()
        {
            Case7OutputDecode.Stop(_c);
            _dev.Dispose();
            if (!_pump.Join(5000)) throw new TimeoutException("The device's input pump did not end within 5 s.");
            InputEvents().Remove(_index);
            Case1Submit.Forget(_index);
            Marshal.FreeHGlobal(_view);
            Marshal.FreeHGlobal(_ring);
        }
    }

    static ulong Setup(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, ushort wLength) =>
        bmRequestType | ((ulong)bRequest << 8) | ((ulong)wValue << 16) | ((ulong)wIndex << 32) | ((ulong)wLength << 48);

    // ── Raw submits on the controller case 1 builds ──────────────────────

    /// <summary>SubmitRawReport and SubmitRawExtendedReport at the lengths
    /// that pick each branch: empty and past the field (which throw), one
    /// byte, the declared extended report less one and whole (the
    /// data-only and verbatim forms, HMController.cs:1480-1502), 64, the
    /// data field's 256 and the extended field's 80. A SubmitState first,
    /// so the GIP slice a raw frame carries is the state's
    /// (HMController.cs:1185-1189), and between them, so a raw frame after
    /// a state shows what it keeps. The idle pump's repeat of the last raw
    /// frame (1611-1628), which a controller with an idle pump remembers
    /// (1151, 1251, RememberRawFrame 1455-1463), follows each, with the pump's thread object
    /// present and never started as in case 6. Each output is the section as
    /// case 1 records it and whether the write woke the XUSB companion, on an
    /// unnamed event the case puts where the controller looks for the
    /// companion's (SharedMemoryIO.cs:486-490, 908-917).</summary>
    static void Raw(GoldenCase g, ControllerProfile p, int index, Rng rng)
    {
        if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
        g.Stream($"raw/{p.Id}/submits");
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        IntPtr companion = IntPtr.Zero;
        try
        {
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
            companion = CreateEventW(IntPtr.Zero, false, false, null);
            if (companion == IntPtr.Zero) throw new InvalidOperationException("CreateEventW failed.");
            CompanionEvents()[index] = companion;
            var c = Case1Submit.Create(p, index, view);
            g.Explainer = new FrameExplainer(c, tail: 1);
            typeof(HMController).GetField("_idleThread", Instance)!.SetValue(c, new Thread(() => { }) { Name = "case 8 idle pump, never started" });
            var repeat = typeof(HMController).GetMethod("RepeatLastFrame", Instance)!;
            var section = new byte[SharedMemoryIO.SHARED_INPUT_SIZE];
            var frame = new byte[2 + SharedMemoryIO.DATA_CAPACITY + 2 + SharedMemoryIO.EXTENDED_DATA_CAPACITY + SharedMemoryIO.GIP_DATA_LENGTH];
            uint stamp = 0;
            void Record(string tag)
            {
                Marshal.Copy(view, section, 0, section.Length);
                var f = Case1Submit.Frame(section, frame);
                var o = new byte[f.Length + 1];
                f.CopyTo(o);
                o[^1] = (byte)(WaitForSingleObject(companion, 0) == 0 ? 1 : 0);
                g.Out(tag, o);
            }
            void Do(string tag, Action a)
            {
                try { a(); Record(tag); }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    g.Out(tag + ".throw", Encoding.ASCII.GetBytes(e.InnerException.GetType().FullName ?? "?"));
                }
                catch (Exception e) { g.Out(tag + ".throw", Encoding.ASCII.GetBytes(e.GetType().FullName ?? "?")); }
            }
            void State()
            {
                var s = RandomState(rng);
                stamp += 12000;
                s.SensorTimestamp = stamp;
                g.SetInput(in s);
                Do("state", () => c.SubmitState(in s));
            }
            int extSize = p.ExtendedReport?.Size ?? 0;
            var lengths = new SortedSet<int> { 0, 1, 2, 64, SharedMemoryIO.EXTENDED_DATA_CAPACITY, SharedMemoryIO.EXTENDED_DATA_CAPACITY + 1, SharedMemoryIO.DATA_CAPACITY, SharedMemoryIO.DATA_CAPACITY + 1 };
            if (extSize > 1) { lengths.Add(extSize - 1); lengths.Add(extSize); }
            int input = c.Profile.InputReportSize;
            if (input > 1) { lengths.Add(input - 1); lengths.Add(input); }
            State();
            foreach (int len in lengths)
            {
                var b = new byte[len];
                rng.Fill(b);
                Do($"raw.{len}", () => c.SubmitRawReport(b));
                Do($"repeat.raw.{len}", () => repeat.Invoke(c, null));
                State();
                Do($"raw-ext.{len}", () => c.SubmitRawExtendedReport(b));
                Do($"repeat.raw-ext.{len}", () => repeat.Invoke(c, null));
                State();
            }
            Case1Submit.Forget(index);
        }
        finally
        {
            CompanionEvents().Remove(index);
            if (companion != IntPtr.Zero) CloseHandle(companion);
            Marshal.FreeHGlobal(view);
        }
    }

    static Dictionary<int, IntPtr> CompanionEvents() =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField("s_companionInputEvents", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>The host side, as in cases 4 and 6, for transfers whose
    /// RET_SUBMIT may come at once, later, or never.</summary>
    sealed class Host : IDisposable
    {
        readonly string _path;
        readonly Socket _listener, _client, _server;
        readonly byte[] _header = new byte[UsbipProtocol.HeaderSize];
        readonly Dictionary<uint, bool> _isIn = new();
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
            _client.ReceiveTimeout = 5000;
            var t = typeof(UsbipServer.Connection);
            Connection = (UsbipServer.Connection)RuntimeHelpers.GetUninitializedObject(t);
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            (t.GetField("_socket", f) ?? throw new MissingFieldException(t.Name, "_socket")).SetValue(Connection, _server);
            (t.GetField("_sendLock", f) ?? throw new MissingFieldException(t.Name, "_sendLock")).SetValue(Connection, new object());
        }

        public uint Submit(UsbipEmulatedDevice dev, byte endpoint, bool isIn, int length, ReadOnlySpan<byte> payload, ulong setup = 0)
        {
            var cmd = new UsbipProtocol.CommandHeader
            {
                Command = UsbipProtocol.CmdSubmit,
                Seqnum = ++_seq,
                Direction = isIn ? 1u : 0u,
                Ep = endpoint,
                TransferBufferLength = length,
                NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
                Setup = setup,
            };
            _isIn[_seq] = isIn;
            dev.HandleSubmit(cmd, payload, ReadOnlySpan<(uint, uint)>.Empty);
            return _seq;
        }

        public void Unlink(UsbipEmulatedDevice dev, uint victim) => dev.HandleUnlink(++_seq, victim);

        /// <summary>Every reply waiting on the socket. The device sends from
        /// the caller's thread, so what a call sent is here when it
        /// returns.</summary>
        public List<(uint Kind, uint Seq, int Status, int Actual, byte[] Data)> DrainReplies()
        {
            var list = new List<(uint, uint, int, int, byte[])>();
            while (_client.Available > 0)
            {
                ReadExactly(_header);
                uint kind = BinaryPrimitives.ReadUInt32BigEndian(_header);
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
                int status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
                int actual = kind == UsbipProtocol.RetSubmit ? BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24)) : 0;
                if (kind != UsbipProtocol.RetSubmit && kind != UsbipProtocol.RetUnlink)
                    throw new InvalidDataException($"Unexpected reply kind {kind} for {seq}.");
                bool isIn = kind == UsbipProtocol.RetSubmit && _isIn.TryGetValue(seq, out bool b) && b;
                var data = new byte[isIn ? actual : 0];
                ReadExactly(data);
                list.Add((kind, seq, status, actual, data));
            }
            return list;
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
}
