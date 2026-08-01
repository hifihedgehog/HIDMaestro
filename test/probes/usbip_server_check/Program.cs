// USB/IP server protocol check (issue #39).
//
// Plays usbip-win2's role over loopback TCP against the real in-process
// server + emulated composite device, no kernel driver involved. Every
// wire expectation below is the 0.9.7.7 receive path's, read at source
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
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    const int Index = 60; // far from live controller indices

    static int Main()
    {
        Console.WriteLine("=== USB/IP server protocol (issue #39) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile("dualsense-composite")!.Inner;

        var server = UsbipServer.GetOrStart();
        var device = new UsbipEmulatedDevice(profile, Index);
        server.Register(device);

        var outFrames = new MemoryStream();
        device.Audio.OutFrames += pcm => { lock (outFrames) outFrames.Write(pcm.Span); };
        var controlWrites = new List<(byte Unit, byte Sel, short Raw)>();
        device.Audio.ControlChanged += (u, s, c, v) => { lock (controlWrites) controlWrites.Add((u, s, v)); };

        try
        {
            RunAll(server, device, profile, outFrames, controlWrites);
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(Index);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
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

        var prod = cl.ControlIn(0x80, 0x06, 0x0302, 0x0409, 255);
        Check("product string is 'Wireless Controller' (40-byte descriptor)",
              prod.Status == 0 && prod.Data.Length == 40 && prod.Data[0] == 40 && prod.Data[1] == 3
              && System.Text.Encoding.Unicode.GetString(prod.Data, 2, 38) == "Wireless Controller");

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
        var f20 = cl.ControlIn(0xA1, 0x01, 0x0320, 3, 64);
        Check("0x20 firmware: 64 bytes with fwType=2 at byte 20",
              f20.Status == 0 && f20.Data.Length == 64 && f20.Data[20] == 0x02);
        WaitRing(outView, ref ringSeq, ringBuf, out _, out _, out _); // drain the 0x20 notify
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
        Thread.Sleep(20);
        lock (controlWrites)
            Check("ControlChanged event carried the write",
                  controlWrites.Any(w => w.Item1 == 2 && w.Item2 == 2 && w.Item3 == unchecked((short)0xF000)));
        var volCur2 = cl.ControlIn(0xA1, 0x81, 0x0200, 0x0200, 2);
        Check("GET_CUR reflects the write", S16(volCur2.Data) == unchecked((short)0xF000));

        var badUnit = cl.ControlIn(0xA1, 0x81, 0x0200, 0x0900, 2);
        Check("unknown unit stalls", badUnit.Status == -32);

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
        Check("pacing sane: completed within 400 ms", sw.ElapsedMilliseconds < 400);
        Check("iso OUT RET: n packets echoed, actual = buffer length",
              lastRet.RawNumberOfPackets == Pkts && lastRet.ActualLength == Pkts * PktBytes);
        Check("iso OUT descriptors echo offsets with status 0",
              lastRet.IsoDescs != null && lastRet.IsoDescs.Length == Pkts
              && lastRet.IsoDescs[0].Offset == 0 && lastRet.IsoDescs[1].Offset == PktBytes
              && lastRet.IsoDescs.All(d => d.Status == 0));
        Thread.Sleep(30);
        lock (outFrames)
            Check("OutFrames delivered every PCM byte in order",
                  outFrames.Length == sent.Length
                  && outFrames.ToArray().SequenceEqual(sent.ToArray()),
                  $"{outFrames.Length}/{sent.Length} bytes");

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
            _s.ReadTimeout = 5000;
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
