using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>The device clock (DeviceClock), which every device that sends
/// on its own time runs on: the idle repeat, a UMDF2 protocol host, and a
/// USB/IP device's reports and host polls. A pass that threw used to run
/// again at once, its due time unmoved, and held a core while it threw.</summary>
internal static class Clock
{
    static readonly long s_period = Math.Max(1, Stopwatch.Frequency / 1000);
    static bool s_fail;
    static int s_attempts, s_successes;

    /// <summary>A tick that throws while the case says so.</summary>
    struct FailingTick : IClockTick
    {
        public bool Running(out long origin) { origin = 0; return true; }
        public long Period => s_period;
        public long NotBefore => 0;
        public void Tick(long due)
        {
            Interlocked.Increment(ref s_attempts);
            if (Volatile.Read(ref s_fail)) throw new InvalidOperationException("loop_containment_check: a tick that throws");
            Interlocked.Increment(ref s_successes);
        }
        public void RunEnded() { }
    }

    /// <summary>A Running that throws while the case says so, so the pass
    /// fails before any tick.</summary>
    struct FailingRunning : IClockTick
    {
        public bool Running(out long origin)
        {
            origin = 0;
            Interlocked.Increment(ref s_attempts);
            if (Volatile.Read(ref s_fail)) throw new InvalidOperationException("loop_containment_check: a Running that throws");
            return true;
        }
        public long Period => s_period;
        public long NotBefore => 0;
        public void Tick(long due) => Interlocked.Increment(ref s_successes);
        public void RunEnded() { }
    }

    /// <summary>A tick that throws on every call on a 1 ms grid, then stops
    /// throwing.</summary>
    public static void Tick()
    {
        const string name = "LCC.ClockTick";
        var clock = new DeviceClock<FailingTick>(new FailingTick(), MissedTick.NextPoll, name);
        bool logged = Failing(clock, name, "a tick that throws", out bool bounded);
        int before = Volatile.Read(ref s_successes);
        Volatile.Write(ref s_fail, false);
        bool ticks = Program.WaitUntil(() => Volatile.Read(ref s_successes) >= before + 2);
        Checks.Check("once the tick stops throwing, the clock ticks again", ticks, $"{Volatile.Read(ref s_successes) - before} tick(s)");
        Diag.Logged("the end of the failures reaches the diagnostic log", name, Program.EndText, logged && bounded);
        Stops(clock, "Stop ends the clock");
    }

    /// <summary>A Running that throws on every call, and a Stop while it
    /// still throws, which ends the clock in its wait between two passes.</summary>
    public static void Running()
    {
        const string name = "LCC.ClockRunning";
        var clock = new DeviceClock<FailingRunning>(new FailingRunning(), MissedTick.NextPoll, name);
        Failing(clock, name, "a Running that throws", out _);
        Stops(clock, "Stop ends the clock while every pass still throws");
    }

    static bool Failing(IDeviceClock clock, string name, string what, out bool bounded)
    {
        Volatile.Write(ref s_fail, true);
        clock.Start();
        bool again = Program.WaitUntil(() => Volatile.Read(ref s_attempts) >= 2);
        Checks.Check($"the clock calls {what} again after it threw", again, $"{Volatile.Read(ref s_attempts)} call(s)");
        bounded = Program.CheckBounded($"a clock on {what} every time", Program.Measure());
        // The first line went out before the pass after it began.
        return Diag.Failures(name, $"a pass threw System.InvalidOperationException: loop_containment_check: {what}", wait: false);
    }

