using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

/// <summary>state-cost [runs] [calls]: the budget table's codec-encoded input
/// reports (perf_budget_check's encode mode, EncodeCost.Ids) as the state
/// programs they lower into, the kernels this probe's build generated, on
/// the states that mode encodes (EncodeStates). Each call fills the counter
/// slots as the code that runs a lowered report would (StateChecks.Feeds),
/// copies the six slots and runs the kernel. One launch prints each report's
/// median ns per call, the program's and, in this process, the codec's. Not
/// applied: the SDK runs only state blocks as state programs.</summary>
internal static unsafe class StateCost
{
    public static int Run(int runs, int calls)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var db = ProfileDatabase.LoadEmbedded();
        var asm = typeof(HMContext).Assembly;
        var roots = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(res)!;
            var root = JsonDocument.Parse(stream, ProfilePrograms.DocumentOptions).RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && id.GetString() is string s) roots[s] = root;
        }
        var (states, slotValues) = EncodeStates.Make();
        var variants = new List<(string Name, Action<int> Batch)>();
        foreach (var id in EncodeCost.Ids)
        {
            var p = db.All.First(x => x.Id == id);
            var (_, fields, size, rid) = StateLowering.CodecReports(roots[id]).First();
            var spec = StateLowering.Lower(fields, size, rid, allowFeeds: true, out var feedSpec, out string reason)
                ?? throw new InvalidOperationException($"{id}: not lowered: {reason}");
            var v = ProgramForm.Validate(spec);
            if (!ProbeKernels.StateByHash.TryGetValue(v.Hash, out nint fn)) throw new InvalidOperationException($"{id}: no generated state kernel");
            var kernel = (delegate*<in HMGamepadState, float*, byte*, uint*, void>)fn;
            uint nominal = (uint)Math.Clamp((long)p.ExtendedReport!.IdleFrameIntervalMs * 3000, 0, uint.MaxValue);
            var feeds = new StateChecks.Feeds(feedSpec, nominal);
            var block = GC.AllocateArray<byte>(size, pinned: true);
            var counters = GC.AllocateArray<uint>(ProgramForm.MaxCounters, pinned: true);
            var sl = GC.AllocateArray<float>(6, pinned: true);
            bool anyFeeds = feedSpec.Length > 0;
            variants.Add((id + ",program", n =>
            {
                fixed (byte* d = block)
                fixed (uint* c = counters)
                fixed (float* f = sl)
                    for (int i = 0; i < n; i++)
                    {
                        int j = i & 63;
                        if (anyFeeds)
                        {
                            feeds.Fill(in states[j]);
                            for (int k = 0; k < feedSpec.Length; k++) c[k] = feeds.Slots[k];
                        }
                        f[0] = slotValues[j * 6]; f[1] = slotValues[j * 6 + 1]; f[2] = slotValues[j * 6 + 2];
                        f[3] = slotValues[j * 6 + 3]; f[4] = slotValues[j * 6 + 4]; f[5] = slotValues[j * 6 + 5];
                        kernel(in states[j], f, d, c);
                        if (anyFeeds) feeds.Advance();
                    }
            }));
            var codec = id == "gamecube-adapter" ? PortLayout.Of(p.ExtendedReport, id, output: false)!.FieldSpec : p.ExtendedReport;
            var buffer = new byte[codec.Size];
            var enc = new VendorBlobCodec.EncoderState();
            variants.Add((id + ",codec-here", n =>
            {
                for (int i = 0; i < n; i++)
                {
                    int j = i & 63;
                    VendorBlobCodec.EncodeInput(codec, in states[j], slotValues[j * 6], slotValues[j * 6 + 1], slotValues[j * 6 + 2],
                        slotValues[j * 6 + 3], slotValues[j * 6 + 4], slotValues[j * 6 + 5], buffer, enc);
                }
            }));
        }
        foreach (var (_, batch) in variants) batch(Math.Min(calls, 20_000));
        for (int w = 0; w < 60; w++) foreach (var (_, batch) in variants) batch(100);
        System.Threading.Thread.Sleep(500);
        foreach (var (_, batch) in variants) batch(Math.Min(calls, 20_000));
        var times = variants.Select(_ => new List<double>()).ToList();
        for (int r = 0; r < runs; r++)
            for (int k = 0; k < variants.Count; k++)
            {
                long t0 = Stopwatch.GetTimestamp();
                variants[k].Batch(calls);
                times[k].Add((Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / calls);
            }
        for (int k = 0; k < variants.Count; k++)
        {
            var t = times[k];
            t.Sort();
            Console.WriteLine(FormattableString.Invariant($"encode,{variants[k].Name},{t[t.Count / 2]:F3}"));
        }
        return 0;
    }
}
