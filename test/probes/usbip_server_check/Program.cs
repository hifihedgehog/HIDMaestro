// USB/IP server protocol check (issue #39).
//
// Plays usbip-win2's role over loopback TCP against the real in-process
// server + emulated composite device, no kernel driver involved. Every
// wire expectation below is the 0.9.7.5 receive path's, read at source
// (drivers/ude/wsk_receive.cpp, vhci_ioctl.cpp, include/usbip/proto*.h):
//
//   - import handshake: op_common(0x0111, OP_REP_IMPORT, ST_OK) + the
//     312-byte usbip_usb_device with the busid echoed
//   - descriptors served byte-for-byte from the profile's verbatim blobs
//   - RET_SUBMIT non-isoch number_of_packets == -1 on the wire
//   - isochronous IN: compacted payload, descriptor offsets echoing the
//     submit, actual_length == sum of per-packet actuals
//   - isochronous OUT: descriptors only, paced at 1 ms per packet
//   - RET_UNLINK: -ECONNRESET while queued, 0 after completion
//   - stream state with no SET_INTERFACE (usbip-win2 0.9.8.1 beside an
//     older filter): traffic opens each stream and its end closes it, and
//     an interface that has seen SET_INTERFACE follows SET_INTERFACE alone
//   - a throwing handler on any public audio event, each in a child
//     process: the handler after it still runs and the connection still
//     answers
//   - a ControlChanged or StreamingChanged handler that blocks stalls no
//     transfer, and the events still arrive in the order the host sent
//     them
//   - the import's port lookup goes on after failed lookups of the host
//     controller's interface
//   - "--offline", which needs no elevation and no device: the audio
//     engine's event thread, driven in process as the connection drives it
//     (its wake event closed at Dispose, queued events raised at Dispose,
//     IsStreaming current while a handler blocks, one waiting write per
//     control while a handler blocks, every handle closed and nothing
//     thrown to the connection when a thread does not start, a change
//     queued then raised at Dispose, its event thread known as its own),
//     the device's input pump woken at Dispose, timed over 30 runs, and
//     the connection owner lookup and location match of Program.Lookup.cs
//   - teardown: the connection's detach racing the device's Dispose, and a
//     transfer that arrives after Dispose, neither touching a disposed
//     object or ending the process ("teardown" runs these alone)
//   - Dispose time with the input pump waiting for a frame, p50 and p95
//     over 30 devices ("dispose-timing" runs it alone)
//
// Bridges to the SDK's shared-memory contract are exercised end to end:
// the probe writes input frames exactly as HMController.SubmitState does
// and reads the output ring exactly as OutputPollLoop does.
//
// Requires elevation (Global\ section creation), touches no PnP.
// Exit 0 PASS / 1 FAIL.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static int s_total, s_failures, s_unhandled;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // Above every transport index a create takes (ControllerLease.IndexLimit,
    // 4096), so no program's controller can hold these. A create starts at
    // 64 while a v1.12.0 program runs, inside the old 60 to 91.
    const int Index = 5000;

    static int Main(string[] args)
    {
        // The probe plays the host controller from this process.
        UsbipServer.AdmitImportsFromThisProcess = true;
        if (args.Length >= 2 && args[0] == "--throwing") return ThrowingChild(args[1]);
        if (args.Length >= 1 && args[0] == "--held-pump") return HeldPumpChild();
        if (args.Length >= 1 && args[0] == "--offline") return Offline();

        Console.WriteLine("=== USB/IP server protocol (issue #39) ===");
        // An exception that escapes a connection thread ends the process.
        // The handler records it and ends the process itself with exit code
        // 3, and the error mode keeps any crash from opening an error dialog.
        SetErrorMode(QuietErrorMode);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Interlocked.Increment(ref s_unhandled);
            // The full text (type, message, inner exceptions, stack) so a
            // battery transcript names the throwing frame without a rerun.
            // The managed ID tells the main thread, 1, from a thread-pool
            // thread, which has no name either.
            Console.WriteLine($"  [FAIL] unhandled {e.ExceptionObject.GetType().Name} on thread " +
                              $"{Thread.CurrentThread.Name ?? "(unnamed)"} (managed ID {Environment.CurrentManagedThreadId}): {e.ExceptionObject}");
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total + 1} checks passed, the process ended by an unhandled exception ===");
            Console.Out.Flush();
            TerminateProcess(GetCurrentProcess(), 3);
        };

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var server = UsbipServer.GetOrStart();

        // "dispose-timing" runs the dispose timing alone.
        if (args.Contains("dispose-timing"))
        {
            DisposeTiming(ctx);
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
            return s_failures == 0 ? 0 : 1;
        }

        // "teardown" runs the teardown checks alone.
        if (!args.Contains("teardown"))
        {
            var profile = ctx.GetProfile("dualsense-composite")!.Inner;
            var device = new UsbipEmulatedDevice(profile, Index);
            server.Register(device);

            var outFrames = new MemoryStream();
            device.Audio.OutFrames += pcm => { lock (outFrames) outFrames.Write(pcm.Span); };
            var controlWrites = new List<(byte Unit, byte Sel, short Raw)>();
            device.Audio.ControlChanged += (u, s, c, v) => { lock (controlWrites) controlWrites.Add((u, s, v)); };

            try
            {
                RunAll(server, device, profile, outFrames, controlWrites);
                Handshake(server, device);
                ThrowingHandlers();
                PortLookup();
                SlowHandlers(ctx, server);
            }
            finally
            {
                server.Unregister(device);
                device.Dispose();
                SharedMemoryIO.DestroyController(Index);
            }
        }

        if (!args.Contains("teardown")) DeclaredInputReports(ctx, server);
        if (!args.Contains("teardown")) ProtocolInputGetReport(ctx, server);
        if (!args.Contains("teardown")) LongIsoUrbs(ctx, server);
        Teardown(ctx, server);
        if (!args.Contains("teardown")) DisposeTiming(ctx);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>Dispose time of a device whose input pump sits in its wait
    /// for a frame, over 30 devices, each disposed at a random point of
    /// that 500 ms wait. Until Dispose woke the pump, each Dispose waited
    /// out the rest of the wait, 0 to 500 ms.</summary>
    static void DisposeTiming(HMContext ctx)
    {
        Console.WriteLine("-- Dispose time with the input pump waiting for a frame (30 devices)");
        // One index per device, since the 30 run at once.
        const int TimingIndex = Index + 2;
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        var devices = new UsbipEmulatedDevice?[30];
        double[] ms;
        try
        {
            ms = DisposeTogether(30, new Random(43), 50, 500, i => devices[i] = new UsbipEmulatedDevice(profile, TimingIndex + i));
        }
        finally
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i] == null) continue;
                devices[i]!.Dispose();
                SharedMemoryIO.DestroyController(TimingIndex + i);
            }
        }
        int lost = Array.FindAll(ms, double.IsNaN).Length;
        Array.Sort(ms);
        // Nearest rank: the 15th and the 29th of 30. A pump that waits out
        // its wait costs 0 to 450 ms at random, a median near 225 ms, and a
        // woken one under a millisecond. The median tells them apart, and
        // load that slows a few Disposes does not move it.
        double p50 = ms[14], p95 = ms[28], max = ms[29];
        Check("a USB/IP device's Dispose does not wait out its input pump's 500 ms wait (median under 100 ms over 30)",
              p50 < 100 && lost == 0,
              $"p50 {p50:F1} ms, p95 {p95:F1} ms, max {max:F1} ms" + (lost > 0 ? $", {lost} Dispose(s) threw or hung" : ""));
    }

    static void RunAll(UsbipServer server, UsbipEmulatedDevice device, ControllerProfile profile,
                       MemoryStream outFrames, List<(byte, byte, short)> controlWrites)
    {
        var set = device.Descriptors;

        // ── Devlist ─────────────────────────────────────────────────────
        Console.WriteLine("\n-- OP_REQ_DEVLIST --");
        using (var c = new Client(server.Port))
        {
            c.SendOpCommon(0x8005);
            var op = c.ReadExactly(8);
            Check("devlist reply op_common OK",
                  BinaryPrimitives.ReadUInt16BigEndian(op) == 0x0111
                  && BinaryPrimitives.ReadUInt16BigEndian(op.AsSpan(2)) == 0x0005
                  && BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 0);
            var ndev = BinaryPrimitives.ReadUInt32BigEndian(c.ReadExactly(4));
            Check("one exported device", ndev == 1, ndev.ToString());
            var row = c.ReadExactly(312);
            Check("devlist row busid + VID/PID",
                  ReadStr(row, 256, 32) == device.BusId
                  && BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(300)) == 0x054C
                  && BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(302)) == 0x0CE6);
            var ifaceRows = c.ReadExactly(4 * 4);
            Check("devlist interface rows: audio, audio, audio, HID",
                  ifaceRows[0] == 0x01 && ifaceRows[4] == 0x01 && ifaceRows[8] == 0x01 && ifaceRows[12] == 0x03);
        }

        // ── Import: unknown busid refused ───────────────────────────────
        Console.WriteLine("\n-- OP_REQ_IMPORT --");
        using (var c = new Client(server.Port))
        {
            c.SendImport("9-9");
            var op = c.ReadExactly(8);
            Check("unknown busid answered ST_NODEV",
                  BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 4);
        }

        // ── The real session ────────────────────────────────────────────
        using var cl = new Client(server.Port);
        cl.SendImport(device.BusId);
        {
            var op = cl.ReadExactly(8);
            Check("import op_common OK",
                  BinaryPrimitives.ReadUInt16BigEndian(op.AsSpan(2)) == 0x0003
                  && BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 0);
            var dev = cl.ReadExactly(312);
            Check("import reply echoes busid", ReadStr(dev, 256, 32) == device.BusId);
            Check("import reply: high speed, devid busnum/devnum",
                  BinaryPrimitives.ReadUInt32BigEndian(dev.AsSpan(296)) == 3
                  && BinaryPrimitives.ReadUInt32BigEndian(dev.AsSpan(288)) == 1
                  && BinaryPrimitives.ReadUInt32BigEndian(dev.AsSpan(292)) == Index + 1);
        }

        // ── Standard descriptors, byte-exact vs the blobs ───────────────
        Console.WriteLine("\n-- GET_DESCRIPTOR --");
        var devDesc = cl.ControlIn(0x80, 0x06, 0x0100, 0, 18);
        Check("device descriptor == verbatim blob", devDesc.Status == 0
              && devDesc.Data.SequenceEqual(set.DeviceDescriptor));

        var cfg9 = cl.ControlIn(0x80, 0x06, 0x0200, 0, 9);
        Check("config header read (wLength 9) truncates correctly",
              cfg9.Status == 0 && cfg9.Data.Length == 9
              && cfg9.Data.SequenceEqual(set.ConfigurationDescriptor.Take(9)));

        var cfgFull = cl.ControlIn(0x80, 0x06, 0x0200, 0, 512);
        Check("full config descriptor == 227-byte verbatim blob",
              cfgFull.Status == 0 && cfgFull.Data.SequenceEqual(set.ConfigurationDescriptor));

        var qual = cl.ControlIn(0x80, 0x06, 0x0600, 0, 10);
        Check("device qualifier served (dual-speed pad)",
              qual.Status == 0 && qual.Data.SequenceEqual(set.DeviceQualifier));

        var other = cl.ControlIn(0x80, 0x06, 0x0700, 0, 512);
        Check("other-speed configuration == verbatim blob",
              other.Status == 0 && other.Data.SequenceEqual(set.OtherSpeedConfiguration!));

        var lang = cl.ControlIn(0x80, 0x06, 0x0300, 0, 255);
        Check("string 0 is the en-US LANGID table",
              lang.Status == 0 && lang.Data.SequenceEqual(new byte[] { 0x04, 0x03, 0x09, 0x04 }));

        // Derived from the profile rather than hard-coded, so a future
        // product-string change cannot leave this asserting a stale literal.
        // The string moved to the current hardware's longer form in v1.4.5
        // (issue #44); the 2020 launch pad reported "Wireless Controller".
        string expectProduct = profile.ProductString;
        int expectLen = 2 + expectProduct.Length * 2;
        var prod = cl.ControlIn(0x80, 0x06, 0x0302, 0x0409, 255);
        Check($"product string is '{expectProduct}' ({expectLen}-byte descriptor)",
              prod.Status == 0 && prod.Data.Length == expectLen
              && prod.Data[0] == expectLen && prod.Data[1] == 3
              && System.Text.Encoding.Unicode.GetString(prod.Data, 2, expectLen - 2) == expectProduct,
              prod.Status == 0 ? $"{prod.Data.Length} bytes" : $"status {prod.Status}");

        var msOs = cl.ControlIn(0x80, 0x06, 0x03EE, 0, 255);
        Check("MS OS string 0xEE stalls (real pad has none)", msOs.Status == -32);

        var report = cl.ControlIn(0x81, 0x06, 0x2200, 3, 512);
        Check("HID report descriptor == profile's 273 bytes",
              report.Status == 0 && report.Data.SequenceEqual(profile.GetDescriptorBytes()!));

        var status = cl.ControlIn(0x80, 0x00, 0, 0, 2);
        Check("GET_STATUS(device) reports self-powered",
              status.Status == 0 && status.Data.Length == 2 && status.Data[0] == 0x01);

        // ── Configure ───────────────────────────────────────────────────
        Console.WriteLine("\n-- Configuration --");
        var setCfg = cl.ControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>());
        Check("SET_CONFIGURATION(1) acks", setCfg.Status == 0);
        var getCfg = cl.ControlIn(0x80, 0x08, 0, 0, 1);
        Check("GET_CONFIGURATION returns 1", getCfg.Status == 0 && getCfg.Data[0] == 1);
        var setIdle = cl.ControlOut(0x21, 0x0A, 0, 3, Array.Empty<byte>());
        Check("HID SET_IDLE acks (pcap pkt49)", setIdle.Status == 0);

        // ── HID input: shared section → interrupt IN ────────────────────
        Console.WriteLine("\n-- HID input --");
        IntPtr view = SharedMemoryIO.EnsureInputMapping(Index);
        IntPtr evt = SharedMemoryIO.GetInputEvent(Index);
        uint seqNo = ReadSeqNoBase(view);

        // Frame first, read second: the frame queues, the read drains it.
        var data1 = MakePattern(63, 0x10);
        SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, data1, 63, null);
        Thread.Sleep(60); // input pump wake + queue
        uint irSeq = cl.NextSeq();
        cl.SendSubmitInterruptIn(irSeq, 4, 64);
        var ir = cl.ReadRet(irSeq);
        Check("queued frame served on interrupt IN", ir.Status == 0 && ir.Data.Length == 64);
        Check("report = [0x01][data][zero-fill]", ir.Data[0] == 0x01
              && ir.Data.Skip(1).Take(63).SequenceEqual(data1));
        Check("non-isoch RET number_of_packets is -1 on the wire", ir.RawNumberOfPackets == -1);

        // Read first, frame second: the read parks, the frame completes it.
        uint irSeq2 = cl.NextSeq();
        cl.SendSubmitInterruptIn(irSeq2, 4, 64);
        Thread.Sleep(30);
        var data2 = MakePattern(63, 0x77);
        SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, data2, 63, null);
        var ir2 = cl.ReadRet(irSeq2);
        Check("parked interrupt IN completed by the next frame",
              ir2.Status == 0 && ir2.Data[0] == 0x01 && ir2.Data.Skip(1).Take(63).SequenceEqual(data2));

        // GET_REPORT(Input) control path serves the latest report.
        var giRep = cl.ControlIn(0xA1, 0x01, 0x0101, 3, 64);
        Check("GET_REPORT(Input) serves the latest report",
              giRep.Status == 0 && giRep.Data.Skip(1).Take(63).SequenceEqual(data2));
        // An input report ID the descriptor does not declare, and report ID
        // 0 on a pad whose reports carry IDs, stall (USB 2.0 9.2.7) and
        // never answer with report 0x01's bytes.
        var giOther = cl.ControlIn(0xA1, 0x01, 0x0105, 3, 64);
        var giZero = cl.ControlIn(0xA1, 0x01, 0x0100, 3, 64);
        Check("GET_REPORT(Input 0x05) and GET_REPORT(Input 0) stall",
              giOther.Status == -32 && giOther.Data.Length == 0 && giZero.Status == -32 && giZero.Data.Length == 0,
              $"{giOther.Status}/{giOther.Data.Length} {giZero.Status}/{giZero.Data.Length}");

        // ── HID output: interrupt OUT + SET_REPORT → ring ───────────────
        Console.WriteLine("\n-- HID output --");
        IntPtr outView = SharedMemoryIO.EnsureOutputMapping(Index);
        uint ringSeq = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(outView, 0);
        var ringBuf = new byte[256];

        var outReport = new byte[48];
        outReport[0] = 0x02; // DualSense USB output RID
        for (int i = 1; i < outReport.Length; i++) outReport[i] = (byte)(i * 3);
        uint outSeq = cl.NextSeq();
        cl.SendSubmitInterruptOut(outSeq, 3, outReport);
        var or = cl.ReadRet(outSeq);
        Check("interrupt OUT acked with full length", or.Status == 0 && or.ActualLength == outReport.Length);
        Check("output ring got HidOutput RID 0x02 with the payload",
              WaitRing(outView, ref ringSeq, ringBuf, out var src, out var rid, out int rsize)
              && src == 0 && rid == 0x02 && rsize == 47
              && ringBuf.Take(47).SequenceEqual(outReport.Skip(1)));

        var setRep = cl.ControlOut(0x21, 0x09, 0x0305, 3, new byte[] { 0x05, 0xAA, 0xBB });
        Check("SET_REPORT(Feature) acks", setRep.Status == 0);
        Check("ring got HidFeature RID 0x05",
              WaitRing(outView, ref ringSeq, ringBuf, out src, out rid, out rsize)
              && src == 1 && rid == 0x05 && rsize == 2 && ringBuf[0] == 0xAA && ringBuf[1] == 0xBB);

        // ── Sony feature stubs (driver.c table) ─────────────────────────
        Console.WriteLine("\n-- Sony Get_Feature stubs --");
        var f05 = cl.ControlIn(0xA1, 0x01, 0x0305, 3, 41);
        Check("0x05 calibration: 41 bytes, RID echoed", f05.Status == 0
              && f05.Data.Length == 41 && f05.Data[0] == 0x05);
        Check("feature read notified to the ring (armOn lane)",
              WaitRing(outView, ref ringSeq, ringBuf, out src, out rid, out rsize)
              && src == 3 && rid == 0x05 && rsize == 0);
        // Calibration is a DIVISOR, not decoration (issue #43). Compute the
        // denominators the way hid-playstation.c does and require every one
        // to be non-zero: the all-zero blob served before made SDL's
        // sensitivity 0.0f/0 (NaN) and made the Linux driver declare the
        // calibration invalid, which is what native-PlayStation titles
        // rejected the pad on.
        short LE(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));
        {
            var c = f05.Data;
            short pitchBias = LE(c, 1), yawBias = LE(c, 3), rollBias = LE(c, 5);
            short pitchPlus = LE(c, 7), pitchMinus = LE(c, 9);
            short yawPlus = LE(c, 11), yawMinus = LE(c, 13);
            short rollPlus = LE(c, 15), rollMinus = LE(c, 17);
            short speedPlus = LE(c, 19), speedMinus = LE(c, 21);
            short accXPlus = LE(c, 23), accXMinus = LE(c, 25);
            short accYPlus = LE(c, 27), accYMinus = LE(c, 29);
            short accZPlus = LE(c, 31), accZMinus = LE(c, 33);

            int gyroPitch = Math.Abs(pitchPlus - pitchBias) + Math.Abs(pitchMinus - pitchBias);
            int gyroYaw = Math.Abs(yawPlus - yawBias) + Math.Abs(yawMinus - yawBias);
            int gyroRoll = Math.Abs(rollPlus - rollBias) + Math.Abs(rollMinus - rollBias);
            int speed2x = speedPlus + speedMinus;
            int rangeX = accXPlus - accXMinus, rangeY = accYPlus - accYMinus, rangeZ = accZPlus - accZMinus;

            Check("calibration gyro denominators are non-zero",
                  gyroPitch != 0 && gyroYaw != 0 && gyroRoll != 0,
                  $"pitch {gyroPitch}, yaw {gyroYaw}, roll {gyroRoll}");
            Check("calibration accel ranges are non-zero",
                  rangeX != 0 && rangeY != 0 && rangeZ != 0,
                  $"x {rangeX}, y {rangeY}, z {rangeZ}");
            Check("gyro speed_2x is non-zero (SDL numerator)", speed2x != 0, $"{speed2x}");
            // SDL_hidapi_ps5.c divides by (plus - minus) with no absolute
            // value, in this one DualSense order, so every plus must be
            // positive and every minus negative or that axis turns
            // backwards. Issue #64 also set the scale to the identity for
            // 16 per degree/second and 8192 per g, the units consumers
            // submit: denominator 16 * speed_2x, range_2g 16384.
            Check("every gyro plus is positive and every minus negative in DualSense order",
                  pitchPlus > 0 && yawPlus > 0 && rollPlus > 0
                  && pitchMinus < 0 && yawMinus < 0 && rollMinus < 0,
                  $"+{pitchPlus}/{yawPlus}/{rollPlus} {pitchMinus}/{yawMinus}/{rollMinus}");
            Check("gyro denominators equal 16 * speed_2x (16 per degree/second)",
                  gyroPitch == 16 * speed2x && gyroYaw == 16 * speed2x && gyroRoll == 16 * speed2x,
                  $"16 * {speed2x} vs {gyroPitch}/{gyroYaw}/{gyroRoll}");
            Check("accel ranges equal 16384 (8192 per g)",
                  rangeX == 16384 && rangeY == 16384 && rangeZ == 16384,
                  $"x {rangeX}, y {rangeY}, z {rangeZ}");
        }

        var f09 = cl.ControlIn(0xA1, 0x01, 0x0309, 3, 20);
        Check("0x09 pairing: 20 bytes as the descriptor declares", f09.Status == 0
              && f09.Data.Length == 20 && f09.Data[0] == 0x09,
              f09.Status == 0 ? $"{f09.Data.Length} bytes" : $"status {f09.Status}");
        Check("0x09 carries a non-zero, locally administered MAC",
              f09.Status == 0 && (f09.Data[1] != 0 || f09.Data[2] != 0 || f09.Data[3] != 0
                                  || f09.Data[4] != 0 || f09.Data[5] != 0 || f09.Data[6] != 0)
              && (f09.Data[1] & 0x02) != 0,
              f09.Status == 0 ? string.Join(":", f09.Data.Skip(1).Take(6).Select(b => b.ToString("X2"))) : "");
        WaitRing(outView, ref ringSeq, ringBuf, out _, out _, out _); // drain the 0x09 notify

        // 0x20 was zero-filled but for fwType until #43's second round.
        // F1 22 reads it before it ever asks for calibration and abandons
        // the pad on zeros, which is why the earlier calibration and
        // product-string fixes changed nothing for it. The expected bytes
        // are spelled out here and in sony_feature_gate_check so the two
        // backends cannot drift apart silently: this probe covers the
        // composite lane, that one covers the UMDF2 lane, and both must
        // match this literal.
        byte[] expect20 = {
            0x20, 0x4A, 0x75, 0x6C, 0x20, 0x20, 0x34, 0x20,
            0x32, 0x30, 0x32, 0x35, 0x31, 0x30, 0x3A, 0x31,
            0x30, 0x3A, 0x33, 0x32, 0x03, 0x00, 0x04, 0x00,
            0x10, 0x13, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x01,
            0x01, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x30, 0x06, 0x00, 0x00,
            0x3C, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x02, 0x00,
            0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        var f20 = cl.ControlIn(0xA1, 0x01, 0x0320, 3, 64);
        Check("0x20 firmware: 64 bytes", f20.Status == 0 && f20.Data.Length == 64,
              f20.Status == 0 ? $"{f20.Data.Length} bytes" : $"status {f20.Status}");
        Check("0x20 matches the driver's table byte for byte",
              f20.Status == 0 && f20.Data.SequenceEqual(expect20),
              f20.Status == 0
                  ? $"{f20.Data.Zip(expect20, (a, b) => a == b).Count(x => x)}/64 bytes equal"
                  : "read failed");
        {
            int fwType = f20.Status == 0 ? f20.Data[20] | (f20.Data[21] << 8) : -1;
            // dualsense-tester renders Factory Info only for fwType 2 or 3.
            // WinUHid's own default blob reports 4, which is why it is not
            // used verbatim even though it is equally real.
            Check("0x20 fwType satisfies the dualsense-tester render gate",
                  fwType == 2 || fwType == 3, $"fwType {fwType}");
        }
        WaitRing(outView, ref ringSeq, ringBuf, out _, out _, out _); // drain the 0x20 notify

        // Real values in 0x20 turn on dualsense-tester's traceability
        // branch, which opens by reading 0x22. Unserved IDs stall, so
        // without this the fix for F1 22 would have broken a consumer that
        // works today.
        var f22 = cl.ControlIn(0xA1, 0x01, 0x0322, 3, 64);
        Check("0x22 bt-patch: 64 bytes, RID echoed, does not stall",
              f22.Status == 0 && f22.Data.Length == 64 && f22.Data[0] == 0x22,
              f22.Status == 0 ? $"{f22.Data.Length} bytes, RID 0x{f22.Data[0]:X2}"
                              : $"status {f22.Status}");
        WaitRing(outView, ref ringSeq, ringBuf, out _, out _, out _); // drain the 0x22 notify

        // 0x12 is the DS4's pairing-info read and this persona is a DS5, so
        // the composite lane's job here is to prove the ID is wired at all
        // and returns the right shape. The DS4 lane is covered on the real
        // UMDF2 path by sony_feature_gate_check, which is where a DS4
        // profile actually exists.
        var f12 = cl.ControlIn(0xA1, 0x01, 0x0312, 3, 16);
        Check("0x12 pairing info: 16 bytes, RID echoed, non-zero MAC",
              f12.Status == 0 && f12.Data.Length == 16 && f12.Data[0] == 0x12
              && (f12.Data[1] | f12.Data[2] | f12.Data[3] | f12.Data[4] | f12.Data[5] | f12.Data[6]) != 0,
              f12.Status == 0
                  ? string.Join(":", f12.Data.Skip(1).Take(6).Select(b => b.ToString("X2")))
                  : $"status {f12.Status}");
        Check("0x12 MAC is locally administered",
              f12.Status == 0 && (f12.Data[1] & 0x02) != 0);
        WaitRing(outView, ref ringSeq, ringBuf, out _, out _, out _); // drain the 0x12 notify

        var fBad = cl.ControlIn(0xA1, 0x01, 0x03F7, 3, 64);
        Check("unknown feature ID stalls", fBad.Status == -32);

        // ── UAC controls: the real pad's wire values ────────────────────
        Console.WriteLine("\n-- UAC controls --");
        var volCur = cl.ControlIn(0xA1, 0x81, 0x0200, 0x0200, 2);
        var volMin = cl.ControlIn(0xA1, 0x82, 0x0200, 0x0200, 2);
        var volMax = cl.ControlIn(0xA1, 0x83, 0x0200, 0x0200, 2);
        var volRes = cl.ControlIn(0xA1, 0x84, 0x0200, 0x0200, 2);
        Check("speaker FU2 volume CUR/MIN/MAX/RES = pcap values",
              S16(volCur.Data) == -25600 && S16(volMin.Data) == -25600
              && S16(volMax.Data) == 0 && S16(volRes.Data) == 256,
              $"{S16(volCur.Data)}/{S16(volMin.Data)}/{S16(volMax.Data)}/{S16(volRes.Data)}");
        var micMax = cl.ControlIn(0xA1, 0x83, 0x0200, 0x0500, 2);
        var micRes = cl.ControlIn(0xA1, 0x84, 0x0200, 0x0500, 2);
        Check("mic FU5 MAX +48 dB, RES 0x007A", S16(micMax.Data) == 12288 && S16(micRes.Data) == 122);
        var mute = cl.ControlIn(0xA1, 0x81, 0x0100, 0x0200, 1);
        Check("mute GET_CUR = 0 (1 byte)", mute.Status == 0 && mute.Data.Length == 1 && mute.Data[0] == 0);

        var setVol = cl.ControlOut(0x21, 0x01, 0x0200, 0x0200, new byte[] { 0x00, 0xF0 }); // -16 dB
        Check("SET_CUR(volume) acks", setVol.Status == 0);
        // The audio event thread raises it. The wait ends once it has.
        Check("ControlChanged event carried the write",
              WaitUntil(() => { lock (controlWrites) return controlWrites.Any(w => w.Item1 == 2 && w.Item2 == 2 && w.Item3 == unchecked((short)0xF000)); }, Hang));
        var volCur2 = cl.ControlIn(0xA1, 0x81, 0x0200, 0x0200, 2);
        Check("GET_CUR reflects the write", S16(volCur2.Data) == unchecked((short)0xF000));

        var badUnit = cl.ControlIn(0xA1, 0x81, 0x0200, 0x0900, 2);
        Check("unknown unit stalls", badUnit.Status == -32);

        // ── Stream state without SET_INTERFACE ──────────────────────────
        // A 0.9.8.1 host controller driver beside an older usbip-win2
        // filter forwards the filter's SELECT_INTERFACE marker as an
        // ordinary request, so SET_INTERFACE never arrives while the audio
        // flows. The engine then reads stream state from the traffic: open
        // at the endpoint's alternate setting on the first transfer, closed
        // once the transfers stop. Nothing has sent SET_INTERFACE yet.
        Console.WriteLine("\n-- Stream state without SET_INTERFACE --");
        var altEvents = new List<(byte Iface, byte Alt)>();
        device.Audio.AltSettingChanged += (i, a) => { lock (altEvents) altEvents.Add((i, a)); };
        {
            const int P = 10, B = 392;
            uint o1 = cl.NextSeq(), o2 = cl.NextSeq();
            cl.SendSubmitIsoOut(o1, 1, new byte[P * B], P, B);
            cl.SendSubmitIsoOut(o2, 1, new byte[P * B], P, B);
            cl.ReadRet(o1);
            cl.ReadRet(o2);
            Check("speaker traffic with no SET_INTERFACE opens interface 1",
                  device.Audio.GetAltSetting(1) == 1 && LastAlt(altEvents, 1) == 1,
                  $"alt {device.Audio.GetAltSetting(1)}, last event {LastAlt(altEvents, 1)}");

            uint i1 = cl.NextSeq();
            cl.SendSubmitIsoIn(i1, 2, 10, 196);
            cl.ReadRet(i1);
            Check("microphone traffic with no SET_INTERFACE opens interface 2",
                  device.Audio.GetAltSetting(2) == 1 && LastAlt(altEvents, 2) == 1,
                  $"alt {device.Audio.GetAltSetting(2)}, last event {LastAlt(altEvents, 2)}");

            // Polled, so a late idle timer passes and a stream that never
            // closes still fails.
            WaitUntil(() => device.Audio.GetAltSetting(1) == 0 && device.Audio.GetAltSetting(2) == 0
                            && LastAlt(altEvents, 1) == 0 && LastAlt(altEvents, 2) == 0, Hang);
            Check("both streams close once their traffic stops",
                  device.Audio.GetAltSetting(1) == 0 && device.Audio.GetAltSetting(2) == 0
                  && LastAlt(altEvents, 1) == 0 && LastAlt(altEvents, 2) == 0,
                  $"alts {device.Audio.GetAltSetting(1)}/{device.Audio.GetAltSetting(2)}, " +
                  $"last events {LastAlt(altEvents, 1)}/{LastAlt(altEvents, 2)}");
        }
        // The PCM above is not part of the delivery check below.
        lock (outFrames) outFrames.SetLength(0);

        // ── Isochronous OUT: pacing + delivery ──────────────────────────
        Console.WriteLine("\n-- Isochronous OUT --");
        var setIf1 = cl.ControlOut(0x01, 0x0B, 0x0001, 0x0001, Array.Empty<byte>());
        Check("SET_INTERFACE(1, alt 1) acks", setIf1.Status == 0);

        const int Urbs = 8, Pkts = 10, PktBytes = 392;
        byte counter = 0;
        var sent = new MemoryStream();
        var isoSeqs = new uint[Urbs];
        var sw = Stopwatch.StartNew();
        for (int u = 0; u < Urbs; u++)
        {
            var payload = new byte[Pkts * PktBytes];
            for (int i = 0; i < payload.Length; i++) payload[i] = counter++;
            sent.Write(payload);
            isoSeqs[u] = cl.NextSeq();
            cl.SendSubmitIsoOut(isoSeqs[u], 1, payload, Pkts, PktBytes);
        }
        Client.Ret lastRet = default;
        foreach (var s in isoSeqs) lastRet = cl.ReadRet(s);
        sw.Stop();
        Check("all iso OUT URBs completed", true, $"{sw.ElapsedMilliseconds} ms for {Urbs * Pkts} ms of audio");
        Check("pacing held: 80 ms of audio took >= 55 ms", sw.ElapsedMilliseconds >= 55,
              $"{sw.ElapsedMilliseconds} ms");
        // A stall bound only: load can stretch the 80 ms, a stall never ends.
        Check("pacing does not stall: completed within the hang bound", sw.ElapsedMilliseconds < Hang,
              $"{sw.ElapsedMilliseconds} ms");
        Check("iso OUT RET: n packets echoed, actual = buffer length",
              lastRet.RawNumberOfPackets == Pkts && lastRet.ActualLength == Pkts * PktBytes);
        Check("iso OUT descriptors echo offsets with status 0",
              lastRet.IsoDescs != null && lastRet.IsoDescs.Length == Pkts
              && lastRet.IsoDescs[0].Offset == 0 && lastRet.IsoDescs[1].Offset == PktBytes
              && lastRet.IsoDescs.All(d => d.Status == 0));
        WaitUntil(() => { lock (outFrames) return outFrames.Length >= sent.Length; }, Hang);
        lock (outFrames)
            Check("OutFrames delivered every PCM byte in order",
                  outFrames.Length == sent.Length
                  && outFrames.ToArray().SequenceEqual(sent.ToArray()),
                  $"{outFrames.Length}/{sent.Length} bytes");

        // Interface 1 has now received a SET_INTERFACE, so idle time no
        // longer closes it. Only SET_INTERFACE does.
        Thread.Sleep(400);
        Check("after SET_INTERFACE, idle time leaves interface 1 open",
              device.Audio.GetAltSetting(1) == 1 && LastAlt(altEvents, 1) == 1,
              $"alt {device.Audio.GetAltSetting(1)}, last event {LastAlt(altEvents, 1)}");

        // ── Isochronous IN: microphone ──────────────────────────────────
        Console.WriteLine("\n-- Isochronous IN --");
        var setIf2 = cl.ControlOut(0x01, 0x0B, 0x0001, 0x0002, Array.Empty<byte>());
        Check("SET_INTERFACE(2, alt 1) acks", setIf2.Status == 0);

        var micPattern = MakePattern(8000, 0x40);
        device.Audio.SubmitMicSamples(micPattern);

        const int InPkts = 10, InCap = 196, InPer = 192;
        uint in1 = cl.NextSeq(), in2 = cl.NextSeq();
        cl.SendSubmitIsoIn(in1, 2, InPkts, InCap);
        cl.SendSubmitIsoIn(in2, 2, InPkts, InCap);
        var r1 = cl.ReadRet(in1);
        var r2 = cl.ReadRet(in2);
        Check("iso IN actual_length = 10 x 192 compacted", r1.ActualLength == InPkts * InPer
              && r1.Data.Length == InPkts * InPer);
        Check("iso IN descriptors: offsets echo submit capacities, actual 192 each",
              r1.IsoDescs != null && r1.IsoDescs.All(d => d.Actual == InPer)
              && r1.IsoDescs[1].Offset == InCap && r1.IsoDescs[9].Offset == 9 * InCap);
        Check("mic PCM streamed in order across URBs",
              r1.Data.SequenceEqual(micPattern.Take(1920))
              && r2.Data.SequenceEqual(micPattern.Skip(1920).Take(1920)));

        // Drain: a third URB underruns into silence.
        uint in3 = cl.NextSeq();
        cl.SendSubmitIsoIn(in3, 2, InPkts, InCap);
        var r3 = cl.ReadRet(in3);
        Check("mic underrun fills silence at full cadence",
              r3.ActualLength == InPkts * InPer
              && r3.Data.Skip(8000 - 3840).All(b => b == 0));

        // ── Microphone ring frame alignment (issue #41) ─────────────────
        // A submit larger than the free space is truncated. The ring
        // reserves one byte so full stays distinguishable from empty, so
        // the free count is 3 (mod 4) whenever the ring is frame-aligned.
        // Accepting that raw leaves a 3-byte fragment in a stream of
        // 4-byte frames, and every later sample reaches the host shifted
        // one byte: the low byte of each sample arrives as its high byte,
        // which is full-scale noise. The ring never re-aligns on its own,
        // so one truncating submit corrupts capture for the life of the
        // device. Reproduced by streaming across the truncation boundary
        // and checking that frame markers still land on frame boundaries.
        Console.WriteLine("\n-- Microphone ring alignment (issue #41) --");

        const int Frame = 4;                       // 2ch x 16-bit, per the profile
        const int MicRingBytes = InPer * 256;      // engine's ring for this persona

        // Drain what the underrun check left so the ring starts empty and
        // the byte stream below is entirely ours.
        while (device.Audio.MicBufferedBytes > 0)
        {
            uint dq = cl.NextSeq();
            cl.SendSubmitIsoIn(dq, 2, InPkts, InCap);
            cl.ReadRet(dq);
        }
        Check("ring empty before alignment test", device.Audio.MicBufferedBytes == 0);

        // Frame k carries its index in bytes 0-1 and a marker in bytes 2-3,
        // so any byte shift moves the marker off the frame boundary.
        static byte[] MarkedFrames(int frames, byte m0, byte m1)
        {
            var b = new byte[frames * Frame];
            for (int f = 0; f < frames; f++)
            {
                b[f * Frame] = (byte)f;
                b[f * Frame + 1] = (byte)(f >> 8);
                b[f * Frame + 2] = m0;
                b[f * Frame + 3] = m1;
            }
            return b;
        }

        // Oversized: forces the truncating path on an empty ring.
        var blockA = MarkedFrames(MicRingBytes / Frame + 1024, 0xA5, 0x5A);
        int acceptedA = device.Audio.SubmitMicSamples(blockA);
        Check("truncating submit accepts a whole number of frames",
              acceptedA % Frame == 0,
              $"accepted {acceptedA} of {blockA.Length} bytes, remainder {acceptedA % Frame}");
        Check("buffered count stays frame-aligned after truncation",
              device.Audio.MicBufferedBytes % Frame == 0,
              $"{device.Audio.MicBufferedBytes} bytes buffered");

        // Drain exactly what is buffered, in whole packets, so the stream
        // we validate never contains underrun silence.
        void DrainReal(MemoryStream sink, int bytes)
        {
            while (bytes >= InPer)
            {
                int pkts = Math.Min(32, bytes / InPer);
                uint dq = cl.NextSeq();
                cl.SendSubmitIsoIn(dq, 2, pkts, InCap);
                sink.Write(cl.ReadRet(dq).Data);
                bytes -= pkts * InPer;
            }
        }
        var micStream = new MemoryStream();

        // Leave a margin un-drained so the second block lands behind the
        // truncation point with the stream still flowing, exactly as a
        // live capture session does.
        DrainReal(micStream, device.Audio.MicBufferedBytes - 4096);
        var blockB = MarkedFrames(1920 / Frame, 0xC3, 0x3C);
        int acceptedB = device.Audio.SubmitMicSamples(blockB);
        Check("post-truncation submit is accepted whole",
              acceptedB == blockB.Length, $"{acceptedB}/{blockB.Length} bytes");
        DrainReal(micStream, device.Audio.MicBufferedBytes);

        var micGot = micStream.ToArray();
        int badFrame = -1, firstB = -1, strayA = -1;
        for (int off = 0; off + Frame <= micGot.Length; off += Frame)
        {
            bool isA = micGot[off + 2] == 0xA5 && micGot[off + 3] == 0x5A;
            bool isB = micGot[off + 2] == 0xC3 && micGot[off + 3] == 0x3C;
            if (!isA && !isB) { badFrame = off; break; }
            if (isB && firstB < 0) firstB = off;
            if (isA && firstB >= 0 && strayA < 0) strayA = off;
        }
        Check("no frame boundary shifts across a truncating submit",
              badFrame < 0,
              badFrame < 0
                  ? $"{micGot.Length / Frame} frames verified over {micGot.Length} bytes"
                  : $"first corrupt frame at byte {badFrame} of {micGot.Length}");
        Check("second block streams intact after the truncation point",
              firstB >= 0 && strayA < 0,
              firstB < 0 ? "second block never arrived" : $"starts at byte {firstB}");

        // The ring is a byte FIFO of one continuous stream, so frames may
        // legitimately span submits: a consumer feeding odd-sized chunks
        // is framed correctly by concatenation. Only a submit that drops
        // may floor, and it has to floor against the ring's fill rather
        // than its own length, or a mid-frame fill still leaves a
        // fragment behind.
        while (device.Audio.MicBufferedBytes > 0)
        {
            uint dq = cl.NextSeq();
            cl.SendSubmitIsoIn(dq, 2, InPkts, InCap);
            cl.ReadRet(dq);
        }

        var blockC = MarkedFrames(768, 0x99, 0x66);
        int fed = 0;
        while (fed < blockC.Length)
        {
            int chunk = Math.Min(102, blockC.Length - fed); // deliberately not a frame multiple
            int got = device.Audio.SubmitMicSamples(blockC.AsSpan(fed, chunk));
            if (got != chunk) break;
            fed += got;
        }
        Check("odd-sized chunks of a continuous stream are accepted whole",
              fed == blockC.Length && device.Audio.MicBufferedBytes == blockC.Length,
              $"fed {fed}/{blockC.Length}, buffered {device.Audio.MicBufferedBytes}");

        var sinkC = new MemoryStream();
        DrainReal(sinkC, device.Audio.MicBufferedBytes);
        var gotC = sinkC.ToArray();
        Check("frames spanning chunk boundaries arrive byte for byte",
              gotC.Length > 0 && blockC.Take(gotC.Length).SequenceEqual(gotC),
              $"{gotC.Length} bytes compared");

        // Truncate from a mid-frame fill: the cut has to land the ring on
        // a frame boundary, which means flooring against fill + free.
        while (device.Audio.MicBufferedBytes > 0)
        {
            uint dq = cl.NextSeq();
            cl.SendSubmitIsoIn(dq, 2, InPkts, InCap);
            cl.ReadRet(dq);
        }
        var blockD = MarkedFrames(MicRingBytes / Frame + 256, 0x11, 0xEE);
        device.Audio.SubmitMicSamples(blockD.AsSpan(0, 102)); // fill ends mid-frame
        device.Audio.SubmitMicSamples(blockD.AsSpan(102));    // truncates
        Check("truncation from a mid-frame fill lands the ring on a frame boundary",
              device.Audio.MicBufferedBytes % Frame == 0,
              $"{device.Audio.MicBufferedBytes} bytes buffered");

        var sinkD = new MemoryStream();
        DrainReal(sinkD, device.Audio.MicBufferedBytes);
        var gotD = sinkD.ToArray();
        Check("mid-frame truncation still delivers the stream prefix byte for byte",
              gotD.Length > MicRingBytes / 2 && blockD.Take(gotD.Length).SequenceEqual(gotD),
              $"{gotD.Length} bytes compared");

        // ── UNLINK ──────────────────────────────────────────────────────
        Console.WriteLine("\n-- CMD_UNLINK --");
        // The victim has to still be queued when the unlink lands. Due time
        // comes from the stream cursor, which only re-anchors to now once it
        // is more than 50 ms stale, so a short lead is not deterministic: if
        // the gap since the last submit sits anywhere under that window the
        // cursor is already behind, the URB is due in the past, and it
        // completes before the unlink arrives (RET_UNLINK 0, not -ECONNRESET).
        // A lead past the re-anchor window makes the queued state certain on
        // any machine. Observed on the Atom, where the checks above spend
        // tens of ms comparing 50 KB buffers before this runs.
        const int VictimPkts = 200; // ~200 ms, vs the 50 ms re-anchor window
        uint victim = cl.NextSeq();
        cl.SendSubmitIsoIn(victim, 2, VictimPkts, InCap);
        uint unlinkSeq = cl.NextSeq();
        cl.SendUnlink(unlinkSeq, victim);
        var ur = cl.ReadRetUnlink(unlinkSeq);
        bool victimAnswered = cl.SawSeqnum(victim);
        Check("queued victim unlinked with -ECONNRESET and no RET_SUBMIT",
              ur == -104 && !victimAnswered, $"status {ur}");

        uint unlink2 = cl.NextSeq();
        cl.SendUnlink(unlink2, isoSeqs[0]); // long since completed
        Check("unlink after completion answers 0", cl.ReadRetUnlink(unlink2) == 0);

        // Park the streams like usbaudio does when sessions close.
        cl.ControlOut(0x01, 0x0B, 0x0000, 0x0001, Array.Empty<byte>());
        cl.ControlOut(0x01, 0x0B, 0x0000, 0x0002, Array.Empty<byte>());
    }

    // ── Audio handlers that throw ───────────────────────────────────────

    /// <summary>Each public audio event, with a throwing handler ahead of a
    /// counting one, in a child process of its own. Before v2.0.0 a
    /// throwing handler on the connection thread closed the device's
    /// connection, and one on the audio pump or the thread pool ended the
    /// process.</summary>
    static void ThrowingHandlers()
    {
        Console.WriteLine("\n-- audio handlers that throw --");
        foreach (var (which, want, what) in new[]
        {
            ("control", 1, "a volume write"),
            ("speaker-streaming", 2, "the speaker stream opened and parked by SET_INTERFACE"),
            ("microphone-streaming", 2, "the microphone stream opened and parked by SET_INTERFACE"),
            ("frames", 2, "two isochronous OUT transfers of speaker audio"),
            ("inferred-close", 2, "a speaker stream opened by its traffic and closed when it stops"),
        })
        {
            var (code, line) = RunChild(which);
            Check($"{which}: behind a throwing handler, the next one sees {what}, and the connection still answers",
                  code == 0 && line == $"CHILD {which} seen={want} alive=True", $"exit {code}: {line}");
        }
    }

    // ── Audio handlers that block ───────────────────────────────────────

    /// <summary>A ControlChanged or StreamingChanged handler that blocks,
    /// on a DualSense composite's public audio object, while the host goes
    /// on: an interrupt IN read with a frame behind it must still be
    /// answered, as a real pad answers its IN endpoint whatever the host's
    /// audio app does. Before this release both events ran on the
    /// connection's one command thread, so the read was not even taken in
    /// until the handler returned. Then the order of the events the host
    /// caused.</summary>
    static void SlowHandlers(HMContext ctx, UsbipServer server)
    {
        Console.WriteLine("\n-- audio handlers that block --");
        const int SlowIndex = Index + 1;
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        foreach (string which in new[] { "ControlChanged", "StreamingChanged" })
        {
            var device = new UsbipEmulatedDevice(profile, SlowIndex);
            server.Register(device);
            var audio = new HMUsbAudio(profile, device);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            // The handler holds well past the answer's wait below, so only a
            // read taken in while it blocks counts as answered.
            void Block() { entered.Set(); release.Wait(2 * Hang); }
            if (which == "ControlChanged") audio.ControlChanged += (_, _) => Block();
            else audio.Output.StreamingChanged += (_, _) => Block();
            try
            {
                using var cl = new Client(server.Port);
                cl.SendImport(device.BusId);
                cl.ReadExactly(8);
                cl.ReadExactly(312);
                cl.ControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>());
                if (which == "ControlChanged")
                    cl.SendControlOut(0x21, 0x01, 0x0200, 0x0200, new byte[] { 0x00, 0xF0 });   // SET_CUR(volume)
                else
                    cl.SendControlOut(0x01, 0x0B, 0x0001, 0x0001, Array.Empty<byte>());         // SET_INTERFACE(1, alt 1)
                bool inHandler = entered.Wait(Hang);

                IntPtr view = SharedMemoryIO.EnsureInputMapping(SlowIndex);
                IntPtr evt = SharedMemoryIO.GetInputEvent(SlowIndex);
                uint seqNo = ReadSeqNoBase(view);
                uint read = cl.NextSeq();
                cl.SendSubmitInterruptIn(read, 4, 64);
                var data = MakePattern(63, 0x31);
                SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, data, 63, null);
                var reply = System.Threading.Tasks.Task.Run(() => cl.ReadRet(read));
                bool answered = reply.Wait(Hang);
                bool stillBlocked = !release.IsSet;
                release.Set();
                if (!answered) reply.Wait(5000);
                Check($"a blocking {which} handler leaves the device answering its interrupt IN endpoint",
                      inHandler && answered && stillBlocked,
                      inHandler ? (answered ? "answered while the handler blocked" : "no answer while the handler blocked") : "the handler never ran");
            }
            finally
            {
                release.Set();
                server.Unregister(device);
                device.Dispose();
                SharedMemoryIO.DestroyController(SlowIndex);
            }
        }

        // The order: the speaker stream opened, three volume writes, the
        // stream parked, as the host sent them.
        {
            var device = new UsbipEmulatedDevice(profile, SlowIndex);
            server.Register(device);
            var audio = new HMUsbAudio(profile, device);
            var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
            audio.ControlChanged += (_, e) => order.Enqueue($"volume {e.RawValue}");
            audio.Output.StreamingChanged += (_, on) => order.Enqueue(on ? "open" : "parked");
            try
            {
                using var cl = new Client(server.Port);
                cl.SendImport(device.BusId);
                cl.ReadExactly(8);
                cl.ReadExactly(312);
                cl.ControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>());
                cl.ControlOut(0x01, 0x0B, 0x0001, 0x0001, Array.Empty<byte>());
                foreach (short v in new short[] { -4096, -2048, -1024 })
                    cl.ControlOut(0x21, 0x01, 0x0200, 0x0200, new[] { (byte)v, (byte)(v >> 8) });
                cl.ControlOut(0x01, 0x0B, 0x0000, 0x0001, Array.Empty<byte>());
                // A write still waiting when the next one to the same control
                // arrives gives way to it, so the volumes raised are the
                // sent ones in order, ending with the last.
                WaitUntil(() => order.Count >= 3 && order.Last() == "parked", Hang);
                var seen = order.ToArray();
                var volumes = seen.Skip(1).Take(seen.Length - 2).ToArray();
                var sent = new[] { "volume -4096", "volume -2048", "volume -1024" };
                bool inOrder = seen.Length >= 3 && seen[0] == "open" && seen[^1] == "parked"
                    && volumes.Length > 0 && volumes[^1] == sent[^1]
                    && volumes.Select(v => Array.IndexOf(sent, v)).Zip(volumes.Select(v => Array.IndexOf(sent, v)).Skip(1), (a, b) => a >= 0 && a < b).All(x => x)
                    && volumes.All(v => Array.IndexOf(sent, v) >= 0);
                Check("the audio events arrive in the order the host caused them", inOrder, string.Join(", ", seen));
            }
            finally
            {
                server.Unregister(device);
                device.Dispose();
                SharedMemoryIO.DestroyController(SlowIndex);
            }
        }
    }

    // ── The import's port lookup ────────────────────────────────────────

    /// <summary>Two failed lookups of the host controller's interface path
    /// in a row, then a real one: the server's port lookup must find the
    /// path again. Before this release the first failure stopped every
    /// later lookup for that import.</summary>
    static void PortLookup()
    {
        Console.WriteLine("\n-- the import's port lookup --");
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;
        var pathField = typeof(VhciClient).GetField("s_path", Statics)!;
        var failField = typeof(VhciClient.LocationLookup).GetField("FailPathLookups", Statics)!;
        object? kept = pathField.GetValue(null);
        var lookup = new VhciClient.LocationLookup("127.0.0.1", 1, "9-9");
        pathField.SetValue(null, null);
        failField.SetValue(null, 2);
        int calls = 0;
        while (calls < 40 && pathField.GetValue(null) == null)
        {
            lookup.Find();
            calls++;
        }
        bool found = pathField.GetValue(null) != null;
        failField.SetValue(null, 0);
        if (!found) pathField.SetValue(null, kept);
        Check("after two failed interface lookups the port lookup finds the interface again",
              found, found ? $"after {calls} lookups" : "not after 40 lookups");
    }

    // ── Offline: the audio engine's event thread ────────────────────────

    /// <summary>The audio engine on its own, with no device, no section
    /// and no connection: its thread and handles, and its events, driven
    /// the way the connection's reader drives them.</summary>
    static int Offline()
    {
        Console.WriteLine("=== USB/IP audio engine, owner lookup and location match, offline ===");
        SetErrorMode(QuietErrorMode);
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var ds = ctx.GetProfile("dualsense-composite")!.Inner;
        var gc = ctx.GetProfile("gamecube-adapter")!.Inner;
        var wakeField = typeof(UsbAudioEngine).GetField("_eventWake", Private)!;
        var eventsThread = typeof(UsbAudioEngine).GetField("_eventThread", Private)!;
        UsbAudioEngine.IsoCompletion none = (_, _, _) => { };
        static byte[] Volume(short v) => new[] { (byte)v, (byte)(v >> 8) };

        // An engine that never raised an event, as on every USB/IP device
        // with no audio, closes its wake event at Dispose.
        {
            var engine = new UsbAudioEngine(gc, none);
            var wake = (AutoResetEvent)wakeField.GetValue(engine)!;
            engine.Dispose();
            Check("an audio engine with no event closes its wake event at Dispose",
                  wake.SafeWaitHandle.IsClosed, wake.SafeWaitHandle.IsClosed ? "" : "still open");
        }

        // A stream opened and parked while a handler blocks, then Dispose:
        // IsStreaming follows the host at once, and both changes are still
        // raised once the handler returns.
        {
            var engine = new UsbAudioEngine(ds, none);
            var audio = new HMUsbAudio(ds, engine);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var raised = new System.Collections.Concurrent.ConcurrentQueue<string>();
            audio.ControlChanged += (_, e) =>
            {
                raised.Enqueue($"volume {e.RawValue}");
                if (!entered.IsSet) { entered.Set(); release.Wait(Hang); }
            };
            audio.Output.StreamingChanged += (_, on) => raised.Enqueue(on ? "open" : "parked");
            engine.HandleUacSet(2, 2, 0, Volume(-4096));
            bool blocked = entered.Wait(Hang);
            engine.SetAltSetting(1, 1);
            bool streaming = audio.Output.IsStreaming;
            engine.SetAltSetting(1, 0);
            bool parked = !audio.Output.IsStreaming;
            Check("IsStreaming follows the host at once while an audio handler blocks",
                  blocked && streaming && parked, $"handler blocking {blocked}, open {streaming}, parked {parked}");
            engine.Dispose();
            release.Set();
            var thread = (Thread?)eventsThread.GetValue(engine);
            bool ended = WaitUntil(() => thread == null || !thread.IsAlive, Hang);
            var wake = (AutoResetEvent)wakeField.GetValue(engine)!;
            string got = string.Join(", ", raised);
            Check("changes queued before Dispose are still raised, then the event thread ends and closes its wake event",
                  got == "volume -4096, open, parked" && ended && wake.SafeWaitHandle.IsClosed,
                  $"{got}, ended {ended}, wake closed {wake.SafeWaitHandle.IsClosed}");
        }

        // A burst of volume writes while a handler blocks, with the speaker
        // stream opened and parked inside it: one write per control waits,
        // the newest, and the stream changes keep their order.
        {
            var engine = new UsbAudioEngine(ds, none);
            var audio = new HMUsbAudio(ds, engine);
            var queue = (System.Collections.ICollection)typeof(UsbAudioEngine).GetField("_events", Private)!.GetValue(engine)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var raised = new System.Collections.Concurrent.ConcurrentQueue<string>();
            audio.ControlChanged += (_, e) =>
            {
                raised.Enqueue($"volume {e.RawValue}");
                if (!entered.IsSet) { entered.Set(); release.Wait(Hang); }
            };
            audio.Output.StreamingChanged += (_, on) => raised.Enqueue(on ? "open" : "parked");
            engine.HandleUacSet(2, 2, 0, Volume(-100));
            bool blocked = entered.Wait(Hang);
            int most = 0;
            for (short v = -99; v <= -50; v++)
            {
                if (v == -80) engine.SetAltSetting(1, 1);
                if (v == -60) engine.SetAltSetting(1, 0);
                engine.HandleUacSet(2, 2, 0, Volume(v));
                lock (engine.GetType().GetField("_lock", Private)!.GetValue(engine)!) most = Math.Max(most, queue.Count);
            }
            release.Set();
            WaitUntil(() => raised.Count >= 4, Hang);
            Thread.Sleep(100);
            string got = string.Join(", ", raised);
            Check("a burst of control writes while a handler blocks waits as the newest per control, stream changes in order",
                  blocked && got == "volume -100, open, parked, volume -50" && most <= 3,
                  $"{got}, at most {most} waiting");
            engine.Dispose();
        }

        // A thread that does not start. Thread.Start throws only when the
        // process is out of threads or memory, so the engine's own seam
        // fails it.
        var failStarts = typeof(UsbAudioEngine).GetField("FailThreadStarts", BindingFlags.Static | BindingFlags.NonPublic)!;

        // The pump: the constructor throws, and the device never gets an
        // engine to dispose, so the constructor closes every handle it
        // opened.
        {
            var engine = (UsbAudioEngine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UsbAudioEngine));
            var ctor = typeof(UsbAudioEngine).GetConstructor(new[] { typeof(ControllerProfile), typeof(UsbAudioEngine.IsoCompletion) })!;
            failStarts.SetValue(null, 1);
            string threw = "nothing";
            try { ctor.Invoke(engine, new object[] { ds, none }); }
            catch (TargetInvocationException e) { threw = e.InnerException?.GetType().Name ?? "?"; }
            finally { failStarts.SetValue(null, 0); }
            var pumpWake = (AutoResetEvent?)typeof(UsbAudioEngine).GetField("_wake", Private)!.GetValue(engine);
            var wake = (AutoResetEvent?)wakeField.GetValue(engine);
            var timer = (IntPtr)typeof(UsbAudioEngine).GetField("_timer", Private)!.GetValue(engine)!;
            bool pumpWakeClosed = pumpWake?.SafeWaitHandle.IsClosed ?? false;
            bool wakeClosed = wake?.SafeWaitHandle.IsClosed ?? false;
            bool timerClosed = timer != IntPtr.Zero && !GetHandleInformation(timer, out _);
            Check("an audio engine whose pump does not start throws and closes its two events and its timer",
                  threw == nameof(OutOfMemoryException) && pumpWakeClosed && wakeClosed && timerClosed,
                  $"threw {threw}, pump wake closed {pumpWakeClosed}, event wake closed {wakeClosed}, timer closed {timerClosed}");
        }

        // The event thread, at the first event: the connection's reader
        // gets no exception, the change waits, and the next change starts
        // the thread.
        {
            var engine = new UsbAudioEngine(ds, none);
            var raised = new System.Collections.Concurrent.ConcurrentQueue<short>();
            engine.ControlChanged += (_, _, _, v) => raised.Enqueue(v);
            failStarts.SetValue(null, 1);
            string threw = "nothing";
            try { engine.HandleUacSet(2, 2, 0, Volume(-4096)); }
            catch (Exception e) { threw = e.GetType().Name; }
            finally { failStarts.SetValue(null, 0); }
            bool noThread = eventsThread.GetValue(engine) == null;
            engine.HandleUacSet(2, 2, 0, Volume(-2048));
            bool raisedLater = WaitUntil(() => raised.Contains((short)-2048), Hang);
            var thread = (Thread?)eventsThread.GetValue(engine);
            engine.Dispose();
            bool ended = WaitUntil(() => thread != null && !thread.IsAlive, Hang);
            var wake = (AutoResetEvent)wakeField.GetValue(engine)!;
            Check("an event thread that does not start throws nothing to the connection's reader, and the next change starts it",
                  threw == "nothing" && noThread && raisedLater && string.Join(",", raised) == "-2048" && ended && wake.SafeWaitHandle.IsClosed,
                  $"threw {threw}, raised [{string.Join(", ", raised)}], ended {ended}, wake closed {wake.SafeWaitHandle.IsClosed}");
        }

        // The same failure, then Dispose with no other change: Dispose
        // starts the thread, which raises the change and closes its wake
        // event.
        {
            var engine = new UsbAudioEngine(ds, none);
            var raised = new System.Collections.Concurrent.ConcurrentQueue<short>();
            engine.ControlChanged += (_, _, _, v) => raised.Enqueue(v);
            failStarts.SetValue(null, 1);
            string threw = "nothing";
            try { engine.HandleUacSet(2, 2, 0, Volume(-4096)); }
            catch (Exception e) { threw = e.GetType().Name; }
            finally { failStarts.SetValue(null, 0); }
            var wake = (AutoResetEvent)wakeField.GetValue(engine)!;
            engine.Dispose();
            var thread = (Thread?)eventsThread.GetValue(engine);
            bool ended = WaitUntil(() => thread != null && !thread.IsAlive, Hang);
            Check("a change queued while the event thread could not start is raised at Dispose, which starts the thread",
                  threw == "nothing" && string.Join(",", raised) == "-4096" && ended && wake.SafeWaitHandle.IsClosed,
                  $"threw {threw}, raised [{string.Join(", ", raised)}], thread ended {ended}, wake closed {wake.SafeWaitHandle.IsClosed}");
        }

        // The start fails at the change and again at Dispose: nothing is
        // raised, nothing is thrown, and Dispose closes the wake event and
        // drops what waits.
        {
            var engine = new UsbAudioEngine(ds, none);
            var raised = new System.Collections.Concurrent.ConcurrentQueue<short>();
            engine.ControlChanged += (_, _, _, v) => raised.Enqueue(v);
            var queue = (System.Collections.ICollection)typeof(UsbAudioEngine).GetField("_events", Private)!.GetValue(engine)!;
            string diagLog = Path.Combine(Path.GetTempPath(), "HIDMaestro", "teardown_diag.log");
            long diagFrom = File.Exists(diagLog) ? new FileInfo(diagLog).Length : 0;
            failStarts.SetValue(null, 2);
            string threw = "nothing";
            try { engine.HandleUacSet(2, 2, 0, Volume(-4096)); engine.Dispose(); }
            catch (Exception e) { threw = e.GetType().Name; }
            finally { failStarts.SetValue(null, 0); }
            var wake = (AutoResetEvent)wakeField.GetValue(engine)!;
            Thread.Sleep(100);
            Check("an event thread that cannot start at the change or at Dispose: nothing thrown, the wait dropped, the wake event closed",
                  threw == "nothing" && raised.IsEmpty && queue.Count == 0 && eventsThread.GetValue(engine) == null && wake.SafeWaitHandle.IsClosed,
                  $"threw {threw}, raised {raised.Count}, waiting {queue.Count}, wake closed {wake.SafeWaitHandle.IsClosed}");
            // Under HIDMAESTRO_DIAG=1 the diagnostic log names both failures,
            // in the lines this run added.
            if (DeviceOrchestrator.DiagEnabled)
            {
                string log = "";
                try
                {
                    using var f = new FileStream(diagLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    f.Seek(Math.Min(diagFrom, f.Length), SeekOrigin.Begin);
                    log = new StreamReader(f).ReadToEnd();
                }
                catch (IOException) { }
                bool started = log.Contains("audio event thread did not start, 1 change(s) waiting: System.OutOfMemoryException");
                bool dropped = log.Contains("audio engine dropped 1 change(s) at Dispose");
                Check("the diagnostic log records the failed start and the dropped change",
                      started && dropped, $"failed start {started}, dropped {dropped}");
            }
        }

        // The engine counts its event thread as its own, which a context's
        // Dispose asks to keep a handler's controller on the handler's
        // thread.
        {
            var engine = new UsbAudioEngine(ds, none);
            bool onEventThread = false;
            using var seen = new ManualResetEventSlim();
            engine.ControlChanged += (_, _, _, _) => { onEventThread = engine.RunsOnCurrentThread; seen.Set(); };
            engine.HandleUacSet(2, 2, 0, Volume(-1024));
            bool raised = seen.Wait(Hang);
            bool onCaller = engine.RunsOnCurrentThread;
            engine.Dispose();
            Check("the audio engine counts its event thread as its own, and not the caller's",
                  raised && onEventThread && !onCaller,
                  $"raised {raised}, event thread {onEventThread}, caller {onCaller}");
        }

        InputPumpWakeOffline();

        Console.WriteLine("\n-- the protocol GET_REPORT section's copy of gamecube-adapter --");
        ProtocolInputOffline(ctx);

        // The server's connection owner lookup and the location match the
        // sweeps detach by, which need no device either (Program.Lookup.cs).
        OwnerLookup();
        LocationMatch();

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The device's input pump and Dispose, with no section: a
    /// device object built without its constructor, holding only the locks
    /// and an unnamed auto-reset event in place of the named input event,
    /// and its own input pump on that event. No frame ever arrives, so the
    /// pump sits in its 500 ms wait, as it does between frames. Dispose
    /// comes 20 to 320 ms into the wait, before the pump's first timeout,
    /// so the pump never reaches a section read. Dispose's time is the
    /// input pump's part of every USB/IP Dispose: until Dispose woke the
    /// pump, the rest of the wait.
    ///
    /// <para>Two things it cannot reach. The unnamed event has full access,
    /// while the device opens the named one with SYNCHRONIZE and
    /// EVENT_MODIFY_STATE, so a change to that access mask passes here and
    /// fails only the live dispose timing. And the pump can close its event
    /// only after _stop, which Dispose sets in the same locked block as the
    /// wake, so the _inputWakeClosed guard against setting a closed handle
    /// never decides anything here: the check passes with it removed.</para></summary>
    static void InputPumpWakeOffline()
    {
        var t = typeof(UsbipEmulatedDevice);
        var pumps = new Thread[30];
        var events = new IntPtr[30];
        // A second handle to each event, kept by the probe. Once the pump
        // closes its own, Windows can give that value to the next handle any
        // thread here creates, and on a slow machine the loop below is still
        // creating events and threads when the first Disposes run, so the
        // value alone cannot tell a closed handle from a reused one.
        var dups = new IntPtr[30];
        var ended = new bool[30];
        double[] ms = DisposeTogether(30, new Random(43), 20, 300, i =>
        {
            var dev = (UsbipEmulatedDevice)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            t.GetField("_lifetimeLock", Private)!.SetValue(dev, new object());
            t.GetField("_publishLock", Private)!.SetValue(dev, new object());
            IntPtr ev = CreateEventW(IntPtr.Zero, false, false, null);
            DuplicateHandle(GetCurrentProcess(), ev, GetCurrentProcess(), out dups[i], 0, false, DuplicateSameAccess);
            t.GetField("_inputWaitEvent", Private)!.SetValue(dev, ev);
            var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), dev, t.GetMethod("InputPumpLoop", Private)!);
            var pump = new Thread(loop) { IsBackground = true, Name = "S43 offline input pump" };
            t.GetField("_inputThread", Private)!.SetValue(dev, pump);
            pumps[i] = pump;
            events[i] = ev;
            pump.Start();
            return dev;
        },
        // The pump closes the event as it ends. The join bounds a pump that
        // never ends. The pump's handle is still open only when its value
        // still names the same event as the kept one.
        i =>
        {
            pumps[i].Join(Hang);
            ended[i] = dups[i] != IntPtr.Zero && !pumps[i].IsAlive
                       && !(GetHandleInformation(events[i], out _) && CompareObjectHandles(events[i], dups[i]));
            if (dups[i] != IntPtr.Zero) CloseHandle(dups[i]);
        });
        bool closed = Array.TrueForAll(ended, e => e);
        int lost = Array.FindAll(ms, double.IsNaN).Length;
        Array.Sort(ms);
        // Nearest rank: the 15th and the 29th of 30. A woken pump ends in
        // about a millisecond, and one that waits out its 500 ms wait from
        // 20 to 320 ms in takes 180 ms or more at every sample. The median
        // tells them apart, and load that slows a few Disposes does not
        // move it.
        double p50 = ms[14], p95 = ms[28], max = ms[29];
        Check("Dispose wakes the input pump instead of waiting out its 500 ms wait (median under 100 ms over 30)",
              p50 < 100 && closed && lost == 0,
              $"p50 {p50:F1} ms, p95 {p95:F1} ms, max {max:F1} ms, pump ended and event closed {closed}" + (lost > 0 ? $", {lost} Dispose(s) threw or hung" : ""));
    }

    /// <summary>Dispose times of <paramref name="count"/> devices that run at
    /// once. This thread makes each in turn with <paramref name="make"/> and
    /// starts a thread of its own for it, which sleeps
    /// <paramref name="fromMs"/> plus a random part of
    /// <paramref name="spanMs"/> from that moment, times the device's
    /// Dispose, and then runs <paramref name="after"/>. The devices share no
    /// lock, event or section, so each Dispose waits for its own input pump
    /// alone, as when the devices ran one after another, and the whole run
    /// takes one offset instead of the sum of them. NaN for a Dispose that
    /// threw or did not return within the hang bound.</summary>
    static double[] DisposeTogether(int count, Random rnd, int fromMs, int spanMs, Func<int, IDisposable> make, Action<int>? after = null)
    {
        var ms = new double[count];
        Array.Fill(ms, double.NaN);
        var threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int n = i;
            var dev = make(n);
            int offset = fromMs + rnd.Next(spanMs);
            var start = Stopwatch.StartNew();
            threads[n] = new Thread(() =>
            {
                int left = offset - (int)start.ElapsedMilliseconds;
                if (left > 0) Thread.Sleep(left);
                var sw = Stopwatch.StartNew();
                try { dev.Dispose(); ms[n] = sw.Elapsed.TotalMilliseconds; }
                catch (Exception) { }
                after?.Invoke(n);
            }) { IsBackground = true, Name = "S43 dispose " + n };
            threads[n].Start();
        }
        foreach (var th in threads) th.Join(Hang);
        // A copy, so a Dispose that returns after its bound cannot change
        // what the caller sorts.
        return (double[])ms.Clone();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
                                       uint access, bool inherit, uint options);

    const uint DuplicateSameAccess = 2; // DUPLICATE_SAME_ACCESS

    // Windows 10 1607 and later.
    [System.Runtime.InteropServices.DllImport("kernelbase.dll")]
    static extern bool CompareObjectHandles(IntPtr first, IntPtr second);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static (int Code, string Line) RunChild(string which) => RunChild($"--throwing {which}", null);

    static (int Code, string Line) RunChild(string arguments, (string Name, string Value)? env)
    {
        // The child inherits this error mode: a crash or a loader failure
        // there ends in an exit code, never in a dialog on the desktop.
        SetErrorMode(QuietErrorMode);
        var psi = new ProcessStartInfo(Environment.ProcessPath!, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (env is { } e) psi.Environment[e.Name] = e.Value;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(2 * Hang))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.WaitForExit();
        }
        var lines = (stdout.Result + "\n" + stderr.Result).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return (p.ExitCode, lines.LastOrDefault(l => l.StartsWith("CHILD ", StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? "no output");
    }

    /// <summary>Isochronous URBs as long as the protocol allows. usbip-win2
    /// caps number_of_packets at 1024 (include/usbip/proto.h, max_iso_packets,
    /// checked in drivers/ude/device_ioctl.cpp), and a high-speed endpoint
    /// may take that many in one URB, so the server reads 16 KB of
    /// descriptors and OUT data of any length. Each case runs on a
    /// connection of its own, and the connection must still answer after
    /// it.</summary>
    static void LongIsoUrbs(HMContext ctx, UsbipServer server)
    {
        Console.WriteLine("\n-- Isochronous URBs past 256 packets --");
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;

        void Case(string name, Action<Client, UsbipEmulatedDevice> run)
        {
            var device = new UsbipEmulatedDevice(profile, Index);
            server.Register(device);
            try
            {
                using var cl = new Client(server.Port);
                cl.SendImport(device.BusId);
                cl.ReadExactly(8);
                cl.ReadExactly(312);
                try
                {
                    run(cl, device);
                    var st = cl.ControlIn(0x80, 0x00, 0, 0, 2);
                    Check($"{name}: the connection answers GET_STATUS after it", st.Status == 0 && st.Data.Length == 2,
                          $"status {st.Status}");
                }
                catch (Exception e)
                {
                    Check($"{name}: the device answered", false, $"{e.GetType().Name}: {e.Message}");
                }
            }
            finally
            {
                server.Unregister(device);
                device.Dispose();
                SharedMemoryIO.DestroyController(Index);
            }
        }

        // 1024 packets, 16 KB of descriptors: the microphone, with PCM fed
        // for the first packets and silence after.
        Case("an IN URB of 1024 packets", (cl, device) =>
        {
            const int Pkts = 1024, Cap = 196, Per = 192;
            var pcm = MakePattern(Per * 8, 0x21);
            device.Audio.SubmitMicSamples(pcm);
            uint seq = cl.NextSeq();
            cl.SendSubmitIsoIn(seq, 2, Pkts, Cap);
            var r = cl.ReadRet(seq);
            Check("an IN URB of 1024 packets: all 1024 descriptors back, offsets echoed, 192 bytes each",
                  r.RawNumberOfPackets == Pkts && r.IsoDescs != null && r.IsoDescs.Length == Pkts
                  && r.IsoDescs.Select((d, i) => d.Offset == (uint)(i * Cap) && d.Actual == Per).All(b => b),
                  $"{r.RawNumberOfPackets} packets, actual {r.ActualLength}");
            Check("an IN URB of 1024 packets: the fed PCM first, then silence",
                  r.ActualLength == Pkts * Per && r.Data.Length == Pkts * Per
                  && r.Data.AsSpan(0, pcm.Length).SequenceEqual(pcm) && r.Data.Skip(pcm.Length).All(b => b == 0),
                  $"{r.Data.Length} bytes");
        });

        // More than 4 KB of data and more than 256 packets: the speaker.
        Case("an OUT URB of 300 packets and 117,600 bytes", (cl, device) =>
        {
            const int Pkts = 300, Per = 392;
            var heard = new MemoryStream();
            device.Audio.OutFrames += f => { lock (heard) heard.Write(f.Span); };
            var payload = MakePattern(Pkts * Per, 0x52);
            uint seq = cl.NextSeq();
            cl.SendSubmitIsoOut(seq, 1, payload, Pkts, Per);
            var r = cl.ReadRet(seq);
            Check("an OUT URB of 300 packets and 117,600 bytes: answered with 300 descriptors, actual the buffer length",
                  r.Status == 0 && r.RawNumberOfPackets == Pkts && r.ActualLength == payload.Length
                  && r.IsoDescs != null && r.IsoDescs.Length == Pkts && r.IsoDescs[Pkts - 1].Offset == (uint)((Pkts - 1) * Per),
                  $"status {r.Status}, {r.RawNumberOfPackets} packets, actual {r.ActualLength}");
            WaitUntil(() => { lock (heard) return heard.Length >= payload.Length; }, Hang);
            lock (heard)
                Check("an OUT URB of 300 packets and 117,600 bytes: OutFrames carries every byte in order",
                      heard.ToArray().SequenceEqual(payload), $"{heard.Length}/{payload.Length} bytes");
        });

        // More than 4 KB of data with few packets.
        Case("an OUT URB of 20 packets and 7,840 bytes", (cl, device) =>
        {
            const int Pkts = 20, Per = 392;
            var heard = new MemoryStream();
            device.Audio.OutFrames += f => { lock (heard) heard.Write(f.Span); };
            var payload = MakePattern(Pkts * Per, 0x63);
            uint seq = cl.NextSeq();
            cl.SendSubmitIsoOut(seq, 1, payload, Pkts, Per);
            var r = cl.ReadRet(seq);
            Check("an OUT URB of 20 packets and 7,840 bytes: answered, actual the buffer length",
                  r.Status == 0 && r.RawNumberOfPackets == Pkts && r.ActualLength == payload.Length,
                  $"status {r.Status}, actual {r.ActualLength}");
            WaitUntil(() => { lock (heard) return heard.Length >= payload.Length; }, Hang);
            lock (heard)
                Check("an OUT URB of 20 packets and 7,840 bytes: OutFrames carries every byte in order",
                      heard.ToArray().SequenceEqual(payload), $"{heard.Length}/{payload.Length} bytes");
        });
    }

    /// <summary>GET_REPORT(Input) on a persona whose descriptor declares
    /// several input reports, the Steam Controller (2026): the last report
    /// of the ID asked for, a declared report not yet sent as its ID and
    /// zeros at its declared length, a report kept after another ID's
    /// frame, and a stall for an ID the descriptor does not
    /// declare.</summary>
    static void DeclaredInputReports(HMContext ctx, UsbipServer server)
    {
        Console.WriteLine("\n-- GET_REPORT(Input) by the descriptor's report IDs --");
        var profile = ctx.GetProfile("steam-controller-2")!.Inner;
        var builder = profile.GetOrBuildReportBuilder();
        var declared = builder.InputReports;
        Check("the descriptor declares several input reports", declared.Count > 1,
              string.Join(" ", declared.Select(r => $"{r.Id:X2}:{r.ByteSize}")));
        var device = new UsbipEmulatedDevice(profile, Index);
        server.Register(device);
        try
        {
            using var cl = new Client(server.Port);
            cl.SendImport(device.BusId);
            cl.ReadExactly(8);
            cl.ReadExactly(312);
            ushort iface = device.Descriptors.HidInterfaceNumber;

            IntPtr view = SharedMemoryIO.EnsureInputMapping(Index);
            IntPtr evt = SharedMemoryIO.GetInputEvent(Index);
            uint seqNo = ReadSeqNoBase(view);
            int bodyLength = builder.InputReportByteSize - 1;
            var body = MakePattern(bodyLength, 0x31);
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, body, bodyLength, null);

            // The input pump takes each frame in on its own thread, so each
            // report is asked for until it answers, which a late pump passes
            // and a wrong answer still fails at the bound.
            byte sent = builder.InputReportId;
            Client.Ret got = default;
            bool SentAnswered(Client.Ret r) => r.Status == 0 && r.Data.Length > 1 && r.Data[0] == sent && r.Data.Skip(1).Take(bodyLength).SequenceEqual(body);
            WaitUntil(() => SentAnswered(got = cl.ControlIn(0xA1, 0x01, (ushort)(0x0100 | sent), iface, 64)), Hang);
            Check($"GET_REPORT(Input 0x{sent:X2}), the report just sent, answers it",
                  SentAnswered(got),
                  $"status {got.Status}, {got.Data?.Length ?? 0} bytes");
            // A second declared report, sent whole as an extended frame,
            // takes its own slot: the first keeps its bytes, and each
            // answers with its own.
            var (secondId, secondSize) = declared.First(r => r.Id != sent && r.ByteSize > 1);
            var second = MakePattern(secondSize, 0x5A);
            second[0] = secondId;
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, body, bodyLength, null,
                                           extendedData: second, extendedLen: second.Length);
            // The second report first, so the first is read only once the
            // pump has taken the second frame in.
            int ownWant = Math.Min(second.Length, 64);
            Client.Ret own = default;
            bool OwnAnswered(Client.Ret r) => r.Status == 0 && r.Data.Length == ownWant && r.Data.SequenceEqual(second.Take(ownWant));
            WaitUntil(() => OwnAnswered(own = cl.ControlIn(0xA1, 0x01, (ushort)(0x0100 | secondId), iface, 64)), Hang);
            var keep = cl.ControlIn(0xA1, 0x01, (ushort)(0x0100 | sent), iface, 64);
            Check($"after a frame of report 0x{secondId:X2}, GET_REPORT(Input 0x{sent:X2}) still answers 0x{sent:X2}'s bytes",
                  SentAnswered(keep),
                  $"status {keep.Status}, {keep.Data.Length} bytes, first {(keep.Data.Length > 0 ? keep.Data[0] : 0):X2}");
            Check($"GET_REPORT(Input 0x{secondId:X2}) answers its own frame",
                  OwnAnswered(own),
                  $"status {own.Status}, {own.Data?.Length ?? 0} bytes");

            var unsent = declared.Where(r => r.Id != sent && r.Id != secondId).ToList();
            var wrong = new List<string>();
            foreach (var (id, size) in unsent)
            {
                var r = cl.ControlIn(0xA1, 0x01, (ushort)(0x0100 | id), iface, 64);
                int want = Math.Min(size, 64);
                if (r.Status != 0 || r.Data.Length != want || r.Data[0] != id || r.Data.Skip(1).Any(b => b != 0))
                    wrong.Add($"{id:X2}: status {r.Status}, {r.Data.Length} bytes");
            }
            Check($"each of the {unsent.Count} declared reports not sent answers its ID and zeros at its length",
                  unsent.Count > 0 && wrong.Count == 0, string.Join(", ", wrong));
            var none = cl.ControlIn(0xA1, 0x01, 0x0107, iface, 64);
            Check("GET_REPORT(Input 0x07), which the descriptor does not declare, stalls",
                  none.Status == -32 && none.Data.Length == 0, $"status {none.Status}, {none.Data.Length} bytes");
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(Index);
        }
    }

    /// <summary>GET_REPORT(Input) on a protocol device with getReport
    /// "input", a copy of gamecube-adapter: its own report from the engine,
    /// another declared report as its ID and zeros, and a stall for an ID
    /// the descriptor does not declare, the rule every other persona
    /// follows.</summary>
    static void ProtocolInputGetReport(HMContext ctx, UsbipServer server)
    {
        Console.WriteLine("\n-- GET_REPORT(Input) on a protocol device, getReport \"input\" --");
        string dir = Path.Combine(Path.GetTempPath(), "hm-s43-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        UsbipEmulatedDevice? device = null;
        try
        {
            var profile = ProtocolInputCopy(ctx, dir, out string problem);
            if (profile == null)
            {
                Check("a copy of gamecube-adapter with getReport \"input\" loads", false, problem);
                return;
            }
            var declared = profile.GetOrBuildReportBuilder().InputReports;
            if (!declared.Any(r => r.Id != 0x21))
            {
                Check("the copy's descriptor declares an input report besides 0x21", false,
                      string.Join(" ", declared.Select(r => $"{r.Id:X2}:{r.ByteSize}")));
                return;
            }
            device = new UsbipEmulatedDevice(profile, Index);
            server.Register(device);
            using var cl = new Client(server.Port);
            cl.SendImport(device.BusId);
            cl.ReadExactly(8);
            cl.ReadExactly(312);
            ushort iface = device.Descriptors.HidInterfaceNumber;

            var own = cl.ControlIn(0xA1, 0x01, 0x0121, iface, 64);
            Check("GET_REPORT(Input 0x21), the engine's report, answers it",
                  own.Status == 0 && own.Data.Length == 37 && own.Data[0] == 0x21,
                  $"status {own.Status}, {own.Data.Length} bytes");
            var (otherId, otherSize) = declared.First(r => r.Id != 0x21);
            var other = cl.ControlIn(0xA1, 0x01, (ushort)(0x0100 | otherId), iface, 64);
            int want = Math.Min(otherSize, 64);
            Check($"GET_REPORT(Input 0x{otherId:X2}), declared and never sent, answers its ID and zeros at its length",
                  other.Status == 0 && other.Data.Length == want && other.Data[0] == otherId && other.Data.Skip(1).All(b => b == 0),
                  $"status {other.Status}, {other.Data.Length} bytes");
            var none = cl.ControlIn(0xA1, 0x01, 0x0107, iface, 64);
            Check("GET_REPORT(Input 0x07), which the descriptor does not declare, stalls",
                  none.Status == -32 && none.Data.Length == 0, $"status {none.Status}, {none.Data.Length} bytes");
        }
        finally
        {
            if (device != null)
            {
                server.Unregister(device);
                device.Dispose();
            }
            SharedMemoryIO.DestroyController(Index);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>A copy of gamecube-adapter whose protocol's getReport is
    /// "input", id s43-protocol-input, written to <paramref name="dir"/> and
    /// loaded into <paramref name="ctx"/>. Null, with the reason in
    /// <paramref name="problem"/>, when the catalog file or its protocol is
    /// missing or the copy does not load, so the caller reports a failed
    /// check instead of ending the probe.</summary>
    static ControllerProfile? ProtocolInputCopy(HMContext ctx, string dir, out string problem)
    {
        string? name = typeof(HMContext).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/').EndsWith("/gamecube-adapter.json", StringComparison.Ordinal));
        if (name == null)
        {
            problem = "gamecube-adapter.json is not embedded";
            return null;
        }
        string json;
        using (var stream = typeof(HMContext).Assembly.GetManifestResourceStream(name)!)
        using (var reader = new StreamReader(stream))
            json = reader.ReadToEnd();
        var j = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        j["id"] = "s43-protocol-input";
        // The protocol is a key of the profile itself since eecfd3bb, no
        // longer of usbConfiguration (profiles/schema.json).
        if (j["protocol"] is not System.Text.Json.Nodes.JsonObject protocol)
        {
            problem = "gamecube-adapter.json has no protocol object at $.protocol";
            return null;
        }
        protocol["getReport"] = "input";
        File.WriteAllText(Path.Combine(dir, "s43-protocol-input.json"), j.ToJsonString());
        ctx.LoadProfilesFromDirectory(dir);
        var profile = ctx.GetProfile("s43-protocol-input")?.Inner;
        problem = profile != null ? "" : "not loaded: " + string.Join("; ",
            ctx.ProfileLoadIssues.Where(i => i.ProfileId == "s43-protocol-input").Select(i => $"{i.JsonPath} {i.Reason}"));
        return profile;
    }

    /// <summary>S43's protocol GET_REPORT section without the server: the
    /// copy it imports loads, its protocol compiles with getReport "input"
    /// against the copy's descriptors, and the engine the device answers
    /// GET_REPORT(Input) from holds report 0x21 at its 37 bytes before any
    /// frame, as UsbipEmulatedDevice.ProtocolGetReport sends it.</summary>
    static void ProtocolInputOffline(HMContext ctx)
    {
        string dir = Path.Combine(Path.GetTempPath(), "hm-s43-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ControllerProfile? profile;
            string problem;
            try
            {
                profile = ProtocolInputCopy(ctx, dir, out problem);
            }
            catch (Exception e)
            {
                profile = null;
                problem = e.ToString();
            }
            Check("a copy of gamecube-adapter with getReport \"input\" loads", profile != null, problem);
            if (profile == null) return;
            var declared = profile.GetOrBuildReportBuilder().InputReports;
            Check("its descriptor declares an input report besides 0x21, which S43 reads as never sent",
                  declared.Any(r => r.Id == 0x21) && declared.Any(r => r.Id != 0x21),
                  string.Join(" ", declared.Select(r => $"{r.Id:X2}:{r.ByteSize}")));
            var protocol = CompiledProtocol.Compile(profile, new UsbDescriptorSet(profile, Index));
            Check("its protocol compiles with getReport \"input\" on an input endpoint, sending its own report",
                  protocol is { GetReport: GetReportMode.Input, BuildsReports: false, InputEndpoint: > 0, ReportId: 0x21 },
                  protocol == null ? "no protocol" : $"getReport {protocol.GetReport}, builds {protocol.BuildsReports}, endpoint 0x{protocol.InputEndpoint:X2}, report 0x{protocol.ReportId:X2}");
            if (protocol == null) return;
            var engine = new ProtocolEngine(protocol, profile.SerialString, Index);
            var report = engine.CopyReport();
            Check("before any frame the engine's report is 0x21 at 37 bytes",
                  report.Length == 37 && report[0] == 0x21, $"{report.Length} bytes, first {(report.Length > 0 ? report[0] : 0):X2}");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>The child: one device on its own server, the public audio
    /// object over it, and the host's requests for one event.</summary>
    static int ThrowingChild(string which)
    {
        SetErrorMode(QuietErrorMode);
        const int ChildIndex = Index + 1;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;
        var server = UsbipServer.GetOrStart();
        var device = new UsbipEmulatedDevice(profile, ChildIndex);
        server.Register(device);
        var audio = new HMUsbAudio(profile, device);
        int seen = 0;
        void Count() => Interlocked.Increment(ref seen);
        static Exception Thrown(string name) => new InvalidOperationException($"a throwing {name} handler");
        switch (which)
        {
            case "control":
                audio.ControlChanged += (_, _) => throw Thrown("ControlChanged");
                audio.ControlChanged += (_, _) => Count();
                break;
            case "speaker-streaming":
            case "inferred-close":
                audio.Output.StreamingChanged += (_, _) => throw Thrown("StreamingChanged");
                audio.Output.StreamingChanged += (_, _) => Count();
                break;
            case "microphone-streaming":
                audio.Microphone.StreamingChanged += (_, _) => throw Thrown("StreamingChanged");
                audio.Microphone.StreamingChanged += (_, _) => Count();
                break;
            case "frames":
                audio.Output.FramesReceived += (_, _) => throw Thrown("FramesReceived");
                audio.Output.FramesReceived += (_, _) => Count();
                break;
            default:
                Console.WriteLine($"CHILD {which} is not a case");
                return 1;
        }

        try
        {
            using var cl = new Client(server.Port);
            cl.SendImport(device.BusId);
            cl.ReadExactly(8);
            cl.ReadExactly(312);
            cl.ControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>());
            const int P = 10, B = 392;
            switch (which)
            {
                case "control":
                    cl.ControlOut(0x21, 0x01, 0x0200, 0x0200, new byte[] { 0x00, 0xF0 });
                    SpinWait.SpinUntil(() => Volatile.Read(ref seen) >= 1, Hang);
                    break;
                case "speaker-streaming":
                    cl.ControlOut(0x01, 0x0B, 0x0001, 0x0001, Array.Empty<byte>());
                    cl.ControlOut(0x01, 0x0B, 0x0000, 0x0001, Array.Empty<byte>());
                    SpinWait.SpinUntil(() => Volatile.Read(ref seen) >= 2, Hang);
                    break;
                case "microphone-streaming":
                    cl.ControlOut(0x01, 0x0B, 0x0001, 0x0002, Array.Empty<byte>());
                    cl.ControlOut(0x01, 0x0B, 0x0000, 0x0002, Array.Empty<byte>());
                    SpinWait.SpinUntil(() => Volatile.Read(ref seen) >= 2, Hang);
                    break;
                case "frames":
                {
                    cl.ControlOut(0x01, 0x0B, 0x0001, 0x0001, Array.Empty<byte>());
                    uint o1 = cl.NextSeq(), o2 = cl.NextSeq();
                    cl.SendSubmitIsoOut(o1, 1, MakePattern(P * B, 1), P, B);
                    cl.SendSubmitIsoOut(o2, 1, MakePattern(P * B, 2), P, B);
                    cl.ReadRet(o1);
                    cl.ReadRet(o2);
                    break;
                }
                case "inferred-close":
                {
                    // No SET_INTERFACE: the traffic opens the stream, and the
                    // engine closes it once the traffic stops.
                    uint o1 = cl.NextSeq(), o2 = cl.NextSeq();
                    cl.SendSubmitIsoOut(o1, 1, new byte[P * B], P, B);
                    cl.SendSubmitIsoOut(o2, 1, new byte[P * B], P, B);
                    cl.ReadRet(o1);
                    cl.ReadRet(o2);
                    SpinWait.SpinUntil(() => Volatile.Read(ref seen) >= 2, Hang);
                    break;
                }
            }
            var cfg = cl.ControlIn(0x80, 0x08, 0, 0, 1);
            Console.WriteLine($"CHILD {which} seen={Volatile.Read(ref seen)} alive={cfg.Status == 0 && cfg.Data.Length == 1}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CHILD {which} failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(ChildIndex);
        }
    }

    // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX
    const uint QuietErrorMode = 0x8003;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    // ── Teardown racing Dispose ─────────────────────────────────────────
    //
    // A controller's Dispose plugs the device out, which closes the socket
    // (usbip-win2 device.cpp detach), then disposes the device while the
    // connection's reader thread runs its own detach, and unmaps the sections
    // once Dispose returns (HMContext.OnControllerDisposing). A sweep disposes
    // the device first and plugs it out after, so the host's transfers keep
    // arriving on the open connection meanwhile. The GameCube adapter persona
    // runs a protocol clock with an event and a timer of its own.

    const int TeardownIndex = Index + 1;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    static void Teardown(HMContext ctx, UsbipServer server)
    {
        Console.WriteLine("\n-- Teardown racing Dispose --");
        var gc = ctx.GetProfile("gamecube-adapter")!.Inner;
        var hidLockField = typeof(UsbipEmulatedDevice).GetField("_hidLock", Private)!;
        var connectionField = typeof(UsbipEmulatedDevice).GetField("_connection", Private)!;
        // v2.0.0: the host poll clock is a DeviceClock, which owns its thread.
        var clockField = typeof(UsbipEmulatedDevice).GetField("_protocolClock", Private)!;
        var connections = (System.Collections.ICollection)typeof(UsbipServer).GetField("_connections", Private)!.GetValue(server)!;
        bool NoConnections() => WaitUntil(() => { lock (connections) return connections.Count == 0; }, Hang);

        NoConnections();

        // A transfer the host sends after Dispose, the connection still open:
        // the device ends the connection, so the host controller detaches it.
        {
            var (device, cl) = Attach(server, gc);
            server.Unregister(device);
            device.Dispose();
            cl.SendSubmitInterruptIn(cl.NextSeq(), 1, 37);
            string answer = cl.Within(Hang);
            Check("a transfer after Dispose gets no answer and the device ends the connection",
                  answer == "closed", answer);
            // A sweep disposes the device, then the controller's teardown
            // disposes it again.
            string second = "returned";
            try { device.Dispose(); }
            catch (Exception ex) { second = $"{ex.GetType().Name}: {ex.Message}"; }
            Check("a second Dispose returns without touching what the first released", second == "returned", second);
            cl.Dispose();
            NoConnections();
            SharedMemoryIO.DestroyController(TeardownIndex);
        }

        // An interrupt transfer and a control transfer after Dispose, on a
        // DualSense composite, whose transfers reach no clock: each ends the
        // connection, and the interrupt OUT writes nothing to the output
        // ring. Before this release neither got an answer, the connection
        // stayed open, and the interrupt OUT still wrote the ring.
        {
            var ds = ctx.GetProfile("dualsense-composite")!.Inner;
            IntPtr ring = IntPtr.Zero;
            foreach (string kind in new[] { "interrupt", "control" })
            {
                var (device, cl) = Attach(server, ds);
                ring = SharedMemoryIO.EnsureOutputMapping(TeardownIndex);
                uint headBefore = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(ring, 0);
                server.Unregister(device);
                device.Dispose();
                if (kind == "interrupt")
                {
                    var report = new byte[48];
                    report[0] = 0x02;
                    cl.SendSubmitInterruptOut(cl.NextSeq(), 3, report);
                }
                else
                {
                    cl.SendControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>());   // SET_CONFIGURATION(1)
                }
                string answer = cl.Within(Hang);
                uint headAfter = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(ring, 0);
                Check($"{(kind == "interrupt" ? "an" : "a")} {kind} transfer after Dispose gets no answer, ends the connection and writes no output",
                      answer == "closed" && headAfter == headBefore,
                      $"{answer}, output ring head {headBefore} then {headAfter}");
                cl.Dispose();
                NoConnections();
                SharedMemoryIO.DestroyController(TeardownIndex);
            }
        }

        // An import of a device Dispose has already stopped while the server
        // still lists it: refused. Before this release the claim ran the
        // protocol's reset into the disposed clock event, and the import got
        // no answer at all.
        {
            var device = new UsbipEmulatedDevice(gc, TeardownIndex);
            server.Register(device);
            device.Dispose();
            using var cl = new Client(server.Port);
            cl.SendImport(device.BusId);
            string answer;
            try
            {
                var op = cl.ReadExactly(8);
                uint status = BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4));
                answer = status == 0 ? "imported" : $"refused, status {status}";
            }
            catch (IOException) { answer = "no answer"; }
            Check("an import of a device Dispose has stopped is refused", answer.StartsWith("refused", StringComparison.Ordinal), answer);
            server.Unregister(device);
            cl.Dispose();
            NoConnections();
            SharedMemoryIO.DestroyController(TeardownIndex);
        }

        // The GameCube adapter's protocol clock held past Dispose's join, then
        // let go with the sections unmapped: it ends on its own and waits on
        // nothing Dispose released.
        {
            var (device, cl) = Attach(server, gc);
            var clock = ((IDeviceClock)clockField.GetValue(device)!).Thread;
            object hidLock = hidLockField.GetValue(device)!;
            var met = new System.Collections.Concurrent.ConcurrentQueue<string>();
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> watch = (_, e) =>
            {
                if (Thread.CurrentThread == clock) met.Enqueue($"{e.Exception.GetType().Name}: {e.Exception.Message}");
            };
            AppDomain.CurrentDomain.FirstChanceException += watch;
            bool disposed;
            Monitor.Enter(hidLock);
            try
            {
                // The clock asks whether a read is parked at least every
                // 500 ms, and now waits for this lock.
                Thread.Sleep(700);
                var disposer = new Thread(() => { server.Unregister(device); device.Dispose(); }) { IsBackground = true };
                disposer.Start();
                disposed = disposer.Join(15_000);
                if (disposed) SharedMemoryIO.DestroyController(TeardownIndex);
            }
            finally { Monitor.Exit(hidLock); }
            bool ended = WaitUntil(() => !clock.IsAlive, Hang);
            AppDomain.CurrentDomain.FirstChanceException -= watch;
            Check("the protocol clock held past Dispose's join ends without meeting anything Dispose released",
                  disposed && ended && met.IsEmpty,
                  !disposed ? "Dispose did not return" : !ended ? "the clock is still running" : met.IsEmpty ? "" : string.Join("; ", met));
            cl.Dispose();
            NoConnections();
            if (!disposed) SharedMemoryIO.DestroyController(TeardownIndex);
        }

        // The Switch 2 Pro report clock held past Dispose's join, then let go
        // with the sections unmapped: it ends on its own and waits on nothing
        // Dispose released.
        {
            var s2 = ctx.GetProfile("switch2-pro-controller-composite")!.Inner;
            var (device, cl) = Attach(server, s2);
            // v2.0.0: the device clock of a device that builds its reports, and
            // the protocol engine's lock, which the clock takes to learn
            // whether reports flow.
            var clock = ((IDeviceClock)typeof(UsbipEmulatedDevice).GetField("_layoutClock", Private)!.GetValue(device)!).Thread;
            object sync = typeof(ProtocolEngine).GetField("_sync", Private)!.GetValue(device.Protocol)!;
            var met = new System.Collections.Concurrent.ConcurrentQueue<string>();
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> watch = (_, e) =>
            {
                if (Thread.CurrentThread == clock) met.Enqueue($"{e.Exception.GetType().Name}: {e.Exception.Message}");
            };
            AppDomain.CurrentDomain.FirstChanceException += watch;
            bool disposed;
            Monitor.Enter(sync);
            try
            {
                // With reports off the clock asks for the report state at
                // least every 500 ms, and now waits for this lock.
                Thread.Sleep(700);
                var disposer = new Thread(() => { server.Unregister(device); device.Dispose(); }) { IsBackground = true };
                disposer.Start();
                disposed = disposer.Join(15_000);
                if (disposed) SharedMemoryIO.DestroyController(TeardownIndex);
            }
            finally { Monitor.Exit(sync); }
            bool ended = WaitUntil(() => !clock.IsAlive, Hang);
            AppDomain.CurrentDomain.FirstChanceException -= watch;
            Check("the report clock held past Dispose's join ends without meeting anything Dispose released",
                  disposed && ended && met.IsEmpty,
                  !disposed ? "Dispose did not return" : !ended ? "the clock is still running" : met.IsEmpty ? "" : string.Join("; ", met));
            cl.Dispose();
            NoConnections();
            if (!disposed) SharedMemoryIO.DestroyController(TeardownIndex);
        }

        // The input pump held after its stop check and before its read of the
        // input section, past Dispose's join, with the section unmapped once
        // Dispose returns. Only the pump's diagnostic dump
        // (HIDMAESTRO_DIAG_READS) takes a lock there, so this runs in a child
        // with it set. Before this release the pump then read the unmapped
        // section, an access violation.
        // A run proves something only when its hold took and its guard
        // landed: the pump stamps its dump time and then waits for the lock,
        // so a stamp written just before the child read it hides a hold that
        // did take, and another allocation in the process can take the freed
        // address before the child reserves it. Such a run is made again, up
        // to three times. A crash, a Dispose that does not return or a pump
        // that does not end fails at once.
        {
            const int Tries = 3;
            string detail = "";
            bool proved = false;
            for (int run = 1; run <= Tries && !proved; run++)
            {
                string diag = Path.Combine(Path.GetTempPath(), $"hm-usbip-held-pump-{Environment.ProcessId}-{run}.log");
                var (code, line) = RunChild("--held-pump", ("HIDMAESTRO_DIAG_READS", diag));
                try { File.Delete(diag); } catch (IOException) { }
                detail += (detail.Length > 0 ? ", " : "") + $"run {run}: exit {code}: {line}";
                bool done = code == 0 && line.Contains(" disposed=True ended=True", StringComparison.Ordinal);
                if (!done) break;
                proved = line == "CHILD held-pump held=True guarded=True disposed=True ended=True";
            }
            Check("the input pump held past Dispose's join ends without reading the unmapped section", proved, detail);
        }

        // A consumer's handler on the connection thread that waits for a
        // Dispose running on another thread, as a handler does that marshals
        // to a UI thread while that thread disposes the controller.
        {
            var ds = ctx.GetProfile("dualsense-composite")!.Inner;
            var (device, cl) = Attach(server, ds);
            bool joined = false;
            using var handled = new ManualResetEventSlim();
            device.Audio.ControlChanged += (_, _, _, _) =>
            {
                var disposer = new Thread(() => { server.Unregister(device); device.Dispose(); }) { IsBackground = true };
                disposer.Start();
                joined = disposer.Join(15_000);
                handled.Set();
            };
            cl.SendControlOut(0x21, 0x01, 0x0200, 0x0200, new byte[] { 0x00, 0xF0 });   // SET_CUR(volume)
            bool ran = handled.Wait(30_000);
            Check("Dispose returns while a consumer's handler on the connection thread waits for it",
                  ran && joined, ran ? $"Dispose returned within 15 s: {joined}" : "the handler never ran");
            cl.Dispose();
            NoConnections();
            SharedMemoryIO.DestroyController(TeardownIndex);
        }

        // A consumer's OutFrames handler on the audio pump still running when
        // Dispose's join of the pump gives up, and the sections unmapped
        // before it returns.
        {
            var ds = ctx.GetProfile("dualsense-composite")!.Inner;
            var (device, cl) = Attach(server, ds);
            var pump = (Thread)typeof(UsbAudioEngine).GetField("_thread", Private)!.GetValue(device.Audio)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            device.Audio.OutFrames += _ => { entered.Set(); release.Wait(Hang); };
            cl.SendSubmitIsoOut(cl.NextSeq(), 1, new byte[10 * 392], 10, 392);
            bool inHandler = entered.Wait(Hang);
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(TeardownIndex);
            release.Set();
            bool pumpEnded = WaitUntil(() => !pump.IsAlive, Hang);
            Check("the audio pump ends cleanly when Dispose's join gives up on a consumer's handler",
                  inHandler && pumpEnded && Volatile.Read(ref s_unhandled) == 0,
                  $"handler running at Dispose {inHandler}, pump ended {pumpEnded}");
            cl.Dispose();
            NoConnections();
        }

        // The reader thread's detach held between its claim on the device and
        // its reset while Dispose runs on another thread, and the sections
        // unmapped once Dispose returns, as the controller does.
        const int Held = 5;
        int heldOk = 0;
        string heldDetail = "";
        for (int i = 0; i < Held; i++)
        {
            var (device, cl) = Attach(server, gc);
            object hidLock = hidLockField.GetValue(device)!;
            var disposer = new Thread(() => device.Dispose()) { IsBackground = true, Name = "probe-dispose" };
            bool detaching, disposed;
            Monitor.Enter(hidLock);
            try
            {
                cl.Dispose();   // the host closes the connection
                detaching = WaitUntil(() => connectionField.GetValue(device) == null, Hang);
                server.Unregister(device);
                disposer.Start();
                disposed = disposer.Join(15_000);
                if (disposed) SharedMemoryIO.DestroyController(TeardownIndex);
            }
            finally { Monitor.Exit(hidLock); }
            bool closed = NoConnections();
            if (!disposed)
            {
                disposer.Join(10_000);
                SharedMemoryIO.DestroyController(TeardownIndex);
            }
            bool clockEnded = WaitUntil(() => !((IDeviceClock)clockField.GetValue(device)!).Thread.IsAlive, Hang);
            if (detaching && disposed && closed && clockEnded) heldOk++;
            else heldDetail = $"run {i}: detach under way {detaching}, Dispose returned {disposed}, connection closed {closed}, clock ended {clockEnded}";
        }
        Check($"Dispose returns while a detach is under way, and the detach then finishes and ends the connection ({Held} runs)",
              heldOk == Held, heldDetail);

        // The race as it comes: the host closes the connection while the
        // device is torn down, each side after its own random delay.
        const int Raced = 30;
        var rng = new Random(86);
        int racedOk = 0;
        for (int i = 0; i < Raced; i++)
        {
            var (device, cl) = Attach(server, gc);
            cl.ControlOut(0x00, 0x09, 0x0001, 0, Array.Empty<byte>());   // SET_CONFIGURATION(1)
            uint start = cl.NextSeq();
            cl.SendSubmitInterruptOut(start, 2, new byte[] { 0x13 });      // reports on
            cl.ReadRet(start);
            cl.SendSubmitInterruptIn(cl.NextSeq(), 1, 37);
            cl.SendSubmitInterruptIn(cl.NextSeq(), 1, 37);
            int closeAfter = rng.Next(0, 3000), disposeAfter = rng.Next(0, 3000);
            var closer = new Thread(() => { Spin(closeAfter); cl.Dispose(); }) { IsBackground = true };
            closer.Start();
            Spin(disposeAfter);
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(TeardownIndex);
            closer.Join();
            if (NoConnections()) racedOk++;
        }
        Check($"every connection closes when the host's close races Dispose ({Raced} runs)", racedOk == Raced, $"{racedOk}/{Raced}");
        Check("no exception escaped a device or connection thread", Volatile.Read(ref s_unhandled) == 0, $"{s_unhandled}");
    }

    /// <summary>The child for the held input pump. HIDMAESTRO_DIAG_READS is
    /// set, so the pump's dump takes _hidLock every 2 s, after the pump's
    /// stop check and before its read of the section.</summary>
    static int HeldPumpChild()
    {
        SetErrorMode(QuietErrorMode);
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var server = UsbipServer.GetOrStart();
        var device = new UsbipEmulatedDevice(ctx.GetProfile("dualsense-composite")!.Inner, TeardownIndex);
        server.Register(device);
        object hidLock = typeof(UsbipEmulatedDevice).GetField("_hidLock", Private)!.GetValue(device)!;
        var lastDump = typeof(UsbipEmulatedDevice).GetField("_diagLastDump", Private)!;
        var pump = (Thread)typeof(UsbipEmulatedDevice).GetField("_inputThread", Private)!.GetValue(device)!;
        IntPtr view = (IntPtr)typeof(UsbipEmulatedDevice).GetField("_inputView", Private)!.GetValue(device)!;
        bool held, disposed, guarded = false;
        Monitor.Enter(hidLock);
        try
        {
            // Read under the lock: a dump that stamps its time after this
            // read is waiting for the lock.
            long before = (long)lastDump.GetValue(device)!;
            // The next dump stamps its time, then waits for this lock.
            held = WaitUntil(() => (long)lastDump.GetValue(device)! != before, Hang);
            var disposer = new Thread(() => { server.Unregister(device); device.Dispose(); }) { IsBackground = true };
            disposer.Start();
            disposed = disposer.Join(15_000);
            if (disposed)
            {
                SharedMemoryIO.DestroyController(TeardownIndex);
                // Reserve the freed range with no access, so a read of the
                // unmapped section faults every time rather than finding
                // whatever the process maps there next.
                guarded = VirtualAlloc(view, (UIntPtr)4096, MEM_RESERVE, PAGE_NOACCESS) == view;
            }
        }
        finally { Monitor.Exit(hidLock); }
        bool ended = WaitUntil(() => !pump.IsAlive, Hang);
        Console.WriteLine($"CHILD held-pump held={held} guarded={guarded} disposed={disposed} ended={ended}");
        return 0;
    }

    const uint MEM_RESERVE = 0x2000, PAGE_NOACCESS = 0x01;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

    static (UsbipEmulatedDevice Device, Client Client) Attach(UsbipServer server, ControllerProfile profile)
    {
        var device = new UsbipEmulatedDevice(profile, TeardownIndex);
        server.Register(device);
        var cl = new Client(server.Port);
        cl.SendImport(device.BusId);
        var op = cl.ReadExactly(8);
        cl.ReadExactly(312);
        if (BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) != 0)
            throw new InvalidOperationException($"import of {device.BusId} refused");
        return (device, cl);
    }

    // The bound on a wait a correct device ends: a minute at the timeout
    // scale, which load or Windows' background work cannot reach.
    static readonly int Hang = TimeoutScale.Apply(60_000);

    static bool WaitUntil(Func<bool> done, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    static void Spin(long micros)
    {
        long end = Stopwatch.GetTimestamp() + micros * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(64);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);

    static uint ReadSeqNoBase(IntPtr view)
        => (uint)System.Runtime.InteropServices.Marshal.ReadInt32(view, 0);

    static bool WaitRing(IntPtr view, ref uint lastSeq, byte[] buf,
                         out byte source, out byte reportId, out int size)
    {
        for (int i = 0; i < 100; i++)
        {
            if (SharedMemoryIO.TryReadOutputFrame(view, ref lastSeq, out source, out reportId, out size, buf))
                return true;
            Thread.Sleep(2);
        }
        source = 0; reportId = 0; size = 0;
        return false;
    }

    static byte[] MakePattern(int len, byte seed)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)(seed + i * 7);
        return b;
    }

    static short S16(byte[] d) => d.Length >= 2 ? (short)(d[0] | (d[1] << 8)) : (short)0;

    /// <summary>The last alternate setting raised for an interface, or
    /// 0xFF when none was.</summary>
    static byte LastAlt(List<(byte Iface, byte Alt)> events, byte iface)
    {
        lock (events)
        {
            for (int i = events.Count - 1; i >= 0; i--)
                if (events[i].Iface == iface) return events[i].Alt;
        }
        return 0xFF;
    }

    static string ReadStr(byte[] b, int off, int max)
    {
        int end = Array.IndexOf(b, (byte)0, off, max);
        int len = end < 0 ? max : end - off;
        return System.Text.Encoding.UTF8.GetString(b, off, len);
    }

    /// <summary>Minimal vhci-side wire client. Replies can interleave
    /// (paced iso vs immediate control), so reads stash by seqnum.</summary>
    sealed class Client : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _s;
        uint _seq = 100;
        readonly Dictionary<uint, Ret> _stash = new();
        readonly HashSet<uint> _seen = new();
        readonly Dictionary<uint, int> _unlinkStash = new();

        public Client(int port)
        {
            _tcp = new TcpClient("127.0.0.1", port) { NoDelay = true };
            _s = _tcp.GetStream();
            _s.ReadTimeout = Hang;
        }

        public uint NextSeq() => _seq++;

        public struct Ret
        {
            public int Status, ActualLength, RawNumberOfPackets, StartFrame;
            public byte[] Data;
            public (uint Offset, uint Length, uint Actual, uint Status)[]? IsoDescs;
        }

        public void SendOpCommon(ushort code)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(b, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), code);
            _s.Write(b);
        }

        public void SendImport(string busid)
        {
            SendOpCommon(0x8003);
            var b = new byte[32];
            System.Text.Encoding.UTF8.GetBytes(busid).CopyTo(b, 0);
            _s.Write(b);
        }

        void SendHeader(uint seqnum, uint direction, uint ep, uint transferFlags,
                        int bufLen, int numPackets, int interval, ulong setup)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h, 1); // CMD_SUBMIT
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seqnum);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), (1u << 16) | (Index + 1));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), direction);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(20), transferFlags);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), bufLen);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(28), 0);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(32), numPackets);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(36), interval);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), setup);
            _s.Write(h);
        }

        static ulong Setup(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, ushort wLength)
        {
            Span<byte> s = stackalloc byte[8];
            s[0] = bmRequestType; s[1] = bRequest;
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], wValue);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], wIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], wLength);
            return BinaryPrimitives.ReadUInt64LittleEndian(s);
        }

        public Ret ControlIn(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, ushort wLength)
        {
            uint seq = NextSeq();
            _pendingIn.Add(seq);
            SendHeader(seq, 1, 0, 0, wLength, -1, 0, Setup(bmRequestType, bRequest, wValue, wIndex, wLength));
            return ReadRet(seq);
        }

        /// <summary>A control write whose answer the caller does not wait
        /// for.</summary>
        public void SendControlOut(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data)
        {
            SendHeader(NextSeq(), 0, 0, 0, data.Length, -1, 0,
                Setup(bmRequestType, bRequest, wValue, wIndex, (ushort)data.Length));
            if (data.Length > 0) _s.Write(data);
        }

        public Ret ControlOut(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data)
        {
            uint seq = NextSeq();
            SendHeader(seq, 0, 0, 0, data.Length, -1, 0,
                Setup(bmRequestType, bRequest, wValue, wIndex, (ushort)data.Length));
            if (data.Length > 0) _s.Write(data);
            return ReadRet(seq);
        }

        public void SendSubmitInterruptIn(uint seq, uint ep, int len)
        {
            _pendingIn.Add(seq);
            SendHeader(seq, 1, ep, 0, len, -1, 6, 0);
        }

        public void SendSubmitInterruptOut(uint seq, uint ep, byte[] data)
        {
            SendHeader(seq, 0, ep, 0, data.Length, -1, 6, 0);
            _s.Write(data);
        }

        public void SendSubmitIsoOut(uint seq, uint ep, byte[] payload, int packets, int perPacket)
        {
            SendHeader(seq, 0, ep, 0, payload.Length, packets, 4, 0);
            _s.Write(payload);
            WriteIsoDescs(packets, perPacket);
        }

        public void SendSubmitIsoIn(uint seq, uint ep, int packets, int perPacket)
        {
            _pendingIn.Add(seq);
            SendHeader(seq, 1, ep, 0, packets * perPacket, packets, 4, 0);
            WriteIsoDescs(packets, perPacket);
        }

        void WriteIsoDescs(int packets, int perPacket)
        {
            var d = new byte[packets * 16];
            for (int i = 0; i < packets; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(i * 16), (uint)(i * perPacket));
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(i * 16 + 4), (uint)perPacket);
            }
            _s.Write(d);
        }

        public void SendUnlink(uint seq, uint victim)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h, 2); // CMD_UNLINK
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), (1u << 16) | (Index + 1));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(20), victim);
            _s.Write(h);
        }

        /// <summary>What the server sends within <paramref name="ms"/>:
        /// "data", "closed" or "nothing".</summary>
        public string Within(int ms)
        {
            if (!_tcp.Client.Poll(ms * 1000, SelectMode.SelectRead)) return "nothing";
            return _tcp.Client.Available > 0 ? "data" : "closed";
        }

        public bool SawSeqnum(uint seq) { lock (_stash) return _stash.ContainsKey(seq) || _seen.Contains(seq); }

        public Ret ReadRet(uint seqnum)
        {
            while (true)
            {
                lock (_stash)
                {
                    if (_stash.Remove(seqnum, out var hit)) { _seen.Add(seqnum); return hit; }
                }
                PumpOne();
            }
        }

        public int ReadRetUnlink(uint seqnum)
        {
            while (true)
            {
                lock (_stash)
                {
                    if (_unlinkStash.Remove(seqnum, out var st)) return st;
                }
                PumpOne();
            }
        }

        void PumpOne()
        {
            var h = ReadExactly(48);
            uint command = BinaryPrimitives.ReadUInt32BigEndian(h);
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
            if (command == 4) // RET_UNLINK
            {
                int st = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20));
                lock (_stash) _unlinkStash[seq] = st;
                return;
            }
            if (command != 3) throw new InvalidOperationException($"unexpected command {command}");
            var r = new Ret
            {
                Status = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20)),
                ActualLength = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(24)),
                StartFrame = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(28)),
                RawNumberOfPackets = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(32)),
                Data = Array.Empty<byte>(),
            };
            // Payload rules: IN data (actual_length) for direction-in
            // transfers, then iso descriptors when isochronous. The probe
            // tracks direction implicitly: server zeroes the header's
            // direction, so infer from our own submit bookkeeping. Control
            // and interrupt IN replies have Data == actual_length; OUT acks
            // carry none. Iso: descriptors always follow.
            bool isIso = r.RawNumberOfPackets != -1;
            bool wasIn = _pendingIn.Contains(seq);
            _pendingIn.Remove(seq);
            if (wasIn && r.ActualLength > 0)
                r.Data = ReadExactly(r.ActualLength);
            if (isIso)
            {
                var d = ReadExactly(r.RawNumberOfPackets * 16);
                r.IsoDescs = new (uint, uint, uint, uint)[r.RawNumberOfPackets];
                for (int i = 0; i < r.RawNumberOfPackets; i++)
                    r.IsoDescs[i] = (
                        BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i * 16)),
                        BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i * 16 + 4)),
                        BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i * 16 + 8)),
                        BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i * 16 + 12)));
            }
            lock (_stash) _stash[seq] = r;
        }

        readonly HashSet<uint> _pendingIn = new();
        public byte[] ReadExactly(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(b, got, n - got);
                if (r <= 0) throw new IOException("EOF");
                got += r;
            }
            return b;
        }

        public void Dispose()
        {
            try { _tcp.Close(); } catch { }
        }
    }
}
