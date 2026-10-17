// program [runs] [calls]: the Switch 2 Pro composite's reports, rumble decode
// and state block, offline, each through the code this tree runs: the
// protocol engine's BuildNext over the bound set, generated and interpreted,
// the profile's rumble decoder, and the codec over the profile's state block.
// Built with -p:HmDd4Base=true against the tree before lane DD-4,
// the same names measure the code they replace: Switch2ProDevice.BuildNextReport,
// Switch2ProPacker.DecodeRumble and Switch2ProPacker.BuildBody. One launch
// prints each variant's median ns per call and the bytes the thread
// allocated per call. A script launches it 30 times or more against each
// tree and takes p50 and p95 over the launches.
//
// idle <periodMs> [seconds]: one controller of dualsense-bt built offline,
// its idle repeat at the period, and the process's CPU cycles per second of
// wall time with the repeat running and nothing else submitting
// (QueryProcessCycleTime, since Windows charges thread time at the clock
// tick and a thread that wakes for microseconds between ticks reads as
// none), with the frames the repeat wrote per second. This tree runs the repeat on the device clock
// from the profile's stream, the tree before on IdleFrameLoop from
// extendedReport.idleFrameIntervalMs, whole milliseconds only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
#if !DD4_BASE
using HIDMaestro.Internal.Programs;
#endif

internal static unsafe class ProgramCost
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    const string Composite = "switch2-pro-controller-composite";

    static readonly string[] Setup09 = { "0391000D000800000100FFFFFFFFFFFF", "0C9100020004000027000000", "0C9100040004000020000000" };
    static readonly string[] Setup05 = { "0391000D000800000100FFFFFFFFFFFF", "0C9100020004000027000000", "0C9100040004000024000000", "0391000A0004000005000000" };

    public static void Run(int runs, int calls)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == Composite);
        var rng = new Random(20261017);
        var bodies = new byte[64][];
        for (int i = 0; i < bodies.Length; i++) { bodies[i] = new byte[24]; rng.NextBytes(bodies[i]); }
        var rumble = new byte[64][];
        for (int i = 0; i < rumble.Length; i++) { rumble[i] = new byte[33]; rng.NextBytes(rumble[i]); rumble[i][0] = 0x02; }
        var states = new HMGamepadState[64];
        var slots = new double[64 * 6];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = new HMGamepadState { Buttons = (HMButton)(uint)rng.Next(), AccelGY = (float)(rng.NextDouble() * 4 - 2), GyroDpsX = (float)(rng.NextDouble() * 400 - 200) };
            for (int k = 0; k < 6; k++) slots[i * 6 + k] = rng.NextDouble();
        }
        var variants = new List<(string Name, Action<int> Batch)>();
        var report = new byte[64];

#if DD4_BASE
        foreach (var (name, setup) in new[] { ("s2-report-09", Setup09), ("s2-report-05", Setup05) })
        {
            var dev = new Switch2ProDevice("HM-PERF-0001", "perf");
            foreach (var hex in setup) { dev.HandleBulkOut(Convert.FromHexString(hex), Stopwatch.GetTimestamp()); while (dev.TakeReply(512) != null) { } }
            variants.Add((name, n => { for (int i = 0; i < n; i++) { dev.SetBody(bodies[i & 63]); dev.BuildNextReport(report); } }));
        }
        variants.Add(("s2-decode", n => { for (int i = 0; i < n; i++) Switch2ProPacker.DecodeRumble(rumble[i & 63].AsSpan(1), out _, out _); }));
        var body = new byte[24];
        variants.Add(("s2-body", n =>
        {
            for (int i = 0; i < n; i++)
            {
                int j = i & 63;
                Switch2ProPacker.BuildBody(in states[j], slots[j * 6], slots[j * 6 + 1], slots[j * 6 + 2], slots[j * 6 + 3], slots[j * 6 + 4], slots[j * 6 + 5], body);
            }
        }));
