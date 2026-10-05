using System;
using System.Collections.Generic;
using System.Text;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The device side of the Switch 2 Pro Controller persona (issue
/// #66): the command protocol a host speaks on the vendor interface's bulk
/// pair, the flash image those commands read, and the input report the pad
/// would send at any moment. <see cref="UsbipEmulatedDevice"/> owns the
/// endpoints and the 4 ms clock. This class owns the state.
///
/// <para>Wire facts come from a link-layer capture of a console driving a
/// Pro Controller 2 on firmware 1.1.5
/// (switch2_controller_research captures/usb/rumble-procon-gccon.pcapng),
/// that repository's commands.md, hid_reports.md and memory_layout.md, SDL's
/// SDL_hidapi_switch2.c, and VIIPER's device/ns2pro. "Frame" below is a
/// frame of that capture.</para>
///
/// <para>A request is an 8-byte header and data: command, 0x91, transport
/// (0 on USB), subcommand, 0, data length, 0, 0. A reply is the command, a
/// status, the transport, the subcommand, 00 F8 00 00, then data (frame 180:
/// <c>03 01 00 0d 00 f8 00 00 01 00 00 00</c>). The pad answers every
/// command, and SDL waits 100 ms for each answer, so every command is
/// answered as it arrives.</para>
///
/// <para>State lasts from attach, or a port reset, until the next one. A
/// second host that opens the pad finds it as the first left it.</para></summary>
internal sealed class Switch2ProDevice
{
    public const byte ReportCommon = 0x05;   // the report SDL and Steam read motion from
    public const byte ReportPro = 0x09;      // the pad's default report
    public const int ReportSize = 64;

    // Feature bits (commands.md "Feature Flags"). Buttons 0x01, sticks
    // 0x02, mouse 0x10 and magnetometer 0x80 change nothing this persona
    // sends.
    public const byte FeatureImu = 0x04;
    public const byte FeatureRumble = 0x20;

    private const byte StatusOk = 0x01;
    // Frame 10166: the pad answers command 0x18, which arrived with a
    // later firmware, with status 0.
    private const byte StatusNotSupported = 0x00;

    private readonly object _sync = new();

    // Protocol state.
    private bool _reportsOn;
    private long _reportsOnSince;   // Stopwatch timestamp of the command that turned them on
    private byte _selectedReport = ReportPro;
    private byte _featureMask;
    private byte _featureEnabled;
    private byte _playerLeds;
    private uint _reportCount;

    // Replies awaiting bulk IN, oldest first, and how much of the oldest
    // has already gone out. A host that reads each reply never holds more
    // than one here. The bound is for one that writes commands and reads
    // nothing: past it the oldest unread reply makes room for the newest.
    private const int MaxWaitingReplies = 64;
    private readonly Queue<byte[]> _replies = new();
    private int _headOffset;

    private byte[] _body = Switch2ProPacker.NeutralBody();

    private readonly List<(uint Address, byte[] Data)> _flash;

    // Every bulk OUT payload, raw, with the Stopwatch time it arrived,
    // newest last. Steam's start sequence is in no source, so this is where
    // a command the table lacks shows up.
    private const int BulkLogCapacity = 256;
    private readonly Queue<(long Ticks, byte[] Payload)> _bulkLog = new();
    private readonly string _logTag;

    public Switch2ProDevice(string? serial, string logTag)
    {
        _logTag = logTag;
        _flash = BuildFlash(serial ?? "");
    }

    public bool ReportsOn { get { lock (_sync) return _reportsOn; } }

    /// <summary>Whether input reports are on, and when the command that
    /// turned them on arrived. The pad's first report follows that command
    /// by one report interval (frames 180 and 187).</summary>
    public bool ReportsOnSince(out long timestamp)
    {
        lock (_sync)
        {
            timestamp = _reportsOnSince;
            return _reportsOn;
        }
    }

    public byte SelectedReport { get { lock (_sync) return _selectedReport; } }
    public byte FeatureMask { get { lock (_sync) return _featureMask; } }
    public byte FeatureEnabled { get { lock (_sync) return _featureEnabled; } }
    public byte PlayerLeds { get { lock (_sync) return _playerLeds; } }

    /// <summary>Input reports sent since attach or the last port reset.</summary>
    public uint ReportsSent { get { lock (_sync) return _reportCount; } }

    /// <summary>The state at attach and after a port reset: reports off,
    /// report 0x09 selected, no feature mask, nothing enabled, no reply
    /// waiting. The consumer's last body stays.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _reportsOn = false;
            _selectedReport = ReportPro;
            _featureMask = 0;
            _featureEnabled = 0;
            _playerLeds = 0;
            _reportCount = 0;
            _replies.Clear();
            _headOffset = 0;
        }
    }

    /// <summary>Replace the state the next report is built from. Only a
    /// body <see cref="Switch2ProPacker.BuildBody"/> produced is taken.</summary>
    public void SetBody(ReadOnlySpan<byte> body)
    {
        if (body.Length != Switch2ProPacker.BodySize) return;
        var copy = body.ToArray();
        lock (_sync) _body = copy;
    }

    /// <summary>The bulk OUT payloads seen so far, oldest first.</summary>
    public List<byte[]> SnapshotBulkLog()
    {
        lock (_sync)
        {
            var list = new List<byte[]>(_bulkLog.Count);
            foreach (var entry in _bulkLog) list.Add(entry.Payload);
            return list;
        }
    }

    /// <summary>The same log with each payload's arrival time.</summary>
    public List<(long Ticks, byte[] Payload)> SnapshotBulkLogTimed()
    {
        lock (_sync) return new List<(long, byte[])>(_bulkLog);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    /// <summary>Take one bulk OUT payload and queue its reply. Returns true
    /// when this command turned input reports on.
    /// <paramref name="timestamp"/> is the Stopwatch time it arrived.</summary>
    public bool HandleBulkOut(ReadOnlySpan<byte> payload, long timestamp)
    {
        // Logged before anything reads it, so a payload the switch below
        // does not know is on record whole.
        var raw = payload.ToArray();
        if (DeviceOrchestrator.DiagEnabled)
            DeviceOrchestrator.LogDiag($"switch2 bulk out {_logTag}: {Convert.ToHexString(raw)}");

        lock (_sync)
        {
            if (_bulkLog.Count >= BulkLogCapacity) _bulkLog.Dequeue();
            _bulkLog.Enqueue((timestamp, raw));

            if (payload.Length < 8) return false;
            byte command = payload[0];
            byte transport = payload[2];
            byte sub = payload[3];
            var data = payload[8..];
            bool wasOn = _reportsOn;

            switch ((command << 8) | sub)
            {
                case 0x0201: // read a 0x40-byte flash block (commands.md "Read Memory Block")
                    if (data.Length >= 8) Enqueue(command, StatusOk, transport, sub, FlashReply(0x40, data));
                    else Enqueue(command, StatusOk, transport, sub, default);
                    break;
                case 0x0204: // read N bytes, at most 0x50 over USB (frames 8828 to 9211)
                    if (data.Length >= 8) Enqueue(command, StatusOk, transport, sub, FlashReply(Math.Min((int)data[0], 0x50), data));
                    else Enqueue(command, StatusOk, transport, sub, default);
                    break;
                case 0x0303: // input reports on or off
                    if (data.Length >= 1) _reportsOn = data[0] != 0;
                    Enqueue(command, StatusOk, transport, sub, Ack);
                    break;
                case 0x030A: // select the input report. Any other ID is ignored (frame 9919)
                    if (data.Length >= 1 && (data[0] == ReportCommon || data[0] == ReportPro))
                        _selectedReport = data[0];
                    Enqueue(command, StatusOk, transport, sub, default);
                    break;
                case 0x030D: // initialize USB: input reports start (frames 167, 180)
                    _reportsOn = true;
                    Enqueue(command, StatusOk, transport, sub, Ack);
                    break;
                case 0x0701: // frame 237
                    Enqueue(command, StatusOk, transport, sub, Zero1);
                    break;
                case 0x0907: // player LED mask (frames 746, 749)
                    if (data.Length >= 1) _playerLeds = data[0];
                    Enqueue(command, StatusOk, transport, sub, default);
                    break;
                case 0x0C01: // feature info
                    Enqueue(command, StatusOk, transport, sub, FeatureInfo(data.Length >= 1 ? data[0] : (byte)0));
                    break;
                case 0x0C02: // set the feature mask (frames 752, 8757)
                    if (data.Length >= 1) _featureMask = data[0];
                    Enqueue(command, StatusOk, transport, sub, Zero4);
                    break;
                case 0x0C03: // clear the mask and every enabled feature
                    _featureMask = 0;
                    _featureEnabled = 0;
                    Enqueue(command, StatusOk, transport, sub, Zero4);
                    break;
                case 0x0C04: // enable, within the mask (frames 9795, 9806)
                    if (data.Length >= 1) _featureEnabled |= (byte)(data[0] & _featureMask);
                    Enqueue(command, StatusOk, transport, sub, Zero4);
                    break;
                case 0x0C05: // disable, within the mask
                    if (data.Length >= 1) _featureEnabled &= (byte)~(data[0] & _featureMask);
                    Enqueue(command, StatusOk, transport, sub, Zero4);
                    break;
                case 0x1001: // firmware 1.1.5, Pro Controller (frame 10045)
                    Enqueue(command, StatusOk, transport, sub, FirmwareInfo);
                    break;
                case 0x1101: // commands.md 0x11 0x01
                    Enqueue(command, StatusOk, transport, sub, Ack);
                    break;
                case 0x1103: // sensor scales (commands.md 0x11 0x03)
                    Enqueue(command, StatusOk, transport, sub, SensorScales);
                    break;
                case 0x010C: // frame 10107
                    Enqueue(command, StatusOk, transport, sub, Reply010C);
                    break;
                case 0x1601: // frame 297: 24 zero bytes
                    Enqueue(command, StatusOk, transport, sub, Zero24);
                    break;
                case 0x0A08: // vibration data (frame 9271)
                    Enqueue(command, StatusOk, transport, sub, default);
                    break;
                default:
                    // 01/01 and 08/02 from SDL's start, and anything else:
                    // answered with no data.
                    Enqueue(command, command == 0x18 ? StatusNotSupported : StatusOk, transport, sub, default);
                    break;
            }
            if (wasOn || !_reportsOn) return false;
            _reportsOnSince = timestamp;
            return true;
        }
    }

    private void Enqueue(byte command, byte status, byte transport, byte sub, ReadOnlySpan<byte> data)
    {
        var reply = new byte[8 + data.Length];
        reply[0] = command;
        reply[1] = status;
        reply[2] = transport;
        reply[3] = sub;
        reply[5] = 0xF8;
        data.CopyTo(reply.AsSpan(8));
        if (_replies.Count >= MaxWaitingReplies)
        {
            // Never the head while part of it has gone out: the host is
            // halfway through reading that one.
            if (_headOffset > 0) return;
            _replies.Dequeue();
        }
        _replies.Enqueue(reply);
    }

    /// <summary>Reply data for both flash reads: the length read, three
    /// zero bytes, the address as asked, then the bytes.</summary>
    private byte[] FlashReply(int length, ReadOnlySpan<byte> request)
    {
        uint address = (uint)(request[4] | (request[5] << 8) | (request[6] << 16) | (request[7] << 24));
        var d = new byte[8 + length];
        d[0] = (byte)length;
        request.Slice(4, 4).CopyTo(d.AsSpan(4));
        ReadFlash(address, d.AsSpan(8, length));
        return d;
    }

    /// <summary>commands.md "Get Feature Info", the Pro Controller's
    /// values: one byte per feature bit, with the magnetometer's moved to
    /// index 3.</summary>
    private static byte[] FeatureInfo(byte flags)
    {
        var d = new byte[12];
        if ((flags & 0x01) != 0) d[4] = 0x07;
        if ((flags & 0x02) != 0) d[5] = 0x07;
        if ((flags & 0x04) != 0) d[6] = 0x01;
        if ((flags & 0x80) != 0) d[7] = 0x01;
        if ((flags & 0x10) != 0) d[8] = 0x01;
        if ((flags & 0x20) != 0) d[9] = 0x03;
        return d;
    }

    private static readonly byte[] Ack = { 0x01, 0x00, 0x00, 0x00 };
    private static readonly byte[] Zero1 = new byte[1];
    private static readonly byte[] Zero4 = new byte[4];
    private static readonly byte[] Zero24 = new byte[24];
    private static readonly byte[] Reply010C = { 0x61, 0x12, 0x50, 0x10 };

    private static readonly byte[] FirmwareInfo =
    {
        0x01, 0x01, 0x05, 0x02, 0x0C, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    // The reply of a pad whose gyro reads 16.4 counts per degree/second,
    // the kind report 0x05 below presents: six floats, 0.0023943 m/s/s and
    // 0.0010643 rad/s per count, then the full-scale pairs 78.45 and 34.91,
    // 19.61 and 8.727.
    private static readonly byte[] SensorScales =
    {
        0x01, 0x20, 0x03, 0x00, 0x00,
        0x0A, 0xE8, 0x1C, 0x3B, 0x79, 0x7D, 0x8B, 0x3A,
        0x0A, 0xE8, 0x9C, 0x42, 0x58, 0xA0, 0x0B, 0x42,
        0x0A, 0xE8, 0x9C, 0x41, 0x58, 0xA0, 0x0B, 0x41,
    };

    // ── Replies out ──────────────────────────────────────────────────────

    public bool HasReply { get { lock (_sync) return _replies.Count > 0; } }

    /// <summary>Up to <paramref name="capacity"/> bytes of the oldest
    /// reply. A reply longer than one read spans several: the pad's 80-byte
    /// flash reply arrives as 64 bytes and then 16 (frames 8837, 8840). Two
    /// replies never share a read. Null when nothing waits.</summary>
    public byte[]? TakeReply(int capacity)
    {
        lock (_sync)
        {
            if (_replies.Count == 0) return null;
            var head = _replies.Peek();
            int n = Math.Min(Math.Max(0, capacity), head.Length - _headOffset);
            var chunk = new byte[n];
            Array.Copy(head, _headOffset, chunk, 0, n);
            _headOffset += n;
            if (_headOffset >= head.Length)
            {
                _replies.Dequeue();
                _headOffset = 0;
            }
            return chunk;
        }
    }

    // ── Flash ────────────────────────────────────────────────────────────

    /// <summary>Read the flash image. A byte no segment names is 0xFF, what
    /// erased flash reads on the pad (frames 8840, 8966, 9211). Addresses
    /// wrap at 0x200000 (commands.md "Memory Read").</summary>
    public void ReadFlash(uint address, Span<byte> dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            uint a = (address + (uint)i) & 0x1FFFFF;
            byte value = 0xFF;
            foreach (var (start, data) in _flash)
            {
                if (a >= start && a - start < (uint)data.Length)
                {
                    value = data[a - start];
                    break;
                }
            }
            dst[i] = value;
        }
    }

    // memory_layout.md 0x13080 and 0x130C0, the same on both pads on record
    // (frames 8837, 8902).
    private static readonly byte[] StickBlockConstant =
    {
        0x01, 0xAD, 0xD9, 0x9A, 0x55, 0x56, 0x65, 0xA0, 0x00, 0x0A,
        0xA0, 0x00, 0x0A, 0xE2, 0x20, 0x0E, 0xE2, 0x20, 0x0E, 0x9A,
        0xAD, 0xD9, 0x9A, 0xAD, 0xD9, 0x0A, 0xA5, 0x50, 0x0A, 0xA5,
        0x50, 0x2F, 0xF6, 0x62, 0x2F, 0xF6, 0x62, 0x0A, 0xFF, 0xFF,
    };

    // Center 0x800, 2047 above it and 2048 below, on both axes: three
    // packed 12-bit pairs as SDL's ParseStickCalibration reads them. The
    // body's sticks use the whole 12-bit range, so full deflection reads as
    // full scale.
    private static readonly byte[] StickCalibration =
    {
        0x00, 0x08, 0x80, 0xFF, 0xF7, 0x7F, 0x00, 0x08, 0x80,
    };

    private static List<(uint, byte[])> BuildFlash(string serial)
    {
        // The USB serial, ASCII, zero padded to 16 bytes. SDL reads it from
        // here (ReadFlashBlock 0x13000, bytes 2 to 17).
        var serialField = new byte[16];
        var ascii = Encoding.ASCII.GetBytes(serial);
        Array.Copy(ascii, serialField, Math.Min(ascii.Length, serialField.Length));

        var temperatureAndGyroBias = new byte[16];
        BitConverter.TryWriteBytes(temperatureAndGyroBias, 25.0f);   // then a zero bias, which SDL subtracts

        var gravity = new byte[12];
        BitConverter.TryWriteBytes(gravity.AsSpan(8), 9.80665f);     // floats 0, 0, g

        return new List<(uint, byte[])>
        {
            (0x13000, new byte[] { 0x01, 0x00 }),
            (0x13002, serialField),
            (0x13012, new byte[] { 0x7E, 0x05, 0x69, 0x20 }),
            (0x13016, new byte[] { 0x01, 0x06, 0x01 }),
            (0x13019, new byte[] { 0x23, 0x23, 0x23, 0xA0, 0xA0, 0xA0, 0xE6, 0xE6, 0xE6, 0x32, 0x32, 0x32 }),
            (0x13040, temperatureAndGyroBias),
            (0x13080, StickBlockConstant),
            (0x130A8, StickCalibration),
            (0x130C0, StickBlockConstant),
            (0x130E8, StickCalibration),
            (0x13100, new byte[12]),
            (0x1310C, gravity),
        };
    }

    // ── Input reports ────────────────────────────────────────────────────

    /// <summary>Build the report the pad sends now, from the newest body
    /// and the protocol state at this moment, and count it. A report built
    /// any earlier could carry the wrong ID across a report selection, and
    /// SDL parses any 64-byte report as 0x05 without looking at the ID.
    ///
    /// <para>One count of reports sent drives all three counters: report
    /// 0x09's 8-bit counter, report 0x05's milliseconds (4 each) and its
    /// motion timestamp, which starts at 1 and steps 4000 microseconds. SDL
    /// takes its first set of sensor constants only when the timestamp
    /// advances 3600 to 4399 a report, and reads 0 as no sample. 1 plus a
    /// multiple of 4000 is never 0.</para></summary>
    public byte[] BuildNextReport()
    {
        lock (_sync) return BuildReport(_selectedReport, _reportCount++);
    }

    /// <summary>The report a GET_REPORT(Input) reads: what the next report
    /// would be, in the layout asked for or the selected one, without
    /// counting it.</summary>
    public byte[] PeekReport(byte reportId)
    {
        lock (_sync)
            return BuildReport(reportId == ReportCommon || reportId == ReportPro ? reportId : _selectedReport,
                               _reportCount);
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private byte[] BuildReport(byte reportId, uint n)
    {
        var body = _body;
        uint buttons = (uint)(body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24));
        int s = Switch2ProPacker.SticksOffset;
        ushort lx = (ushort)(body[s] | (body[s + 1] << 8)), ly = (ushort)(body[s + 2] | (body[s + 3] << 8));
        ushort rx = (ushort)(body[s + 4] | (body[s + 5] << 8)), ry = (ushort)(body[s + 6] | (body[s + 7] << 8));

        var r = new byte[ReportSize];
        if (reportId == ReportCommon)
        {
            r[0] = ReportCommon;
            WriteUInt32(r, 0x01, unchecked(n * 4));
            CommonButtons(buttons, r.AsSpan(0x05, 4));
            PackStick(r, 0x0B, lx, ly);
            PackStick(r, 0x0E, rx, ry);
            r[0x20] = 0xD8; r[0x21] = 0x0E;   // battery, 3800 mV
            r[0x22] = 0x20;                   // charged
            r[0x2A] = 0x01;
            if ((_featureEnabled & FeatureImu) != 0)
            {
                WriteUInt32(r, 0x2B, unchecked(1 + n * 4000));
                // 0x2F: temperature, 0. Then accelerometer and gyro.
                Array.Copy(body, Switch2ProPacker.MotionOffset, r, 0x31, 12);
            }
        }
        else
        {
            r[0] = ReportPro;
            r[1] = (byte)n;
            r[2] = 0x25;                      // on external power, charged, level 9
            r[3] = (byte)buttons;
            r[4] = (byte)(buttons >> 8);
            r[5] = (byte)((buttons >> 16) & 0x1F);
            PackStick(r, 6, lx, ly);
            PackStick(r, 9, rx, ry);
            r[12] = (byte)((_featureEnabled & FeatureRumble) != 0 ? 0x38 : 0x30);   // frames 9811, 9818
            // 15: the length of the pad's packed motion block, which no
            // public source decodes. This report carries no motion.
        }
        return r;
    }

    /// <summary>Report 0x05's four button bytes (hid_reports.md "Input
    /// Report 0x05" button format, SDL HandleSwitchProState).</summary>
    private static void CommonButtons(uint b, Span<byte> o)
    {
        int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        if ((b & Switch2ProPacker.BitY) != 0) b0 |= 0x01;
        if ((b & Switch2ProPacker.BitX) != 0) b0 |= 0x02;
        if ((b & Switch2ProPacker.BitB) != 0) b0 |= 0x04;
        if ((b & Switch2ProPacker.BitA) != 0) b0 |= 0x08;
        if ((b & Switch2ProPacker.BitR) != 0) b0 |= 0x40;
        if ((b & Switch2ProPacker.BitZR) != 0) b0 |= 0x80;
        if ((b & Switch2ProPacker.BitMinus) != 0) b1 |= 0x01;
        if ((b & Switch2ProPacker.BitPlus) != 0) b1 |= 0x02;
        if ((b & Switch2ProPacker.BitRightStick) != 0) b1 |= 0x04;
        if ((b & Switch2ProPacker.BitLeftStick) != 0) b1 |= 0x08;
        if ((b & Switch2ProPacker.BitHome) != 0) b1 |= 0x10;
        if ((b & Switch2ProPacker.BitCapture) != 0) b1 |= 0x20;
        if ((b & Switch2ProPacker.BitC) != 0) b1 |= 0x40;
        if ((b & Switch2ProPacker.BitDown) != 0) b2 |= 0x01;
        if ((b & Switch2ProPacker.BitUp) != 0) b2 |= 0x02;
        if ((b & Switch2ProPacker.BitRight) != 0) b2 |= 0x04;
        if ((b & Switch2ProPacker.BitLeft) != 0) b2 |= 0x08;
        if ((b & Switch2ProPacker.BitL) != 0) b2 |= 0x40;
        if ((b & Switch2ProPacker.BitZL) != 0) b2 |= 0x80;
        if ((b & Switch2ProPacker.BitGR) != 0) b3 |= 0x01;
        if ((b & Switch2ProPacker.BitGL) != 0) b3 |= 0x02;
        o[0] = (byte)b0; o[1] = (byte)b1; o[2] = (byte)b2; o[3] = (byte)b3;
    }

    /// <summary>Two 12-bit values in three bytes, as stick12-pair packs
    /// them: X low 8, X high 4 with Y low 4, Y high 8.</summary>
    private static void PackStick(byte[] dst, int offset, ushort x, ushort y)
    {
        dst[offset] = (byte)(x & 0xFF);
        dst[offset + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        dst[offset + 2] = (byte)((y >> 4) & 0xFF);
    }

    private static void WriteUInt32(byte[] dst, int offset, uint v)
    {
        dst[offset] = (byte)v;
        dst[offset + 1] = (byte)(v >> 8);
        dst[offset + 2] = (byte)(v >> 16);
        dst[offset + 3] = (byte)(v >> 24);
    }
}
