using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

/// <summary>The idle repeat sends the state the consumer submitted. A
/// consumer reuses one HMGamepadState and its Axes dictionary, as
/// HMGamepadState.Axes tells it to, and starts rewriting the dictionary for
/// its next frame once SubmitState returns. The repeat encodes the last
/// state again on the device clock's thread a period after the frame, so it
/// must encode the state as submitted, never the dictionary as the consumer
/// has left it since. The catalog's dualsense-bt repeats every 1.25 ms, on
/// its descriptor's report until a host arms it and on report 0x31 after.
/// The check calls the repeat itself on a controller whose clock is stopped,
/// after each way a consumer rewrites its dictionary, and runs the
/// controller's own clock while the consumer rewrites the dictionary across
/// the repeat's time and a reader takes every frame it can.</summary>
internal static class RepeatState
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const int States = 100;

    // A stick value for state k, the same on X and on Z. Consecutive states
    // differ, and none is the rest a missing key reads as, so a frame whose
    // X and Z come from two states, or from a state and the rest, is none
    // of the states.
    static float V(int k) => (1 + k % States) / 255f;

    public static void Run()
    {
        Console.WriteLine("-- the idle repeat sends the state as submitted while the consumer reuses its dictionary");
        string json = Device.Catalog("dualsense-bt");
        Interleaved(json, armed: false, 7960);
        Interleaved(json, armed: true, 7961);
        Concurrent(json, 7962, 7963);
    }

    /// <summary>The repeat called a period after each submit, once the
    /// consumer has begun its next frame in one of four ways: one value
    /// rewritten in place, one key removed, the dictionary cleared and
    /// partly refilled, or every key rewritten for a state it has not
    /// submitted. A fifth leaves the dictionary alone, the control. The
    /// bytes compared are the ones the state decides: the descriptor's
    /// report whole, or the sticks of report 0x31, since that report's
    /// counters, stamp and CRC move every time it goes out.</summary>
    static void Interleaved(string json, bool armed, int index)
    {
        using var d = new SendClock.Offline(json, index);
        d.Clock?.Stop(1000);
        if (armed) typeof(HMController).GetField("_extendedModeArmed", Instance)!.SetValue(d.C, true);
        long period = ((StreamClock)typeof(HMController).GetField("_stream", Instance)!.GetValue(d.C)!).PeriodTicks;
        var spec = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json))!.ExtendedReport!;
        int[] sticks = new[] { "leftStickX", "leftStickY", "rightStickX", "rightStickY" }
            .Select(s => spec.Fields.First(f => f.Semantic == s).Byte!.Value).ToArray();
        byte[] Frame()
        {
            if (!armed) return Data(d.View);
            var e = d.Ext();
            return sticks.Select(i => e[i]).ToArray();
        }

        var axes = new Dictionary<HMAxis, float>();
        var state = new HMGamepadState { Axes = axes };
        int skipped = 0, alike = 0;
        var mixed = new int[5];
        var first = new string[5];
        byte[]? previous = null;
        for (int k = 0; k < States; k++)
        {
            axes[HMAxis.X] = V(k);
            axes[HMAxis.Z] = V(k);
            d.C.SubmitState(in state);
            long sent = Stopwatch.GetTimestamp();
            byte[] submitted = Frame();
            if (previous != null && previous.AsSpan().SequenceEqual(submitted)) alike++;
            previous = submitted;
            int shape = k % 5;
            switch (shape)
            {
                case 0: axes[HMAxis.X] = V(k + 1); break;
                case 1: axes.Remove(HMAxis.Z); break;
                case 2: axes.Clear(); axes[HMAxis.X] = V(k + 1); break;
                case 3: axes[HMAxis.X] = V(k + 1); axes[HMAxis.Z] = V(k + 1); break;
            }
            // The clock repeats a frame a period after it, and a repeat
            // called sooner sends nothing.
            while (Stopwatch.GetTimestamp() - sent < period) Thread.SpinWait(20);
            uint seq = d.Seq;
            d.Tick();
            if (d.Seq == seq) { skipped++; continue; }
            byte[] repeated = Frame();
            if (repeated.AsSpan().SequenceEqual(submitted)) continue;
            if (mixed[shape]++ == 0) first[shape] = $", state {k} submitted {Checks.Hex(submitted)}, repeated {Checks.Hex(repeated)}";
        }
        // The copy the repeat keeps is rewritten in place, so once it has
        // held the state's axes a submit allocates nothing.
        for (int i = 0; i < 2000; i++) { axes[HMAxis.X] = V(i); axes[HMAxis.Z] = V(i); d.C.SubmitState(in state); }
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) { axes[HMAxis.X] = V(i); axes[HMAxis.Z] = V(i); d.C.SubmitState(in state); }
        long submitBytes = GC.GetAllocatedBytesForCurrentThread() - b0;

        string what = armed ? "dualsense-bt armed, report 0x31's sticks" : "dualsense-bt, its descriptor's report";
        Check($"{what}: each repeat a period after a submit sends a frame", skipped == 0, $"{skipped} of {States} sent nothing");
        Check($"{what}: 10,000 SubmitState calls on a controller with its repeat allocate nothing", submitBytes == 0, $"{submitBytes} bytes");
        Check($"{what}: consecutive submits differ, so a repeat of another state shows", alike == 0, $"{alike} alike");
        Check($"{what}: a repeat of a state whose dictionary the consumer left alone is the frame submitted",
            mixed[4] == 0, $"{mixed[4]} of {States / 5} differ{first[4]}");
        Check($"{what}: the repeat is the state submitted after the consumer rewrote a value, removed a key, cleared its dictionary or wrote its next state",
            mixed[0] + mixed[1] + mixed[2] + mixed[3] == 0,
            $"{mixed[0]}, {mixed[1]}, {mixed[2]}, {mixed[3]} of {States / 5} differ{string.Concat(first.Take(4))}");
    }

    /// <summary>The repeat on the controller's own 1.25 ms clock while the
    /// consumer writes each state's X well before the repeat is due and its Z
    /// well after, then submits before a second repeat could come. A reader
    /// on another thread takes every frame it sees through the section's
    /// sequence number, and each must be the descriptor's report of the
    /// state at rest, which the clock repeats before the first submit, or of
    /// a state the consumer gave, as a twin of the profile without a stream
    /// encodes them. How many repeats the reader sees depends on the
    /// machine's load, so no count of them is checked.</summary>
    static void Concurrent(string json, int index, int twinIndex)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        o.Remove("stream");
        var known = new HashSet<string>(StringComparer.Ordinal);
        using (var t = new SendClock.Offline(o.ToJsonString(), twinIndex))
        {
            t.C.SubmitState(new HMGamepadState());
            known.Add(Checks.Hex(Data(t.View)));
            for (int k = 0; k < States; k++)
            {
                t.C.SubmitState(new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = V(k), [HMAxis.Z] = V(k) } });
                known.Add(Checks.Hex(Data(t.View)));
            }
        }

        using var d = new SendClock.Offline(json, index);
        long period = ((StreamClock)typeof(HMController).GetField("_stream", Instance)!.GetValue(d.C)!).PeriodTicks;
        var frames = new List<byte[]>();
        int stop = 0;
        IntPtr view = d.View;
        var reader = new Thread(() =>
        {
            var buffer = new byte[SharedMemoryIO.DATA_CAPACITY];
            uint last = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                if (!TryRead(view, buffer, out int n, out uint seq) || seq == last) { Thread.SpinWait(8); continue; }
                last = seq;
                frames.Add(buffer.AsSpan(0, n).ToArray());
            }
        }) { IsBackground = true, Name = "RepeatState reader" };
        reader.Start();

        const int Submits = 400;
        var axes = new Dictionary<HMAxis, float> { [HMAxis.X] = V(0), [HMAxis.Z] = V(0) };
        var state = new HMGamepadState { Axes = axes };
        d.C.SubmitState(in state);
        long at = Stopwatch.GetTimestamp();
        for (int k = 1; k < Submits; k++)
        {
            Until(at + period / 3);
            axes[HMAxis.X] = V(k);
            Until(at + period * 8 / 5);
            axes[HMAxis.Z] = V(k);
            Until(at + period * 19 / 10);
            d.C.SubmitState(in state);
            at = Stopwatch.GetTimestamp();
        }
        Until(at + period * 3);
        Volatile.Write(ref stop, 1);
        reader.Join();

        int mixed = 0;
        string first = "";
        foreach (var f in frames)
        {
            if (known.Contains(Checks.Hex(f))) continue;
            if (mixed++ == 0) first = $", first {Checks.Hex(f)}";
        }
        Check("dualsense-bt on its own clock: every frame a reader took while the consumer rewrote its dictionary is one state it gave",
            mixed == 0, $"{frames.Count} frames read for {Submits} submits, {mixed} mixed{first}");
    }

    static void Until(long at)
    {
        while (Stopwatch.GetTimestamp() < at) Thread.SpinWait(20);
    }

    /// <summary>The data lane as the last frame left it, read on the thread
    /// that wrote it.</summary>
    static byte[] Data(IntPtr view)
    {
        var b = new byte[Math.Clamp(Marshal.ReadInt32(view, 4), 0, SharedMemoryIO.DATA_CAPACITY)];
        Marshal.Copy(view + SharedMemoryIO.DATA_OFFSET, b, 0, b.Length);
        return b;
    }

    /// <summary>The data lane read from another thread as the driver reads
    /// the section: an even sequence number, the bytes, then the same
    /// number again, so a frame the writer is in the middle of is never
    /// taken.</summary>
    static unsafe bool TryRead(IntPtr view, byte[] into, out int length, out uint seq)
    {
        byte* v = (byte*)view;
        length = 0;
        seq = Volatile.Read(ref *(uint*)v);
        if ((seq & 1) != 0) return false;
        int n = Volatile.Read(ref *(int*)(v + 4));
        if ((uint)n > (uint)into.Length) return false;
        new ReadOnlySpan<byte>(v + SharedMemoryIO.DATA_OFFSET, n).CopyTo(into);
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref *(uint*)v) != seq) return false;
        length = n;
        return true;
    }
}