#else
        var model = p.GetOrCompileModel();
        var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p, 0))!;
        foreach (bool interpret in new[] { false, true })
            foreach (var (name, setup) in new[] { ("s2-report-09", Setup09), ("s2-report-05", Setup05) })
            {
                var runner = ProgramBinder.BindLayouts(model, interpret)!;
                var engine = new ProtocolEngine(compiled, "HM-PERF-0001", 0, runner);
                engine.SetStateBlock(p.StateBlockAtRest());
                var tmp = new byte[512];
                foreach (var hex in setup) { engine.HandleCommand(Convert.FromHexString(hex), Stopwatch.GetTimestamp()); while (engine.TakeReply(512, tmp) >= 0) { } }
                var loop = new BuildLoop { Engine = engine, Bodies = bodies, Report = report };
                variants.Add((name + (interpret ? "-interpreted" : ""), n => { loop.Calls = n; runner.Visit(loop); }));
            }
        foreach (bool interpret in new[] { false, true })
        {
            var dec = ProgramBinder.BindDecoders(model, interpret)[0];
            var input = GC.AllocateArray<byte>(64 + 4, pinned: true);
            var output = GC.AllocateArray<byte>(8, pinned: true);
            // The decoder alone, on reports already in buffers with the
            // slack its reads need, as the code it replaces read the report
            // in place.
            var padded = rumble.Select(r => { var b = GC.AllocateArray<byte>(64 + 4, pinned: true); r.CopyTo(b, 0); return b; }).ToArray();
            variants.Add(("s2-decode" + (interpret ? "-interpreted" : ""), n =>
            {
                fixed (byte* dp = output)
                    for (int i = 0; i < n; i++)
                        fixed (byte* sp = padded[i & 63])
                            dec.Run(sp, dp);
            }));
            // As HMController.RunDecoder: the report's data as the output
            // ring hands it, after the ID, in the thread's 256-byte buffer,
            // through the decoder's RunOutput.
            var ring = rumble.Select(r => { var b = new byte[256]; r.AsSpan(1).CopyTo(b); return b; }).ToArray();
            variants.Add(("s2-decode-path" + (interpret ? "-interpreted" : ""), n =>
            {
                fixed (byte* dp = output)
                    for (int i = 0; i < n; i++)
                        dec.RunOutput(0x02, ring[i & 63], 32, input, dp);
            }));
        }
        var spec = p.StateBlockSpecForCodec();
        var enc = new VendorBlobCodec.EncoderState();
        var body = new byte[24];
        if (Environment.GetEnvironmentVariable("HM_PERF_PARTS") == "1")
            foreach (var (part, pick) in new (string, Func<FieldSpec, bool>)[] { ("buttons", f => f.Type == "button-mask"), ("sticks", f => f.Type == "int16-axis"), ("imu", f => f.Type == "int16-le"), ("none", f => false) })
            {
                var sub = new ExtendedReportSpec { Size = 24, Fields = spec.Fields.Where(pick).ToList() };
                var e2 = new VendorBlobCodec.EncoderState();
                variants.Add(("parts-" + part, n =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        int j = i & 63;
                        VendorBlobCodec.EncodeInput(sub, in states[j], (float)slots[j * 6], (float)slots[j * 6 + 1], (float)slots[j * 6 + 2],
                            (float)slots[j * 6 + 3], (float)slots[j * 6 + 4], (float)slots[j * 6 + 5], body, e2);
                    }
                }));
            }
        // The state block as SubmitState builds it: the six slots as floats,
        // then the state program the profile's fields lower into, bound as a
        // controller binds it, generated and interpreted.
        foreach (bool interpret in new[] { false, true })
        {
            var runner = ProgramBinder.BindState(model.StateProgram!, interpret);
            var pinned = GC.AllocateArray<byte>(24, pinned: true);
            variants.Add(("s2-body" + (interpret ? "-interpreted" : ""), n =>
            {
                float* sl = stackalloc float[6];
                fixed (byte* d = pinned)
                    for (int i = 0; i < n; i++)
                    {
                        int j = i & 63;
                        sl[0] = (float)slots[j * 6]; sl[1] = (float)slots[j * 6 + 1]; sl[2] = (float)slots[j * 6 + 2];
                        sl[3] = (float)slots[j * 6 + 3]; sl[4] = (float)slots[j * 6 + 4]; sl[5] = (float)slots[j * 6 + 5];
                        runner.Run(in states[j], sl, d);
                    }
            }));
        }
        // The codec over the same fields, which the state program replaces.
        variants.Add(("s2-body-codec", n =>
        {
            for (int i = 0; i < n; i++)
            {
                int j = i & 63;
                VendorBlobCodec.EncodeInput(spec, in states[j], (float)slots[j * 6], (float)slots[j * 6 + 1], (float)slots[j * 6 + 2],
                    (float)slots[j * 6 + 3], (float)slots[j * 6 + 4], (float)slots[j * 6 + 5], body, enc);
            }
        }));
