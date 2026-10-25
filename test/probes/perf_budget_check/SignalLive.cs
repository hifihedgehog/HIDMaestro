// signal [runs]: live and elevated, the part of a frame mode call the
// SDK's own code does not decide. On a DualShock 4 and an Xbox 360 pad,
// each created as a program creates it, the probe times, with the
// controller's own section and events:
//   - SetEvent on the input event alone, which wakes the driver's worker
//   - SetEvent on the XUSB companion's doorbell alone (Xbox 360)
//   - a frame written to the section with no signal
//   - the same frame written by the same writer to a buffer of this
//     process's own, which no other process maps: the positive control
//     that tells the writer's cost from what other readers of the section
//     add to it
//   - a frame written and signaled, as every submit ended before 2.1.0.
//     From 2.1.0 the controller's device has the signal section, so its
//     driver counts itself and reads a frame it finds before it waits,
//     which a program before 2.1.0 does not give it: this row is the
//     writer before 2.1.0 against a driver that counts. A program before
//     2.1.0 on this driver is timed by that release's own probe. On this
//     driver the row reads slower than on v2.0.3's. It is not the
//     path a 2.1.0 submit takes: that is the gated row below
//   - from 2.1.0, a frame written and signaled through the signal gate
//     (docs/design/signal-gate.md), as a submit now ends: the signal only
//     while the driver waits. An SDK without the gate has no such row
//   - two rows at 1 kHz, where the worker is back in its wait before each
//     frame, as under a program that submits once a millisecond: each write
//     follows a 1 ms wait on a high-resolution waitable timer, and only the
//     write is timed. A run's sample is its mean ns per write.
//     "a submit's frame write at 1 kHz" is the write each release's submit
//     makes: the gated writer with the controller's signal view on an SDK
//     with the gate, else the writer that signals every frame, so the row
//     pairs v2.0.3's submit with 2.1.0's. "a frame written and signaled at
//     1 kHz" is the writer that signals every frame on both, which tells the
//     driver's part from the gate's. On an SDK with the gate an untimed pass
//     after them prints the share of paced writes that found InputWaiters
//     nonzero just before the write, which shows the row times the gate's
//     signaled branch
// The frames are the controller's own last report and continue its
// sequence numbers, so the driver sends each as it sends a submit's. It
// builds against b4d29d9 too, whose writer takes the same arguments, so the
// two releases' drivers and companions are timed by the same calls.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int Signal(int runs)
    {
        if (!Elevated("signal")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var a = new HMGamepadState { Buttons = HMButton.A };
        var b = new HMGamepadState { Buttons = HMButton.B };
        foreach (string id in new[] { Ds4, Xbox360 })
        {
            using var c = ctx.CreateController(ctx.GetProfile(id)!, s_key);
            if (!OnEmbeddedDriver(c, id)) return 5;
            // A submit first, so the section holds this controller's frame.
            c.SubmitState(a);
            c.SubmitState(b);
            var t = typeof(HMController);
            IntPtr view = (IntPtr)t.GetField("_inputView", Any)!.GetValue(c)!;
            IntPtr input = (IntPtr)t.GetField("_inputEvent", Any)!.GetValue(c)!;
            IntPtr companion = (IntPtr)t.GetField("_companionInputEvent", Any)!.GetValue(c)!;
            var seqField = t.GetField("_inputSeqNo", Any)!;
            uint seq = (uint)seqField.GetValue(c)!;
            // The data lane as the last submit left it.
            int dataLen = Marshal.ReadInt32(view, 4);
            var frame = new byte[Math.Max(1, dataLen)];
            Marshal.Copy(view + 8, frame, 0, frame.Length);
            var gip = new byte[14];
            Marshal.Copy(view + 8 + 256, gip, 0, gip.Length);
            byte[]? gipOrNull = companion != IntPtr.Zero ? gip : null;
            Measure($"SetEvent, the input event, {id}", runs, 20_000, () => SignalSetEvent(input), gate: false);
            if (companion != IntPtr.Zero)
                Measure($"SetEvent, the companion's doorbell, {id}", runs, 20_000, () => SignalSetEvent(companion), gate: false);
            Measure($"a frame written with no signal, {id}", runs, 20_000, () =>
                SharedMemoryIO.WriteInputFrame(view, IntPtr.Zero, ref seq, frame, dataLen, gipOrNull), gate: false);
            IntPtr own = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            try
            {
                uint ownSeq = 0;
                Measure($"a frame written to this process's own memory, {id}", runs, 20_000, () =>
                    SharedMemoryIO.WriteInputFrame(own, IntPtr.Zero, ref ownSeq, frame, dataLen, gipOrNull), gate: false);
            }
            finally { Marshal.FreeHGlobal(own); }
            Action signaled = () =>
                SharedMemoryIO.WriteInputFrame(view, input, ref seq, frame, dataLen, gipOrNull, companionEvent: companion);
            Measure($"a frame written and signaled, {id}", runs, 20_000, signaled, gate: false);
            var gated = GatedWriter();
            IntPtr signal = IntPtr.Zero;
            Action submit = signaled;
            if (gated != null)
            {
                signal = (IntPtr)t.GetField("_signalView", Any)!.GetValue(c)!;
                submit = () => gated(view, input, ref seq, frame, dataLen, gipOrNull, 0, default, 0, companion, signal);
                Measure($"a frame written and signaled through the signal gate, {id}", runs, 20_000, submit, gate: false);
            }
            PacedRows(id, submit, signaled, signal);
            // The controller's next frame follows the probe's.
            seqField.SetValue(c, seq);
        }
        return 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "SetEvent")]
    static extern bool SignalSetEvent(IntPtr handle);

    // The paced rows: 20 runs of 500 writes, a write a millisecond, and an
    // untimed pass of 1,500. Four rows and two passes make 43,200 waits a
    // launch across the two controllers, 43 to 65 s for a 1 ms wait that
    // takes 1.0 to 1.5 ms (1.52 ms measured at idle priority, 2026-10-25).
    const int PacedRuns = 20, PacedWrites = 500, PacedWarmup = 50, PacedCheckWrites = 1_500;
    // InputWaiters in Global\HIDMaestroSignal<N> (driver\hm_contract.h), as a
    // literal so this source builds against an SDK without the gate.
    const int PacedWaitersOffset = 64;
    const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003, Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    static extern IntPtr PacedCreateTimer(IntPtr attributes, IntPtr name, uint flags, uint access);
    [DllImport("kernel32.dll", EntryPoint = "SetWaitableTimer", SetLastError = true)]
    static extern bool PacedSetTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    static extern uint PacedWaitObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    static extern bool PacedCloseHandle(IntPtr handle);

    /// <summary>The 1 kHz rows of one controller. signal is its signal view
    /// on an SDK with the gate, else zero, which skips the check pass.</summary>
    static void PacedRows(string id, Action submit, Action signaled, IntPtr signal)
    {
        IntPtr timer = PacedCreateTimer(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (timer == IntPtr.Zero)
        {
            Console.WriteLine($"  the 1 kHz rows, {id}: no high-resolution waitable timer (error {Marshal.GetLastWin32Error()}), none timed");
            return;
        }
        try
        {
            PacedMeasure($"a submit's frame write at 1 kHz, {id}", timer, submit);
            PacedMeasure($"a frame written and signaled at 1 kHz, {id}", timer, signaled);
            if (signal != IntPtr.Zero) PacedCheck(id, timer, submit, signal);
        }
        finally { PacedCloseHandle(timer); }
    }

    /// <summary>A 1 ms wait on the high-resolution timer, relative, in
    /// 100 ns units.</summary>
    static void PacedTick(IntPtr timer)
    {
        long due = -10_000;
        if (!PacedSetTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        PacedWaitObject(timer, Infinite);
    }

    /// <summary>A row in Measure's line format: a run is PacedWrites paced
    /// writes, its sample the mean of the writes alone, each between two
    /// Stopwatch timestamps with the wait outside them.</summary>
    static void PacedMeasure(string name, IntPtr timer, Action write)
    {
        for (int i = 0; i < PacedWarmup; i++) { PacedTick(timer); write(); }
        var perCall = new List<double>(PacedRuns);
        long worstBytes = 0;
        for (int run = 0; run < PacedRuns; run++)
        {
            long ticks = 0;
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < PacedWrites; i++)
            {
                PacedTick(timer);
                long t0 = Stopwatch.GetTimestamp();
                write();
                ticks += Stopwatch.GetTimestamp() - t0;
            }
            worstBytes = Math.Max(worstBytes, GC.GetAllocatedBytesForCurrentThread() - bytes0);
            perCall.Add(ticks * 1e9 / Stopwatch.Frequency / PacedWrites);
        }
        Report($"{name}: ns per call", perCall, PacedRuns, $", most bytes allocated in one run of {PacedWrites} calls {worstBytes}");
    }

    /// <summary>Untimed: paced writes through the submit's writer, each
    /// after a read of InputWaiters, and the share that found it nonzero. A
    /// write that finds a worker counted takes the gate's signaled branch,
    /// unless the worker leaves its wait between the read and the write's
    /// recheck. A plain line, not a row.</summary>
    static void PacedCheck(string id, IntPtr timer, Action submit, IntPtr signal)
    {
        int found = 0;
        for (int i = 0; i < PacedCheckWrites; i++)
        {
            PacedTick(timer);
            if (Marshal.ReadInt32(signal, PacedWaitersOffset) != 0) found++;
            submit();
        }
        Console.WriteLine(FormattableString.Invariant(
            $"  the 1 kHz submit write, {id}, untimed: {found} of {PacedCheckWrites} paced writes found InputWaiters nonzero just before the write ({100.0 * found / PacedCheckWrites:F1} percent)"));
    }

    delegate void GatedWrite(IntPtr view, IntPtr eventHandle, ref uint seqNo, ReadOnlySpan<byte> data, int dataLen,
        byte[]? gipData, int dataOffset, ReadOnlySpan<byte> extendedData, int extendedLen, IntPtr companionEvent, IntPtr signalView);

    /// <summary>The SDK's frame writer with its signal-gate argument
    /// (2.1.0), or null on an SDK without one, found by its signature so
    /// this source builds against v2.0.3's SDK as it is.</summary>
    static GatedWrite? GatedWriter()
    {
        var m = typeof(SharedMemoryIO).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == "WriteInputFrame" && x.GetParameters().Any(p => p.Name == "signalView"));
        return m == null ? null : m.CreateDelegate<GatedWrite>();
    }
}
