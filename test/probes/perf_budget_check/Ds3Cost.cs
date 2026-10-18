// ds3cost [runs] [calls]: the CPU of one frame across both processes, the
// SDK's and the driver's, for dualshock-3-full and switch-pro, offline, at
// 9bd6279 and in this tree (the plan's budget "CPU per frame across both
// processes": at or under 9bd6279's).
//
// The driver's side is native: hm9bd6279.dll, built by native\build.ps1
// from 9bd6279's own DualShock 3 and Switch Pro functions and the frame code
// around them, and this tree's frame, a section read and two copies. The
// SDK's side is this tree's code: the state program SubmitState runs, and
// the protocol engine's reports over the bound set, generated and
// interpreted. Built with -p:HmBaselineTree against a 9bd6279 checkout
// (and -p:HmDd4Base=true, which the program mode needs there), the mode
// measures 9bd6279's SDK: the codec over dualshock-3-full's
// extendedReport and SwitchProPacker.BuildBody.
//
// One frame:
//   dualshock-3-full at 9bd6279: the codec's native report, the driver's
//     derivation of the joystick report and its copy, and one GET of
//     feature 0 derived from the kept report
//   dualshock-3-full here: the state program, the joystick report and the
//     live feature report the host builds, the driver's frame with the live
//     store, and one GET copied from the store
//   switch-pro 0x30 and 0x3F at 9bd6279: the packer's body, the worker's
//     turn on the frame the SDK wrote, the stream thread's frame from the
//     section
//   switch-pro here: the state block's encode, the host's copy of it and
//     the frame BuildNext builds on a tick, the driver's frame
// The section writes, the same in both designs, are left out. One launch
// prints each part's median ns per call over the runs and the bytes the
// thread allocated per call, then the totals. A script launches it 30 times
// or more and takes p50 and p95 over the launches.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
#if !HM_BASELINE
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;
#endif

internal static unsafe class Ds3Cost
{
    const int SectionSize = 362, Count = 64;

    [DllImport("hm9bd6279.dll")] static extern long OldSwitchFull(byte* sections, int count, int calls);
    [DllImport("hm9bd6279.dll")] static extern long OldSwitchSimple(byte* sections, int count, int calls);
    [DllImport("hm9bd6279.dll")] static extern long OldSwitchSubmit(byte* sections, int count, int calls);
    [DllImport("hm9bd6279.dll")] static extern long OldDs3Frame(byte* sections, int count, int calls);
    [DllImport("hm9bd6279.dll")] static extern long OldDs3Feature(int calls);
    [DllImport("hm9bd6279.dll")] static extern long NewFrame(byte* sections, int count, int calls, int ext, int live);
    [DllImport("hm9bd6279.dll")] static extern long NewFeature(int calls, int live);
    [DllImport("hm9bd6279.dll")] static extern uint RingSum();

    /// <summary>Sections as the SDK leaves them: the data lane at
    /// <paramref name="data"/>, the extended lane at <paramref name="ext"/>.</summary>
    static byte[] Sections(Func<int, byte[]> data, Func<int, byte[]> ext)
    {
        var s = GC.AllocateArray<byte>(SectionSize * Count, pinned: true);
        for (int i = 0; i < Count; i++)
        {
            var d = data(i);
            var e = ext(i);
            int at = i * SectionSize;
            BitConverter.TryWriteBytes(s.AsSpan(at, 4), (uint)(2 * i + 2));
            BitConverter.TryWriteBytes(s.AsSpan(at + 4, 4), (uint)d.Length);
            d.CopyTo(s, at + 8);
            BitConverter.TryWriteBytes(s.AsSpan(at + 8 + 256 + 14, 4), (uint)e.Length);
            e.CopyTo(s, at + 8 + 256 + 14 + 4);
        }
        return s;
    }