    static void Stops(IDeviceClock clock, string what)
    {
        var sw = Stopwatch.StartNew();
        clock.Stop(Program.HangMs);
        Checks.Check(what, !clock.Thread.IsAlive, $"{sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>The input section as a protocol host's channel, with a write
    /// that throws while the case says so.</summary>
    sealed class FailingChannel : IHostChannel
    {
        public bool Fail = true;
        public int Writes, Written;

        public uint Write(byte[] data, int dataLen, byte[] extended, int extendedLen)
        {
            Interlocked.Increment(ref Writes);
            if (Volatile.Read(ref Fail)) throw new IOException("loop_containment_check: a frame write that fails");
            return (uint)Interlocked.Increment(ref Written);
        }

        public bool Delivered(uint seq, int waitMs) => true;
        public bool AcknowledgesDelivery => false;
    }

    /// <summary>A UMDF2 protocol host whose one layout goes out on its clock
    /// every 4 ms, on a channel whose write throws: ProtocolHost.Tick throws
    /// through HostTick into the clock.</summary>
    public static void ProtocolHostTick()
    {
        var p = ClockProfile(4);
        var compiled = CompiledProtocol.Compile(p, null) ?? throw new InvalidOperationException("the test profile has no protocol");
        var channel = new FailingChannel();
        const int index = 7961;
        string name = $"HMProtocolClock_{index}";
        ProtocolHostBody(p, compiled, channel, index, name);
    }

    /// <summary>A protocol clock period under 0.125 ms is refused at
    /// compile, as a stream period is: a period of ticks that rounds to
    /// nothing would make the clock thread tick on every pass.</summary>
    public static void ClockPeriodFloor()
    {
        foreach (var ms in new[] { 0.001, 0.1, 0.124 })
        {
            string msg = "";
            try { CompiledProtocol.Compile(ClockProfile(ms), null); }
            catch (Exception e) { msg = e.Message; }
            Checks.Check($"a protocol clock period of {ms} ms is refused", msg.Contains("clock.periodMs"), msg);
        }
        string ok = "";
        try { CompiledProtocol.Compile(ClockProfile(0.125), null); }
        catch (Exception e) { ok = e.Message; }
        Checks.Check("a protocol clock period of 0.125 ms is accepted", ok.Length == 0, ok);
    }

    static ControllerProfile ClockProfile(double periodMs)
    {
        // switch-pro's identity and descriptor with a protocol of the
        // probe's own, as protocol_engine_check builds its UMDF2 profiles.
        var o = JsonNode.Parse(Program.Catalog("switch-pro"))!.AsObject();
        o["id"] = "lcc-protocol";
        o["pid"] = "0xF0F1";
        foreach (var k in new[] { "stateBlock", "inputReports", "liveFeatureReports", "protocol" }) o.Remove(k);
        o["stateBlock"] = JsonNode.Parse("""{ "size": 4, "fields": [ { "byte": 0, "type": "button-mask", "buttons": [ "#0", "#1" ] } ] }""");
        o["inputReports"] = JsonNode.Parse("""
        [ { "name": "full", "reportId": "0x30", "size": 49, "program": [ { "const": { "0": "30" } }, { "copy": "0-1", "to": 3 } ] } ]
        """);
        o["protocol"] = JsonNode.Parse("""
        {
          "state": [ { "name": "seen" } ],
          "input": { "clock": { "periodMs": 4 } },
          "commands": { "rules": [ { "match": "01", "minLength": 11, "set": { "seen": 1 }, "reply": [ { "hex": "21" }, { "echo": "10" } ], "replyLength": 49 } ] },
          "replies": { "pending": 4, "overflow": "dropOldest" }
        }
        """);
        o["protocol"]!["input"]!["clock"]!["periodMs"] = periodMs;
        return Program.Profile(o.ToJsonString());
    }

    static void ProtocolHostBody(ControllerProfile p, CompiledProtocol compiled, FailingChannel channel, int index, string name)
    {
        var host = new ProtocolHost(p, compiled, index, channel, new object());
        host.Start();
        bool again = Program.WaitUntil(() => Volatile.Read(ref channel.Writes) >= 2);
        Checks.Check("the host's clock tries the next frame after a write that threw", again, $"{Volatile.Read(ref channel.Writes)} write(s)");
        bool bounded = Program.CheckBounded("a protocol host whose every frame fails to go out", Program.Measure());
        bool logged = Diag.Failures(name, "a pass threw System.IO.IOException: loop_containment_check: a frame write that fails", wait: false);
        int before = Volatile.Read(ref channel.Written);
        Volatile.Write(ref channel.Fail, false);
        bool sends = Program.WaitUntil(() => Volatile.Read(ref channel.Written) >= before + 2);
        Checks.Check("once writes succeed, the host sends its frames again", sends, $"{Volatile.Read(ref channel.Written) - before} frame(s)");
        Diag.Logged("the end of the failures reaches the diagnostic log", name, Program.EndText, logged && bounded);
        var sw = Stopwatch.StartNew();
        host.Dispose();
        var thread = host.ClockThread;
        Checks.Check("Dispose ends the host's clock", thread != null && thread.Join(Program.HangMs), $"{sw.Elapsed.TotalMilliseconds:F1} ms");
    }
}
