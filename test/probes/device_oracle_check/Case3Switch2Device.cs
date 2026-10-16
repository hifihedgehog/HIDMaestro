// Case 3: the Switch 2 Pro composite's command responder and report
// builder, Switch2ProDevice at 9bd6279, driven in process through the
// members UsbipEmulatedDevice calls: HandleBulkOut for each bulk OUT payload
// (UsbipEmulatedDevice.cs:790), HasReply and TakeReply for each parked bulk
// IN read (821-824), SetBody for each frame of the input section (516),
// BuildNextReport on each clock tick (890), PeekReport for GET_REPORT(Input)
// (1263), Reset at attach, detach and port reset (439), ReportsOnSince for
// the report clock (871, 943), and the registers and bulk log the probes
// read (switch2_composite_check Steam.cs).
//
// Every output is one of: a command and what HandleBulkOut returned, the
// registers after it, the newest bulk log entry with the log's length, a
// reply chunk, a built or peeked report, a flash read, a body handed in.
// All of them land in the order they happen.
//
// The device reads no clock of its own. HandleBulkOut takes the arrival time
// from its caller (UsbipEmulatedDevice.cs:790 passes Stopwatch.GetTimestamp)
// and only stores it, in the bulk log and as the time reports turned on.
// The case hands it a clock of its own: 10,000 ticks a millisecond, starting
// at zero for each sequence, so the stored times are the same in every run.
// The 4 ms report clock of UsbipEmulatedDevice.cs:933-991 is not part of
// this case: the case calls BuildNextReport where that clock would.
//
// Only Subject below touches Switch2ProDevice, so a lane that replaces the
// device replays this case by changing that one class.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal sealed class Case3Switch2Device : IOracleCase
{
    public string Name => "case3-switch2-device";

    /// <summary>The case's own stream of the run's generator.</summary>
    const ulong RngStream = 0x5357_3244_4445_5643; // "SW2DDEVC"

    const string ProfileId = "switch2-pro-controller-composite";
    const long TicksPerMs = 10_000;
    const int ReportSize = 64;
    const int BodySize = 24;   // Switch2ProPacker.BodySize, 9bd6279
    const int Unbounded = int.MaxValue;

    static readonly int[] Capacities = { 0, 1, 8, 16, 64, 80, Unbounded };
    static readonly byte[] PeekIds = { 0x00, 0x05, 0x09, 0x07 };

    // Every command row Switch2ProDevice.HandleBulkOut names
    // (Switch2ProDevice.cs:175-240), as (command << 8) | subcommand.
    static readonly ushort[] Rows =
    {
        0x0201, 0x0204, 0x0303, 0x030A, 0x030D, 0x0701, 0x0907, 0x0C01, 0x0C02,
        0x0C03, 0x0C04, 0x0C05, 0x1001, 0x1101, 0x1103, 0x010C, 0x1601, 0x0A08,
    };

    // Rows the device does not name. 01/01 and 08/02 are in SDL's start
    // (SDL_hidapi_switch2.c:414-419), switch2_composite_check sends 55/AA as
    // a command no source names (Program.cs:501), and the rest are
    // neighbors of named rows and the extremes.
    static readonly ushort[] Unknown =
    {
        0x0000, 0x0101, 0x0802, 0x0700, 0x1002, 0x55AA, 0x0202, 0x0301, 0x0C06,
        0x1102, 0x0908, 0x0A09, 0x1602, 0x010D, 0xFFFF,
    };

    // First data bytes that change state in some row: report IDs for 03/0A
    // (0x05 and 0x09 taken, 0x07 ignored), feature bits for 0C/02 to 0C/05,
    // on and off for 03/03, read lengths for 02/04.
    static readonly byte[] Meaningful = { 0x00, 0x01, 0x04, 0x05, 0x07, 0x09, 0x20, 0x23, 0x27, 0x40, 0x50, 0x51, 0x80, 0xFF };

    // Flash addresses for 02/01 and 02/04: SDL's reads
    // (SDL_hidapi_switch2.c:433-491), the image's segment edges
    // (Switch2ProDevice.cs:405-419), and the 0x200000 wrap (353).
    static readonly uint[] Addresses =
    {
        0x13000, 0x13040, 0x13080, 0x130C0, 0x13100, 0x13140, 0x1FC040, 0x1FC080,
        0x12FFF, 0x13011, 0x130E7, 0x13117, 0x1FFFF8, 0x213000, 0xFFFFFFF0,
    };

    public void Record(GoldenCase golden, Options options)
    {
        var rng = new Rng(options.Seed, RngStream);
        var profile = ProfileDatabase.LoadEmbedded().All.FirstOrDefault(p => p.Id == ProfileId)
            ?? throw new InvalidOperationException($"No profile {ProfileId} in the catalog.");
        // The serial the USB/IP device hands the responder
        // (UsbipEmulatedDevice.cs:303), for transport index 0, 1 and 15.
        string? Serial(int index) => new UsbDescriptorSet(profile, index).SerialString;
        string serial0 = Serial(0) ?? "";

        Flash(golden, "index0", Serial(0));
        Flash(golden, "index1", Serial(1));
        Flash(golden, "index15", Serial(15));
        Flash(golden, "null", null);
        Flash(golden, "empty", "");
        Flash(golden, "long20", "ABCDEFGHIJKLMNOPQRST");

        SdlStart(golden, serial0, rng);
        SteamStart(golden, serial0);
        RowsByLength(golden, serial0, rng);
        AllPairs(golden, serial0);
        ShortPayloads(golden, serial0, rng);
        Reports(golden, serial0, rng);
        TakeReplies(golden, serial0, rng);
        Overflow(golden, serial0);
        ResetState(golden, serial0, rng);
        Random(golden, serial0, rng);
    }

    // ── Flash ────────────────────────────────────────────────────────────

    /// <summary>The flash image a serial builds, read through ReadFlash in
    /// 0x40 blocks over every segment, and across the 0x200000 wrap.</summary>
    static void Flash(GoldenCase g, string name, string? serial)
    {
        g.Stream($"switch2/{ProfileId}/flash-{name}");
        var s = new Subject(g, serial);
        for (uint a = 0x12FC0; a < 0x13180; a += 0x40) s.ReadFlash(a, 0x40);
        foreach (uint a in new uint[] { 0x1FC040, 0x1FC080, 0x1FFFF0, 0x212FF0, 0xFFFFFFF0 }) s.ReadFlash(a, 0x40);
        s.ReadFlash(0x13000, 0);
        s.ReadFlash(0x13000, 1);
        s.ReadFlash(0x13000, 0x50);
    }

    // ── SDL and Steam ────────────────────────────────────────────────────

    /// <summary>SDL's start of a Switch 2 Pro over USB, SDL-reference 804cf65
    /// SDL_hidapi_switch2.c. The flash reads come first (433-491, ReadFlashBlock
    /// at 252-275: 16 bytes, the address at bytes 12 to 15, then a read of
    /// 0x50), 0x13140 only on the GameCube controller (474-481), then each
    /// command of init_sequence (392-429) at its length, byte 5 plus 8
    /// (498-503), each followed by a read of 0x40. RecvBulkData (172-198)
    /// reads at most 64 bytes a transfer and stops at a short one. A read that
    /// finds no reply is libusb's timeout, and SDL moves on. Then the reports
    /// start, and the player LED (233-249, pattern 0x1 for player 0) and the
    /// sensor enable SDL sends after 100 samples (1150-1180) follow.</summary>
    static void SdlStart(GoldenCase g, string serial, Rng rng)
    {
        g.Stream($"switch2/{ProfileId}/sdl-804cf65-start");
        var s = new Subject(g, serial);
        long t = 0;
        foreach (uint address in new uint[] { 0x13000, 0x13040, 0x13080, 0x130C0, 0x13100, 0x1FC040, 0x1FC080 })
        {
            var read = Hex("02910001000800000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(read.AsSpan(12), address);
            s.Command(read, t += TicksPerMs);
            SdlReceive(s, 0x50);
        }
        foreach (var cmd in SdlInitSequence)
        {
            s.Command(cmd.AsSpan(0, cmd[5] + 8), t += TicksPerMs);
            SdlReceive(s, 0x40);
        }
        var body = new byte[BodySize];
        rng.Fill(body);
        s.SetBody(body);
        for (int i = 0; i < 10; i++) s.Report();
        s.Command(Hex("09910007000800000100000000000000"), t += 40 * TicksPerMs);
        SdlReceive(s, 8);
        for (int i = 0; i < 100; i++) s.Report();
        s.Command(Hex("0C9100040004000023000000"), t += 400 * TicksPerMs);
        SdlReceive(s, 12);
        for (int i = 0; i < 10; i++) s.Report();
        foreach (byte id in PeekIds) s.Peek(id);
        s.LogAll();
    }

    /// <summary>SDL_hidapi_switch2.c:392-429, verbatim.</summary>
    internal static readonly byte[][] SdlInitSequence =
    {
        Hex("0791000100000000"),
        Hex("0C9100020004000027000000"),
        Hex("1191000100000000"),
        Hex("0A9100080014000001FFFFFFFFFFFFFFFF3500460000000000000000"),
        Hex("0C9100040004000027000000"),
        Hex("0191000C00000000"),
        Hex("0191000100000000"),
        Hex("089100020004000001000000"),
        Hex("0391000A0004000005000000"),
        Hex("0391000D000800000100FFFFFFFFFFFF"),
    };

    /// <summary>RecvBulkData, SDL_hidapi_switch2.c:172-198.</summary>
    static void SdlReceive(Subject s, int size)
    {
        while (size > 0)
        {
            int current = Math.Min(size, 64);
            if (!s.HasReply())
            {
                s.Note("read-timeout");
                return;
            }
            int got = s.Take(current);
            size -= got;
            if (got < current) break;
        }
    }

    /// <summary>Steam's start of the pad, as Steam sent it to this persona in
    /// S65 part E: every bulk OUT payload, in order, with its arrival time,
    /// and each followed by a bulk IN read of 512 bytes. Recorded under
    /// HIDMAESTRO_DIAG=1 in %TEMP%\HIDMaestro\teardown_diag.log, lines
    /// 80827-80883, the run of logs\O-hs-switch2-final.log:265-283 (Steam's
    /// SDL added the device at 23:29:07, the log's first command is at
    /// 23:29:07.668). The extract is kept as logs\DD0-c34-s65-steam-bulk.log.
    /// The times below are milliseconds after the first command. The pad
    /// reports every 4 ms from one interval after 03/0D
    /// (UsbipEmulatedDevice.cs:958-968), so the case builds the reports that
    /// clock would have built before each later command.</summary>
    internal static readonly (int Ms, string Hex)[] SteamS65 =
    {
        (0, "02910001000800000000000000300100"),
        (0, "02910001000800000000000040300100"),
        (0, "02910001000800000000000080300100"),
        (0, "029100010008000000000000C0300100"),
        (1, "02910001000800000000000000310100"),
        (1, "02910001000800000000000040C01F00"),
        (1, "02910001000800000000000080C01F00"),
        (1, "0791000100000000"),
        (1, "0C9100020004000027000000"),
        (2, "1191000100000000"),
        (2, "0A9100080014000001FFFFFFFFFFFFFFFF3500460000000000000000"),
        (2, "0C9100040004000027000000"),
        (2, "0191000C00000000"),
        (2, "0191000100000000"),
        (2, "089100020004000001000000"),
        (3, "0391000A0004000005000000"),
        (3, "0391000D000800000100FFFFFFFFFFFF"),
        (1486, "09910007000800000100000000000000"),
        (2284, "0C9100040004000023000000"),
    };

    static void SteamStart(GoldenCase g, string serial)
    {
        g.Stream($"switch2/{ProfileId}/steam-s65-start");
        var s = new Subject(g, serial);
        // The probe staged gravity on the pad (Steam.cs:130, Stage(AccelGY = 1)):
        // buttons 0, sticks at 0x800, about 4096 counts on the third motion
        // word, which Switch2ProPacker.cs:110-112 fills from AccelGY.
        s.SetBody(Hex("00000000" + "0008000800080008" + "000000000010" + "000000000000"));
        long on = -1;
        long reportsBuilt = 0;
        foreach (var (ms, hex) in SteamS65)
        {
            long at = ms * TicksPerMs;
            if (on >= 0)
            {
                long due = (at - on) / (4 * TicksPerMs);
                for (; reportsBuilt < due; reportsBuilt++) s.Report();
            }
            if (s.Command(Hex(hex), at) && on < 0) on = at;
            if (s.HasReply()) s.Take(512);
            else s.Note("read-parked");
        }
        for (int i = 0; i < 25; i++) s.Report();
        foreach (byte id in PeekIds) s.Peek(id);
        s.LogAll();
    }

    // ── Every row, every data length ─────────────────────────────────────

    /// <summary>Each named row, 0x18 and the unknown rows with 0 to 17 data
    /// bytes, three payloads a length: random data, a meaningful first byte
    /// (or a flash read's length and address), and a random transport byte
    /// and length field. A fresh device for each row, its replies drained
    /// in 64-byte reads after each command.</summary>
    static void RowsByLength(GoldenCase g, string serial, Rng rng)
    {
        var rows = new List<ushort>(Rows);
        foreach (byte sub in new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF }) rows.Add((ushort)(0x1800 | sub));
        rows.AddRange(Unknown);
        foreach (ushort row in rows)
        {
            string kind = Array.IndexOf(Rows, row) >= 0 ? "row" : (row >> 8) == 0x18 ? "cmd18" : "unknown";
            g.Stream($"switch2/{ProfileId}/{kind}-{row:X4}");
            var s = new Subject(g, serial);
            long t = 0;
            for (int len = 0; len <= 17; len++)
            {
                for (int variant = 0; variant < 3; variant++)
                {
                    var p = new byte[8 + len];
                    p[0] = (byte)(row >> 8);
                    p[1] = 0x91;
                    p[3] = (byte)row;
                    p[5] = (byte)len;
                    rng.Fill(p.AsSpan(8));
                    if (variant == 1 && len > 0)
                    {
                        if ((row >> 8) == 0x02)
                        {
                            var head = new byte[8];
                            head[0] = Meaningful[rng.Next(Meaningful.Length)];
                            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), Addresses[rng.Next(Addresses.Length)]);
                            head.AsSpan(0, Math.Min(8, len)).CopyTo(p.AsSpan(8));
                        }
                        else
                        {
                            p[8] = Meaningful[(len + variant) % Meaningful.Length];
                        }
                    }
                    if (variant == 2)
                    {
                        p[1] = rng.Byte();
                        p[2] = rng.Byte();
                        p[4] = rng.Byte();
                        p[5] = rng.Byte();
                        p[6] = rng.Byte();
                        p[7] = rng.Byte();
                    }
                    s.Command(p, t += TicksPerMs);
                    s.Drain(64);
                }
            }
            s.Report();
            foreach (byte id in PeekIds) s.Peek(id);
        }
    }

    /// <summary>Every command and subcommand byte, 65,536 rows, on one
    /// device, each with the 8 data bytes of a flash read of 0x10 bytes at
    /// 0x13000. Outputs: what the command returned, whether a reply waits,
    /// and the reply whole, then the registers and the log at the end. The
    /// bulk log fills to its 256 entries and drops the oldest from then
    /// on.</summary>
    static void AllPairs(GoldenCase g, string serial)
    {
        g.Stream($"switch2/{ProfileId}/all-pairs");
        var s = new Subject(g, serial, quiet: true);
        var p = Hex("00910000000800001000000000300100");
        long t = 0;
        for (int cmd = 0; cmd < 256; cmd++)
        {
            for (int sub = 0; sub < 256; sub++)
            {
                p[0] = (byte)cmd;
                p[3] = (byte)sub;
                s.Command(p, t += TicksPerMs);
                s.Drain(Unbounded);
            }
        }
        s.Registers();
        s.LogAll();
    }

    /// <summary>Payloads shorter than the 8-byte header: logged and
    /// otherwise ignored (Switch2ProDevice.cs:166). Random bytes, and each
    /// prefix of 03/0D.</summary>
    static void ShortPayloads(GoldenCase g, string serial, Rng rng)
    {
        g.Stream($"switch2/{ProfileId}/short");
        var s = new Subject(g, serial);
        long t = 0;
        var start = Hex("0391000D000800000100FFFFFFFFFFFF");
        for (int len = 0; len < 8; len++)
        {
            s.Command(start.AsSpan(0, len), t += TicksPerMs);
            var p = new byte[len];
            rng.Fill(p);
            s.Command(p, t += TicksPerMs);
        }
        s.Command(start.AsSpan(0, 8), t += TicksPerMs);
        s.Drain(64);
        s.LogAll();
    }

    // ── Reports ──────────────────────────────────────────────────────────

    /// <summary>Reports 0x09 and 0x05 with the IMU and rumble features off
    /// and on, from random bodies, every peek, and bodies of the wrong size,
    /// which the device ignores (Switch2ProDevice.cs:127).</summary>
    static void Reports(GoldenCase g, string serial, Rng rng)
    {
        g.Stream($"switch2/{ProfileId}/reports");
        var s = new Subject(g, serial);
        long t = 0;
        var body = new byte[BodySize];
        foreach (string setup in new[]
        {
            "",                                         // 0x09, nothing enabled, reports off
            "0391000D000800000100FFFFFFFFFFFF",         // reports on, 0x09
            "0C9100020004000027000000",                 // mask 0x27
            "0C9100040004000020000000",                 // rumble on
            "0391000A0004000005000000",                 // 0x05
            "0C9100040004000004000000",                 // IMU on
            "0C9100050004000020000000",                 // rumble off
            "0391000A0004000009000000",                 // back to 0x09
            "0C9100030004000000000000",                 // mask and features cleared
            "039100030004000000000000",                 // reports off
        })
        {
            if (setup.Length > 0)
            {
                s.Command(Hex(setup), t += TicksPerMs);
                s.Drain(64);
            }
            for (int i = 0; i < 40; i++)
            {
                rng.Fill(body);
                // Edges: every button bit, the 12-bit stick extremes and
                // values past them, motion extremes.
                if (i == 0) body.AsSpan().Fill(0x00);
                if (i == 1) body.AsSpan().Fill(0xFF);
                if (i == 2) { body.AsSpan().Fill(0x00); Hex("FFFFFF00" + "FF0FFF0FFF0FFF0F" + "FF7FFF7FFF7F008000800080").CopyTo(body, 0); }
                s.SetBody(body);
                s.Report();
                if (i % 8 == 0) foreach (byte id in PeekIds) s.Peek(id);
            }
        }
        foreach (int wrong in new[] { 0, 1, 23, 25, 48, 64 })
        {
            var w = new byte[wrong];
            rng.Fill(w);
            s.SetBody(w);
            s.Report();
            s.Peek(0x00);
        }
    }

    // ── Replies ──────────────────────────────────────────────────────────

    /// <summary>A flash reply of 80 bytes (02/01), one of 96 (02/04 for
    /// 0x50 bytes), one of 12 (0C/02), each read at one capacity until it
    /// is gone (twelve reads at most, as capacity 0 never moves), then
    /// three of them waiting at once, read at random capacities.</summary>
    static void TakeReplies(GoldenCase g, string serial, Rng rng)
    {
        g.Stream($"switch2/{ProfileId}/take-reply");
        var s = new Subject(g, serial);
        long t = 0;
        string[] cmds =
        {
            "02910001000800000000000000300100",
            "02910004000800005000000000300100",
            "0C9100020004000027000000",
        };
        foreach (int cap in Capacities)
        {
            foreach (string c in cmds)
            {
                s.Command(Hex(c), t += TicksPerMs);
                for (int i = 0; i < 12 && s.HasReply(); i++) s.Take(cap);
                if (s.HasReply()) s.Take(Unbounded);
                s.Take(cap);   // nothing waiting: null
            }
        }
        for (int round = 0; round < 50; round++)
        {
            foreach (string c in cmds) s.Command(Hex(c), t += TicksPerMs);
            for (int i = 0; i < 40 && s.HasReply(); i++) s.Take(Capacities[rng.Next(Capacities.Length)]);
            s.Drain(Unbounded);
        }
    }

    /// <summary>More than 64 replies waiting (Switch2ProDevice.cs:61, 262-268):
    /// with the head unread the oldest goes, with the head half read the
    /// newest is dropped, and Reset clears a half-read head.</summary>
    static void Overflow(GoldenCase g, string serial)
    {
        g.Stream($"switch2/{ProfileId}/overflow");
        var s = new Subject(g, serial);
        long t = 0;
        byte[] Numbered(int i) => Hex($"0C91000100040000{(byte)i:X2}000000");   // feature info, its flags vary the reply
        for (int i = 0; i < 70; i++) s.Command(Numbered(i), t += TicksPerMs);
        s.Drain(Unbounded);

        s.Command(Hex("02910001000800000000000000300100"), t += TicksPerMs);
        s.Take(4);
        for (int i = 0; i < 70; i++) s.Command(Numbered(i), t += TicksPerMs);
        s.Take(Unbounded);
        for (int i = 70; i < 80; i++) s.Command(Numbered(i), t += TicksPerMs);
        s.Drain(64);

        for (int i = 0; i < 64; i++) s.Command(Numbered(i), t += TicksPerMs);
        s.Take(16);
        s.Command(Numbered(200), t += TicksPerMs);
        s.Reset();
        s.Take(64);
        s.Command(Numbered(201), t += TicksPerMs);
        s.Drain(Unbounded);
        s.LogAll();
    }

    /// <summary>Reset after every kind of state: it clears reports, the
    /// selection, the mask, the features, the LEDs, the count and the
    /// replies, and keeps the body and the bulk log
    /// (Switch2ProDevice.cs:105-121).</summary>
    static void ResetState(GoldenCase g, string serial, Rng rng)
    {
        g.Stream($"switch2/{ProfileId}/reset");
        var s = new Subject(g, serial);
        long t = 0;
        var body = new byte[BodySize];
        rng.Fill(body);
        s.SetBody(body);
        foreach (string c in new[]
        {
            "0391000D000800000100FFFFFFFFFFFF", "0391000A0004000005000000", "0C9100020004000027000000",
            "0C9100040004000027000000", "09910007000800000900000000000000",
        })
            s.Command(Hex(c), t += TicksPerMs);
        for (int i = 0; i < 5; i++) s.Report();
        s.Take(3);
        s.Reset();
        s.Report();
        foreach (byte id in PeekIds) s.Peek(id);
        s.Command(Hex("0391000D000800000100FFFFFFFFFFFF"), t += TicksPerMs);
        s.Reset();
        s.Reset();
        s.Drain(64);
        s.LogAll();
    }

    // ── Random ───────────────────────────────────────────────────────────

    const int RandomStreams = 100_000;
    const int RandomBlocks = 10;

    /// <summary>10^5 random command streams in ten golden streams. Each
    /// starts on a new device or a Reset of the last one and runs 1 to 24
    /// operations: commands (mostly named rows, with random data, lengths,
    /// transports and length fields, sometimes any command or shorter than
    /// a header), SetBody of random bodies and sizes, BuildNextReport,
    /// PeekReport, TakeReply at each capacity, Reset, and register and log
    /// reads.</summary>
    static void Random(GoldenCase g, string serial, Rng rng)
    {
        Subject? s = null;
        long t = 0;
        int perBlock = RandomStreams / RandomBlocks;
        for (int block = 0; block < RandomBlocks; block++)
        {
            g.Stream($"switch2/{ProfileId}/random-{block:D2}");
            for (int n = 0; n < perBlock; n++)
            {
                var seq = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(seq, block * perBlock + n);
                g.Out("seq", seq);
                if (s == null || rng.Next(10) < 6)
                {
                    s = new Subject(g, serial, quiet: true);
                    t = 0;
                }
                else
                {
                    s.Reset();
                }
                int ops = 1 + rng.Next(24);
                for (int op = 0; op < ops; op++)
                {
                    int r = rng.Next(100);
                    if (r < 45) s.Command(RandomCommand(rng), t += 1 + rng.Next(8 * (int)TicksPerMs));
                    else if (r < 65) s.Take(Capacities[rng.Next(Capacities.Length)]);
                    else if (r < 75) s.Report();
                    else if (r < 82) s.Peek(rng.Next(5) == 0 ? rng.Byte() : PeekIds[rng.Next(PeekIds.Length)]);
                    else if (r < 90)
                    {
                        var body = new byte[rng.Next(10) == 0 ? rng.Next(70) : BodySize];
                        rng.Fill(body);
                        s.SetBody(body);
                    }
                    else if (r < 95) s.Registers();
                    else if (r < 98) s.Reset();
                    else s.LogAll();
                }
                s.Registers();
            }
        }
    }

    static byte[] RandomCommand(Rng rng)
    {
        if (rng.Next(30) == 0)
        {
            var shortOne = new byte[rng.Next(8)];
            rng.Fill(shortOne);
            return shortOne;
        }
        int pick = rng.Next(100);
        ushort row = pick < 70 ? Rows[rng.Next(Rows.Length)]
            : pick < 80 ? Unknown[rng.Next(Unknown.Length)]
            : pick < 85 ? (ushort)(0x1800 | rng.Byte())
            : (ushort)rng.Next(0x10000);
        int len = rng.Next(20) == 0 ? rng.Next(81) : rng.Next(18);
        var p = new byte[8 + len];
        p[0] = (byte)(row >> 8);
        p[1] = rng.Next(10) == 0 ? rng.Byte() : (byte)0x91;
        p[2] = rng.Next(10) == 0 ? rng.Byte() : (byte)0;
        p[3] = (byte)row;
        p[5] = rng.Next(5) == 0 ? rng.Byte() : (byte)len;
        rng.Fill(p.AsSpan(8));
        if (len > 0 && rng.Bool())
        {
            if ((row >> 8) == 0x02)
            {
                var head = new byte[8];
                head[0] = Meaningful[rng.Next(Meaningful.Length)];
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4),
                    rng.Bool() ? Addresses[rng.Next(Addresses.Length)] : rng.NextU32());
                head.AsSpan(0, Math.Min(8, len)).CopyTo(p.AsSpan(8));
            }
            else
            {
                p[8] = Meaningful[rng.Next(Meaningful.Length)];
            }
        }
        return p;
    }

    static byte[] Hex(string hex) => Convert.FromHexString(hex);

    /// <summary>The device under test and the outputs it gives. The one
    /// class here that names Switch2ProDevice.</summary>
    sealed class Subject
    {
        readonly GoldenCase _g;
        readonly Switch2ProDevice _d;
        readonly bool _quiet;
        readonly byte[] _report = new byte[ReportSize];

        /// <summary><paramref name="quiet"/> leaves out the registers and log
        /// entry after each command, for the long streams. A quiet stream
        /// reads them where it asks.</summary>
        public Subject(GoldenCase g, string? serial, bool quiet = false)
        {
            _g = g;
            _quiet = quiet;
            // The log tag only names the device in diagnostic lines.
            _d = new Switch2ProDevice(serial, "oracle");
        }

        public bool Command(ReadOnlySpan<byte> payload, long ticks)
        {
            bool started = _d.HandleBulkOut(payload, ticks);
            _g.Out(started ? "cmd.started" : "cmd", payload);
            if (!_quiet)
            {
                Registers();
                LogNewest();
            }
            else
            {
                _g.Out("has-reply", new[] { (byte)(_d.HasReply ? 1 : 0) });
            }
            return started;
        }

        public bool HasReply() => _d.HasReply;

        /// <summary>One bulk IN read: the chunk it gets, or none.</summary>
        public int Take(int capacity)
        {
            var chunk = _d.TakeReply(capacity);
            string cap = capacity == Unbounded ? "max" : capacity.ToString();
            if (chunk == null)
            {
                _g.Out("reply-none.c" + cap, ReadOnlySpan<byte>.Empty);
                return 0;
            }
            _g.Out("reply.c" + cap, chunk);
            return chunk.Length;
        }

        /// <summary>Read until nothing waits. Capacity 0 never empties a
        /// reply, so it is not a drain capacity.</summary>
        public void Drain(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            while (_d.HasReply) Take(capacity);
        }

        public void Report()
        {
            _report.AsSpan().Fill(0xA5);   // a byte the device does not write shows
            int n = _d.BuildNextReport(_report);
            _g.Out("report", _report.AsSpan(0, n));
        }

        public void Peek(byte id) => _g.Out($"peek.{id:X2}", _d.PeekReport(id));

        public void SetBody(ReadOnlySpan<byte> body)
        {
            _d.SetBody(body);
            _g.Out("body", body);
        }

        public void Reset()
        {
            _d.Reset();
            _g.Out("reset", ReadOnlySpan<byte>.Empty);
            if (!_quiet) Registers();
        }

        public void ReadFlash(uint address, int length)
        {
            var dst = new byte[length];
            _d.ReadFlash(address, dst);
            var o = new byte[4 + length];
            BinaryPrimitives.WriteUInt32LittleEndian(o, address);
            dst.CopyTo(o, 4);
            _g.Out("flash", o);
        }

        public void Note(string what) => _g.Out(what, ReadOnlySpan<byte>.Empty);

        /// <summary>Every register the device exposes: reports on and the
        /// time they turned on, the selected report, the feature mask and
        /// enabled bits, the player LEDs, reports sent, a reply waiting.</summary>
        public void Registers()
        {
            var r = new byte[19];
            bool on = _d.ReportsOnSince(out long since);
            r[0] = (byte)(on ? 1 : 0);
            r[1] = (byte)(_d.ReportsOn ? 1 : 0);
            BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(2), since);
            r[10] = _d.SelectedReport;
            r[11] = _d.FeatureMask;
            r[12] = _d.FeatureEnabled;
            r[13] = _d.PlayerLeds;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(14), _d.ReportsSent);
            r[18] = (byte)(_d.HasReply ? 1 : 0);
            _g.Out("regs", r);
        }

        /// <summary>The log's length and its newest entry, time and payload.</summary>
        void LogNewest()
        {
            var log = _d.SnapshotBulkLogTimed();
            var (ticks, payload) = log[^1];
            var o = new byte[2 + 8 + payload.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)log.Count);
            BinaryPrimitives.WriteInt64LittleEndian(o.AsSpan(2), ticks);
            payload.CopyTo(o, 10);
            _g.Out("log", o);
        }

        /// <summary>The whole bulk log, oldest first, with the untimed
        /// snapshot's length beside it.</summary>
        public void LogAll()
        {
            var timed = _d.SnapshotBulkLogTimed();
            int plain = _d.SnapshotBulkLog().Count;
            var head = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)timed.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), (ushort)plain);
            _g.Out("log-all", head);
            foreach (var (ticks, payload) in timed)
            {
                var o = new byte[8 + payload.Length];
                BinaryPrimitives.WriteInt64LittleEndian(o, ticks);
                payload.CopyTo(o, 8);
                _g.Out("log-entry", o);
            }
        }
    }
}