    public static void Run(int runs, int calls)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var all = ProfileDatabase.LoadEmbedded().All;
        var ds3 = all.First(x => x.Id == "dualshock-3-full");
        var sw = all.First(x => x.Id == "switch-pro");
        var rng = new Random(20261018);
        var states = new HMGamepadState[Count];
        var slots = new float[Count * 6];
        for (int i = 0; i < Count; i++)
        {
            states[i] = new HMGamepadState
            {
                Buttons = (HMButton)(uint)rng.Next(), BatteryLevel = (byte)rng.Next(11),
                AccelGY = (float)(rng.NextDouble() * 4 - 2), GyroDpsX = (float)(rng.NextDouble() * 400 - 200),
            };
            for (int k = 0; k < 6; k++) slots[i * 6 + k] = (float)rng.NextDouble();
        }
        var parts = new List<(string Name, Func<int, double> Ns)>();
        double Managed(Action<int> batch, int n)
        {
            long t0 = Stopwatch.GetTimestamp();
            batch(n);
            return (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / n;
        }
        double Ticks(long t, int n) => t * 1e9 / Stopwatch.Frequency / n;

        // The native reports and bodies the SDK writes, through the codec.
        var ds3Spec = ds3.ExtendedReport!;
        var raws = Enumerable.Range(0, Count).Select(i =>
        {
            var b = new byte[ds3Spec.Size];
            VendorBlobCodec.EncodeInput(ds3Spec, in states[i], slots[i * 6], slots[i * 6 + 1], slots[i * 6 + 2], slots[i * 6 + 3], slots[i * 6 + 4], slots[i * 6 + 5], b, new VendorBlobCodec.EncoderState());
            return b;
        }).ToArray();
        var codecBuf = new byte[ds3Spec.Size];
        var codecState = new VendorBlobCodec.EncoderState();
        parts.Add(("ds3-sdk-codec", n => Managed(m =>
        {
            for (int i = 0; i < m; i++)
            {
                int j = i & (Count - 1);
                VendorBlobCodec.EncodeInput(ds3Spec, in states[j], slots[j * 6], slots[j * 6 + 1], slots[j * 6 + 2], slots[j * 6 + 3], slots[j * 6 + 4], slots[j * 6 + 5], codecBuf, codecState);
            }
        }, n)));
        var oldDs3 = Sections(_ => Array.Empty<byte>(), i => raws[i]);
        parts.Add(("ds3-driver-9bd6279", n => { fixed (byte* p = oldDs3) return Ticks(OldDs3Frame(p, Count, n), n); }));
        parts.Add(("ds3-get-9bd6279", n => Ticks(OldDs3Feature(n), n)));

#if HM_BASELINE
        var swBody = new byte[48];
        parts.Add(("sw-sdk-packer", n => Managed(m =>
        {
            for (int i = 0; i < m; i++)
            {
                int j = i & (Count - 1);
                SwitchProPacker.BuildBody(in states[j], slots[j * 6], slots[j * 6 + 1], slots[j * 6 + 2], slots[j * 6 + 3], swBody);
            }
        }, n)));
        var bodies = raws.Select(r => r[..48]).ToArray();
#else
        var swModel = sw.GetOrCompileModel();
        var bodies = Enumerable.Range(0, Count).Select(i =>
        {
            var b = new byte[48];
            VendorBlobCodec.EncodeInput(sw.StateBlockSpecForCodec(), in states[i], slots[i * 6], slots[i * 6 + 1], slots[i * 6 + 2], slots[i * 6 + 3], 0, 0, b, new VendorBlobCodec.EncoderState());
            return b;
        }).ToArray();
        foreach (var (name, p, model) in new[] { ("ds3", ds3, ds3.GetOrCompileModel()), ("sw", sw, swModel) })
        {
            if (model.StateProgram == null)
            {
                // A state block the state program does not lower is the
                // codec's, as HMController encodes it.
                var spec = p.StateBlockSpecForCodec();
                var blockBuf = new byte[spec.Size];
                var enc = new VendorBlobCodec.EncoderState();
                Action<int> codec = m =>
                {
                    for (int i = 0; i < m; i++)
                    {
                        int j = i & (Count - 1);
                        VendorBlobCodec.EncodeInput(spec, in states[j], slots[j * 6], slots[j * 6 + 1], slots[j * 6 + 2], slots[j * 6 + 3], slots[j * 6 + 4], slots[j * 6 + 5], blockBuf, enc);
                    }
                };
                parts.Add(($"{name}-sdk-state", n => Managed(codec, n)));
                parts.Add(($"{name}-sdk-state-interpreted", n => Managed(codec, n)));
                continue;
            }
            foreach (bool interpret in new[] { false, true })
            {
                var state = ProgramBinder.BindState(model.StateProgram!, interpret);
                var buf = GC.AllocateArray<byte>(model.StateProgram!.Spec.Size, pinned: true);
                parts.Add(($"{name}-sdk-state{(interpret ? "-interpreted" : "")}", n => Managed(m =>
                {
                    float* sl = stackalloc float[6];
                    fixed (byte* d = buf)
                        for (int i = 0; i < m; i++)
                        {
                            int j = i & (Count - 1);
                            for (int k = 0; k < 6; k++) sl[k] = slots[j * 6 + k];
                            state.Run(in states[j], sl, d);
                        }
                }, n)));
            }
        }
        // The host's reports: dualshock-3-full's joystick report and live
        // feature on each submit, switch-pro's 0x30 and 0x3F on each tick.
        foreach (bool interpret in new[] { false, true })
        {
            string tail = interpret ? "-interpreted" : "";
            var d3 = Engine(ds3, interpret);
            var live = new byte[64];
            var input = new SubmitLoop { Engine = d3.Engine, Blocks = raws };
            parts.Add(("ds3-sdk-input" + tail, n => Managed(m => { input.Calls = m; d3.Runner.Visit(input); }, n)));
            parts.Add(("ds3-sdk-live" + tail, n => Managed(m =>
            {
                for (int i = 0; i < m; i++) { d3.Engine.SetStateBlock(raws[i & (Count - 1)]); d3.Engine.BuildLiveFeature(0x00, live); }
            }, n)));
            foreach (var (kind, seen) in new[] { ("full", true), ("simple", false) })
            {
                var s = Engine(sw, interpret);
                if (seen)
                {
                    var cmd = new byte[49];
                    cmd[0] = 0x01; cmd[10] = 0x02;
                    s.Engine.HandleCommand(cmd, 0);
                    var tmp = new byte[64];
                    while (s.Engine.TakeReply(64, tmp) > 0) { }
                }
                var loop = new TickLoop { Engine = s.Engine, Blocks = bodies };
                parts.Add(($"sw-sdk-{kind}" + tail, n => Managed(m => { loop.Calls = m; s.Runner.Visit(loop); }, n)));
            }
        }
        // The protocol host as the controller drives it, on a channel that
        // writes nothing (the section writes are left out, as above): a
        // submit, OnState, and for switch-pro a tick of its clock after it.
        {
            var d3Host = Host(ds3);
            parts.Add(("ds3-sdk-host", n => Managed(m => { for (int i = 0; i < m; i++) d3Host.OnState(raws[i & (Count - 1)]); }, n)));
            foreach (var (kind, seen) in new[] { ("full", true), ("simple", false) })
            {
                var h = Host(sw);
                if (seen)
                {
                    var cmd = new byte[49];
                    cmd[0] = 0x01; cmd[10] = 0x02;
                    h.OnOutput(cmd, 49, 0);
                    h.Tick(0);
                }
                parts.Add(($"sw-sdk-host-{kind}", n => Managed(m =>
                {
                    for (int i = 0; i < m; i++) { h.OnState(bodies[i & (Count - 1)]); h.Tick(0); }
                }, n)));
            }
        }
        var newDs3 = Sections(i => raws[i], i => raws[i][..12]);
        parts.Add(("ds3-driver-here", n => { fixed (byte* p = newDs3) return Ticks(NewFrame(p, Count, n, 12, 49), n); }));
        parts.Add(("ds3-get-here", n => Ticks(NewFeature(n, 49), n)));
        var newSw = Sections(_ => Array.Empty<byte>(), i => raws[i]);
        parts.Add(("sw-full-driver-here", n => { fixed (byte* p = newSw) return Ticks(NewFrame(p, Count, n, 49, 0), n); }));
        parts.Add(("sw-simple-driver-here", n => { fixed (byte* p = newSw) return Ticks(NewFrame(p, Count, n, 12, 0), n); }));
#endif
        var oldSw = Sections(i => bodies[i], _ => Array.Empty<byte>());
        parts.Add(("sw-full-driver-9bd6279", n => { fixed (byte* p = oldSw) return Ticks(OldSwitchFull(p, Count, n), n); }));
        parts.Add(("sw-simple-driver-9bd6279", n => { fixed (byte* p = oldSw) return Ticks(OldSwitchSimple(p, Count, n), n); }));
        parts.Add(("sw-submit-driver-9bd6279", n => { fixed (byte* p = oldSw) return Ticks(OldSwitchSubmit(p, Count, n), n); }));

        // Past the tiered JIT's call-count threshold and its background
        // recompile, as program mode warms up.
        foreach (var (_, ns) in parts) ns(Math.Min(calls, 20_000));
        for (int w = 0; w < 60; w++) foreach (var (_, ns) in parts) ns(100);
        Thread.Sleep(500);
        var times = parts.Select(_ => new List<double>()).ToList();
        var bytes = new long[parts.Count];
        for (int r = 0; r < runs; r++)
            for (int k = 0; k < parts.Count; k++)
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                times[k].Add(parts[k].Ns(calls));
                bytes[k] = Math.Max(bytes[k], GC.GetAllocatedBytesForCurrentThread() - b0);
            }
        var median = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int k = 0; k < parts.Count; k++)
        {
            var t = times[k];
            t.Sort();
            median[parts[k].Name] = t[t.Count / 2];
            Console.WriteLine(FormattableString.Invariant($"ds3cost,{parts[k].Name},{t[t.Count / 2]:F3},{bytes[k] / Math.Max(1, calls)}"));
        }
        double Sum(params string[] names) => names.All(median.ContainsKey) ? names.Sum(n => median[n]) : double.NaN;
        void Total(string name, double v) { if (!double.IsNaN(v)) Console.WriteLine(FormattableString.Invariant($"ds3cost-total,{name},{v:F3}")); }
        Total("ds3-9bd6279", Sum("ds3-sdk-codec", "ds3-driver-9bd6279", "ds3-get-9bd6279"));
        Total("sw-full-9bd6279", Sum("sw-sdk-packer", "sw-submit-driver-9bd6279", "sw-full-driver-9bd6279"));
        Total("sw-simple-9bd6279", Sum("sw-sdk-packer", "sw-submit-driver-9bd6279", "sw-simple-driver-9bd6279"));
        foreach (string tail in new[] { "", "-interpreted" })
        {
            Total("ds3-engine-here" + tail, Sum("ds3-sdk-state" + tail, "ds3-sdk-input" + tail, "ds3-sdk-live" + tail, "ds3-driver-here", "ds3-get-here"));
            Total("sw-full-engine-here" + tail, Sum("sw-sdk-state" + tail, "sw-sdk-full" + tail, "sw-full-driver-here"));
            Total("sw-simple-engine-here" + tail, Sum("sw-sdk-state" + tail, "sw-sdk-simple" + tail, "sw-simple-driver-here"));
        }
        // The budget's totals: the state block, the host as the controller
        // drives it, the driver.
        Total("ds3-here", Sum("ds3-sdk-state", "ds3-sdk-host", "ds3-driver-here", "ds3-get-here"));
        Total("sw-full-here", Sum("sw-sdk-state", "sw-sdk-host-full", "sw-full-driver-here"));
        Total("sw-simple-here", Sum("sw-sdk-state", "sw-sdk-host-simple", "sw-simple-driver-here"));
        Console.WriteLine($"ds3cost,ring,{RingSum():X8}");
    }

