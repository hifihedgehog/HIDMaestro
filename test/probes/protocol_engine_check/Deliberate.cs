using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>The Switch Pro's deliberate changes (design note, decision (e)):
/// the 0x30 stream on a 15 ms grid from the start, where 9bd6279's stream
/// thread waited 15 ms after each tick and drifted by the tick's work and
/// the wait's rounding (driver.c:1817-1820), and the simple 0x3F report
/// sent when its bytes change, before the handshake and in input mode 0x3F,
/// as a real pad sends it (dekuNukem bluetooth_hid_notes.md, "Standard
/// input report - Simple HID mode": "Pushes updated packets with every
/// button/stick press"), where 9bd6279 sent it every tick before the
/// handshake and never after 03 3F. Each runs the catalog's switch-pro
/// through the SDK's protocol host on a channel that records what it
/// writes.</summary>
internal static class Deliberate
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    sealed class Recorder : IHostChannel
    {
        public readonly List<(long At, byte[] Report)> Frames = new();
        uint _seq;
        public uint Write(byte[] data, int dataLen, byte[] extended, int extendedLen)
        {
            lock (Frames) Frames.Add((Stopwatch.GetTimestamp(), extended[..extendedLen]));
            return ++_seq;
        }
        public bool Delivered(uint seq, int waitMs) => true;
        public bool AcknowledgesDelivery => false;
        public int Count(byte id) { lock (Frames) return Frames.Count(f => f.Report.Length > 0 && f.Report[0] == id); }
    }

    static (ProtocolHost Host, Recorder Channel) Make(int index)
    {
        var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "switch-pro");
        var compiled = CompiledProtocol.Compile(p, null)!;
        var ch = new Recorder();
        return (new ProtocolHost(p, compiled, index, ch, new object()), ch);
    }

    /// <summary>A subcommand as SDL writes it: report 0x01, a packet number,
    /// neutral rumble, the subcommand and its argument, 49 bytes.</summary>
    static byte[] Subcommand(byte id, byte arg = 0)
    {
        var b = new byte[49];
        b[0] = 0x01;
        new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }.CopyTo(b, 2);
        b[10] = id;
        b[11] = arg;
        return b;
    }

    public static void Run()
    {
        Console.WriteLine("-- the Switch Pro's deliberate changes: the 15 ms grid, 0x3F on change and in mode 0x3F");
        Change();
        Mode3F();
        Grid();
    }

    static void Change()
    {
        var (host, ch) = Make(7970);
        using (host)
        {
            for (int i = 0; i < 10; i++) host.Tick(0);
            Check("before the handshake ten idle ticks send one 0x3F, the state at rest", ch.Count(0x3F) == 1, $"{ch.Count(0x3F)}");
            var block = host.Engine.CopyStateBlock();
            block[2] |= 0x08;
            host.OnState(block);
            for (int i = 0; i < 10; i++) host.Tick(0);
            Check("a button pressed sends one more, and ten idle ticks after it none", ch.Count(0x3F) == 2, $"{ch.Count(0x3F)}");
        }
    }

    static void Mode3F()
    {
        var (host, ch) = Make(7971);
        using (host)
        {
            host.OnOutput(Subcommand(0x02), 49, 0);
            host.Tick(0);
            Check("after a subcommand a tick sends the 49-byte 0x30", ch.Count(0x30) == 1, $"{ch.Count(0x30)}");
            host.OnOutput(Subcommand(0x03, 0x3F), 49, 0);
            int before = ch.Count(0x3F);
            for (int i = 0; i < 5; i++) host.Tick(0);
            Check("after 03 3F the ticks send the 0x3F simple report, once, and no 0x30", ch.Count(0x3F) == before + 1 && ch.Count(0x30) == 1,
                  $"0x3F {ch.Count(0x3F) - before}, 0x30 {ch.Count(0x30)}");
            host.OnOutput(Subcommand(0x03, 0x30), 49, 0);
            host.Tick(0);
            Check("03 30 brings the 0x30 back", ch.Count(0x30) == 2, $"{ch.Count(0x30)}");
        }
    }

    /// <summary>The running clock after a handshake. Its missed-tick rule
    /// decides the grid: NextPoll keeps every tick on the origin plus whole
    /// periods and skips the points a late tick passed, where a clock that
    /// waits its period after each tick runs long every period, 16.58 ms at
    /// 9bd6279's identity commit. The rule is read from the host's clock, and
    /// the grid math it runs is checked above without a running clock. The
    /// median of 400 intervals is printed, not judged: a loaded machine
    /// moves it with no defect, 15.14 ms on the Atom fixture. 401 frames
    /// take 6 s, and 60 s is the hang bound.</summary>
    const int Periods = 400;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    static void Grid()
    {
        var (host, ch) = Make(7972);
        MissedTick? rule = null;
        try
        {
            host.OnOutput(Subcommand(0x02), 49, 0);
            host.Start();
            var clock = typeof(ProtocolHost).GetField("_clock", Instance)?.GetValue(host);
            rule = clock?.GetType().GetField("_missed", Instance)?.GetValue(clock) as MissedTick?;
            var deadline = Stopwatch.StartNew();
            while (ch.Count(0x30) < Periods + 1 && deadline.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(50);
        }
        finally { host.Dispose(); }
        Check("the 0x30 stream's clock keeps its grid: a late tick skips the points it passed and never restarts the period",
              rule == MissedTick.NextPoll, rule?.ToString() ?? "no clock");
        List<long> at;
        lock (ch.Frames) at = ch.Frames.Where(f => f.Report.Length > 0 && f.Report[0] == 0x30).Select(f => f.At).ToList();
        if (at.Count < Periods + 1) { Check($"the clock sends {Periods + 1} 0x30 frames within 60 s", false, $"{at.Count}"); return; }
        var intervals = Enumerable.Range(0, Periods).Select(i => (at[i + 1] - at[i]) * 1000.0 / Stopwatch.Frequency).OrderBy(ms => ms).ToList();
        double median = (intervals[Periods / 2 - 1] + intervals[Periods / 2]) / 2;
        Console.WriteLine($"  info: the 0x30 stream's median period over {Periods} intervals is {median:F4} ms");
    }
}
