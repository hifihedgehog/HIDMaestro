// gate-offline [runs] [calls]: offline, the SDK's frame writer through the
// signal gate (docs/design/signal-gate.md) on buffers of this process's own:
// an input section, a signal section with the capability set and an
// unnamed auto-reset event. No device, no driver, no elevation. The rows:
//   - a frame written with no signal, the writer's cost before the gate
//   - kept, nothing pending: no worker counted and the handoff empty, as a
//     worker leaves it once it took the last frame. The probe empties the
//     handoff before each call, in every row, so the rows differ only in
//     the writer's path
//   - kept, the last frame still pending: no worker counted and the last
//     frame's final SeqNo still there, as when frames come faster than the
//     worker takes them
//   - taken back: a worker counted, so the writer takes the frame back and
//     signals the event
// The rows run interleaved run by run, at high process priority. It builds
// against an earlier 2.1.0 SDK with -p:HmSdkDll, which times the same calls
// on that writer. An SDK without the gate prints that and runs nothing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro.Internal;

internal static partial class Program
{
    [DllImport("kernel32.dll", EntryPoint = "CreateEventW")]
    static extern IntPtr GateCreateEvent(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    static extern bool GateCloseHandle(IntPtr handle);

    static unsafe void GateSet(IntPtr p, int offset, int value) => Volatile.Write(ref *(int*)((byte*)p + offset), value);

    static unsafe int GateGet(IntPtr p, int offset) => Volatile.Read(ref *(int*)((byte*)p + offset));

    static int GateOffline(int runs, int calls)
    {
        var gated = GatedWriter();
        if (gated == null)
        {
            Console.WriteLine("gate-offline: this SDK's frame writer has no signal gate");
            return 0;
        }
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        IntPtr signal = Marshal.AllocHGlobal(256);
        IntPtr ev = GateCreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        try
        {
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[256], 0, signal, 256);
            GateSet(signal, 0, 1);
            GateSet(signal, 4, 1);
            var frame = new byte[63];
            for (int i = 0; i < frame.Length; i++) frame[i] = (byte)i;
            uint seq = 0;
            var rows = new (string Name, Action Call)[]
            {
                ("a frame written with no signal", () =>
                {
                    GateSet(signal, 68, 0);
                    SharedMemoryIO.WriteInputFrame(view, IntPtr.Zero, ref seq, frame, frame.Length, null);
                }),
                ("kept, nothing pending", () =>
                {
                    GateSet(signal, 64, 0);
                    GateSet(signal, 68, 0);
                    gated(view, ev, ref seq, frame, frame.Length, null, 0, default, 0, IntPtr.Zero, signal);
                }),
                ("kept, the last frame still pending", () =>
                {
                    GateSet(signal, 64, 0);
                    GateSet(signal, 68, GateGet(signal, 68) & ~1);
                    gated(view, ev, ref seq, frame, frame.Length, null, 0, default, 0, IntPtr.Zero, signal);
                }),
                ("taken back", () =>
                {
                    GateSet(signal, 64, 1);
                    GateSet(signal, 68, 0);
                    gated(view, ev, ref seq, frame, frame.Length, null, 0, default, 0, IntPtr.Zero, signal);
                }),
            };
            var perCall = new List<double>[rows.Length];
            for (int r = 0; r < rows.Length; r++)
            {
                perCall[r] = new List<double>(runs);
                for (int i = 0; i < Math.Min(calls, 2_000); i++) rows[r].Call();
            }
            for (int run = 0; run < runs; run++)
                for (int r = 0; r < rows.Length; r++)
                {
                    var call = rows[r].Call;
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < calls; i++) call();
                    long t1 = Stopwatch.GetTimestamp();
                    perCall[r].Add((t1 - t0) * 1e9 / Stopwatch.Frequency / calls);
                }
            for (int r = 0; r < rows.Length; r++)
                Report($"gate-offline, {rows[r].Name}: ns per call", perCall[r], runs, "");
            return 0;
        }
        finally
        {
            if (ev != IntPtr.Zero) GateCloseHandle(ev);
            Marshal.FreeHGlobal(signal);
            Marshal.FreeHGlobal(view);
        }
    }
}