#if !HM_BASELINE
    sealed class NullChannel : IHostChannel
    {
        uint _seq;
        public uint Write(byte[] data, int dataLen, byte[] extended, int extendedLen) => ++_seq;
        public bool Delivered(uint seq, int waitMs) => true;
        public bool AcknowledgesDelivery => false;
    }

    static ProtocolHost Host(ControllerProfile p) =>
        new(p, CompiledProtocol.Compile(p, null)!, 0, new NullChannel(), new object());

    static (ProtocolEngine Engine, LayoutRunner Runner) Engine(ControllerProfile p, bool interpret)
    {
        var compiled = CompiledProtocol.Compile(p, null)!;
        var runner = ProgramBinder.BindLayouts(p.GetOrCompileModel(), interpret)!;
        var e = new ProtocolEngine(compiled, "HM-PERF-0001", 0, runner);
        e.SetStateBlock(p.StateBlockAtRest());
        return (e, runner);
    }

    /// <summary>The submit's call, BuildOnSubmit over the set's own type,
    /// with the state block replaced each call as SubmitState replaces it.</summary>
    sealed class SubmitLoop : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public byte[][] Blocks = null!;
        public int Calls;
        readonly byte[] _report = new byte[64];
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet
        {
            int n = 0;
            for (int i = 0; i < Calls; i++)
            {
                Engine.SetStateBlock(Blocks[i & (Count - 1)]);
                n += Engine.BuildOnSubmit(ref set, _report);
            }
            return n;
        }
    }

    /// <summary>The device clock's call, BuildNext over the set's own type.
    /// Each call's state block differs from the last, so a layout sent on
    /// change goes out each time.</summary>
    sealed class TickLoop : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public byte[][] Blocks = null!;
        public int Calls;
        readonly byte[] _report = new byte[64];
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet
        {
            int n = 0;
            for (int i = 0; i < Calls; i++)
            {
                Engine.SetStateBlock(Blocks[i & (Count - 1)]);
                n += Engine.BuildNext(ref set, _report);
            }
            return n;
        }
    }
#endif
}
