using System;
using System.Diagnostics;
using System.Threading;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal;

/// <summary>A profile's stream, compiled: its period in Stopwatch ticks, the
/// register a host sets it through, the condition it runs under, and what a
/// repeat does to the sensor timestamp.</summary>
internal sealed class StreamClock
{
    public long PeriodTicks { get; }
    public int PeriodRegister { get; } = -1;
    public double UnitTicks { get; }
    public Condition? While { get; }
    public bool AdvanceTimestamps { get; }

    /// <summary>Whether the stream reads the protocol's registers, through
    /// its condition or its period register. Nothing wakes the idle clock
    /// when a register changes, so such a stream keeps the clock's capped
    /// wait. A stream that reads none waits for its next repeat in one
    /// wait: a consumer's frame only moves the repeat later, which the
    /// clock sees when it wakes for the earlier one.</summary>
    public bool ReadsRegisters => While != null || PeriodRegister >= 0;

    private StreamClock(long period, int register, double unit, Condition? when, bool advance)
    {
        PeriodTicks = period; PeriodRegister = register; UnitTicks = unit; While = when; AdvanceTimestamps = advance;
    }

    /// <summary>The idle repeat of a profile that has one: its
    /// <c>stream</c>, or the whole-millisecond period of
    /// <c>extendedReport.idleFrameIntervalMs</c>. Null when it has neither.
    /// Throws naming the JSON path of a value it refuses.</summary>
    public static StreamClock? Compile(ControllerProfile profile, RegisterTable registers)
    {
        int idleMs = profile.ExtendedReport?.IdleFrameIntervalMs ?? 0;
        var s = profile.Stream;
        string Where(string key) => $"Profile '{profile.Id}': stream.{key}";
        if (s == null)
            return idleMs > 0 ? new StreamClock(Ticks(idleMs), -1, 0, null, advance: false) : null;
        if (idleMs > 0)
            throw new InvalidOperationException($"Profile '{profile.Id}': stream and extendedReport.idleFrameIntervalMs both set the idle repeat. Give one.");
        // From a microframe, the shortest period a USB host polls, to a
        // minute.
        if (s.PeriodMs is not double ms || !(ms >= 0.125) || !(ms <= 60_000))
            throw new InvalidOperationException($"{Where("periodMs")}: a period from 0.125 to 60000 ms.");
        int register = -1;
        double unit = 0;
        if (s.PeriodRegister is { } pr)
        {
            register = registers.IndexOf(pr.Name);
            if (register < 0) throw new InvalidOperationException($"{Where("periodRegister.name")}: '{pr.Name}' is not a register protocol.state declares.");
            if (!(pr.UnitMs > 0) || double.IsInfinity(pr.UnitMs)) throw new InvalidOperationException($"{Where("periodRegister.unitMs")}: a unit above 0.");
            unit = pr.UnitMs * Stopwatch.Frequency / 1000.0;
        }
        Condition? when = null;
        if (s.While != null)
        {
            try { when = ConditionParser.Parse(s.While, registers, "$.stream.while"); }
            catch (FormException e) { throw new InvalidOperationException(e.ForProfile(profile.Id), e); }
        }
        bool advance = s.Timestamps switch
        {
            null or "hold" => false,
            "advance" => true,
            _ => throw new InvalidOperationException($"{Where("timestamps")}: \"{s.Timestamps}\" is not one of hold, advance."),
        };
        return new StreamClock(Ticks(ms), register, unit, when, advance);
    }

    private static long Ticks(double ms) => Math.Max(1, (long)Math.Round(ms * Stopwatch.Frequency / 1000.0));

    /// <summary>The period now: the host's register times its unit, kept to
    /// the 0.125 ms to 60 s a profile's own period may take, or the profile's
    /// period while the register holds 0 or there is none.</summary>
    public long Period(uint[]? registers)
    {
        if (PeriodRegister < 0 || registers == null) return PeriodTicks;
        uint v = Volatile.Read(ref registers[PeriodRegister]);
        return v == 0 ? PeriodTicks : (long)Math.Clamp(v * UnitTicks, Ticks(0.125), Ticks(60_000));
    }

    /// <summary>A stamp moved on by the time since the frame it repeats, in
    /// the 1/3 microsecond ticks of HMGamepadState.SensorTimestamp. A stamp of
    /// 0 is no stamp, and stays 0: the codec stamps that frame from its own
    /// clock.</summary>
    public static uint Advance(uint stamp, long sinceTicks)
    {
        if (stamp == 0) return 0;
        long f = Stopwatch.Frequency;
        long thirds = sinceTicks / f * 3_000_000 + sinceTicks % f * 3_000_000 / f;
        uint advanced = unchecked(stamp + (uint)thirds);
        return advanced == 0 ? 1u : advanced;
    }
}

/// <summary>The idle repeat on the device clock: runs while the stream's
/// condition holds on the protocol's registers, and repeats the consumer's
/// last frame one period after the last frame went out.</summary>
internal struct StreamTick : IClockTick
{
    private readonly StreamClock _stream;
    private readonly uint[]? _registers;
    private readonly Action _repeat;
    private readonly Func<long> _lastFrame;

    public StreamTick(StreamClock stream, uint[]? registers, Action repeat, Func<long> lastFrame)
    {
        _stream = stream;
        _registers = registers;
        _repeat = repeat;
        _lastFrame = lastFrame;
    }

    public bool Running(out long origin)
    {
        origin = 0;
        return _stream.While == null || (_registers != null && _stream.While.Holds(_registers));
    }

    public long Period => _stream.Period(_registers);

    /// <summary>A consumer's own frame puts the next repeat one period after
    /// it, so a live consumer's cadence always wins.</summary>
    public long NotBefore
    {
        get
        {
            long last = _lastFrame();
            return last == 0 ? 0 : last + Period;
        }
    }

    public void Tick(long due) => _repeat();

    public void RunEnded() { }
}

/// <summary>The stream of a profile that reports on its own clock
/// (issue #72, <c>extendedReport.send</c> <c>"clock"</c>): a report each
/// period on a grid from the clock's origin, carrying the consumer's
/// latest frame. Nothing the consumer sends moves it.</summary>
internal readonly struct StreamGridTick : IClockTick
{
    private readonly long _period;
    private readonly long _origin;
    private readonly Action _send;

    public StreamGridTick(long period, long origin, Action send)
    {
        _period = period;
        _origin = origin;
        _send = send;
    }

    public bool Running(out long origin)
    {
        origin = _origin;
        return true;
    }

    public long Period => _period;

    public long NotBefore => 0;

    public void Tick(long due) => _send();

    public void RunEnded() { }
}