#endif
        // Each variant past the tiered JIT's call-count threshold (30) and its
        // background recompile, so every run reads Tier1 code. Ten calls left
        // a variant's loop in on-stack-replacement code, whose speed varied
        // from launch to launch: the generated decode ran at 6.7 ns in some
        // launches and 15 to 20 ns in others, and at 6.7 ns in all of them
        // with the tiered JIT off or with the warmup below.
        foreach (var (_, batch) in variants) batch(Math.Min(calls, 20_000));
        for (int w = 0; w < 60; w++) foreach (var (_, batch) in variants) batch(100);
        Thread.Sleep(500);
        foreach (var (_, batch) in variants) batch(Math.Min(calls, 20_000));
        var times = variants.Select(_ => new List<double>()).ToList();
        var bytes = new long[variants.Count];
        for (int r = 0; r < runs; r++)
            for (int k = 0; k < variants.Count; k++)
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                variants[k].Batch(calls);
                long t1 = Stopwatch.GetTimestamp();
                bytes[k] = Math.Max(bytes[k], GC.GetAllocatedBytesForCurrentThread() - b0);
                times[k].Add((t1 - t0) * 1e9 / Stopwatch.Frequency / calls);
            }
        for (int k = 0; k < variants.Count; k++)
        {
            var t = times[k];
            t.Sort();
            Console.WriteLine(FormattableString.Invariant($"program,{variants[k].Name},{t[t.Count / 2]:F3},{bytes[k] / Math.Max(1, calls)}"));
        }
    }

#if !DD4_BASE
    /// <summary>The device clock's call, BuildNext over the set's own type,
    /// in a loop compiled for that type, with the body replaced each call as
    /// SubmitState replaces it.</summary>
    sealed class BuildLoop : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public byte[][] Bodies = null!;
        public byte[] Report = null!;
        public int Calls;
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet
        {
            int n = 0;
            for (int i = 0; i < Calls; i++)
            {
                Engine.SetStateBlock(Bodies[i & 63]);
                n += Engine.BuildNext(ref set, Report);
            }
            return n;
        }
    }
#endif

    public static void Idle(double periodMs, double seconds)
    {
        var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "dualsense-bt");
        // A period of 0 is no repeat: the process's own cost at rest.
#if DD4_BASE
        p.ExtendedReport!.IdleFrameIntervalMs = (int)periodMs;
#else
        if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
        p.Stream = periodMs > 0 ? new StreamSpec { PeriodMs = periodMs, Timestamps = "advance" } : null;
#endif
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
        int index = 7700;
        var c = Offline(p, index, view);
        c.SubmitState(new HMGamepadState { Buttons = HMButton.A });
        // Past the tiered JIT's recompiles of the repeat path, so the window
        // reads the steady state.
        Thread.Sleep(3000);
        IntPtr self = GetCurrentProcess();
        uint seq0 = (uint)Marshal.ReadInt32(view);
        QueryProcessCycleTime(self, out ulong cycles0);
        long t0 = Stopwatch.GetTimestamp();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        QueryProcessCycleTime(self, out ulong cycles1);
        long t1 = Stopwatch.GetTimestamp();
        uint seq1 = (uint)Marshal.ReadInt32(view);
        double wall = (t1 - t0) / (double)Stopwatch.Frequency;
        // The sequence number goes up by two a frame: odd while the frame is
        // written, even once it is whole (SharedMemoryIO.WriteInputFrame).
        double frames = (seq1 - seq0) / 2.0 / wall;
        double cyclesPerSecond = (cycles1 - cycles0) / wall;
        Console.WriteLine(FormattableString.Invariant(
            $"idle,{periodMs},{cyclesPerSecond / 1e6:F3},{frames:F1},{(frames > 0 ? cyclesPerSecond / frames : 0):F0}"));
    }

    static HMController Offline(ControllerProfile p, int index, IntPtr view)
    {
        Cache("s_inputViews")[index] = view;
        Cache("s_outputViews")[index] = IntPtr.Zero;
        var ctor = typeof(HMController).GetConstructor(Instance, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
        }) ?? throw new MissingMethodException("HMController's private constructor changed.");
        var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        return (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;
}
