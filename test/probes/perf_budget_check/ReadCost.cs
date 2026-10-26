// profile-reads [runs] [calls]: one read of each HMProfile member that hands
// a program a profile's own data, offline: ButtonMap, AxisMap,
// ExtendedReport, ExtendedOutputReport and Layout on dualsense-bt, and the
// specs and layout of the catalog's largest (steam-deck-composite's
// extendedReport, dualshock-4-v2's extendedOutputReport beside dualsense-bt's,
// switch2-pro-controller's layout). A tree whose members hand out copies
// pays for the copy on each read. The variants run interleaved run by run at
// high process priority, as SubmitCost's do, and each prints its median ns
// per read and the bytes the thread allocated per read in its heaviest run.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class ReadCost
{
    // Each read lands here, so none is optimized away.
    static object? s_sink;

    public static void Run(int runs, int calls)
    {
        using (var me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.High;
        var db = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        HMProfile P(string id) => new HMProfile(db[id]);
        var ds = P("dualsense-bt");
        var deck = P("steam-deck-composite");
        var ds4 = P("dualshock-4-v2");
        var sw2 = P("switch2-pro-controller");
        var variants = new List<(string Name, Action Read)>
        {
            ("ButtonMap,dualsense-bt", () => s_sink = ds.ButtonMap),
            ("AxisMap,dualsense-bt", () => s_sink = ds.AxisMap),
            ("ExtendedReport,dualsense-bt", () => s_sink = ds.ExtendedReport),
            ("ExtendedReport,steam-deck-composite", () => s_sink = deck.ExtendedReport),
            ("ExtendedOutputReport,dualsense-bt", () => s_sink = ds.ExtendedOutputReport),
            ("ExtendedOutputReport,dualshock-4-v2", () => s_sink = ds4.ExtendedOutputReport),
            ("Layout,dualsense-bt", () => s_sink = ds.Layout),
            ("Layout,switch2-pro-controller", () => s_sink = sw2.Layout),
        };
        foreach (var (name, read) in variants)
        {
            read();
            if (s_sink == null) throw new InvalidOperationException($"{name.Replace(',', ' ')}: the profile has none.");
        }

        var times = variants.Select(_ => new List<double>()).ToList();
        var bytes = new long[variants.Count];
        foreach (var (_, read) in variants)
            for (int i = 0; i < Math.Min(calls, 2_000); i++) read();
        Thread.Sleep(500);
        foreach (var (_, read) in variants)
            for (int i = 0; i < Math.Min(calls, 2_000); i++) read();
        for (int r = 0; r < runs; r++)
            for (int k = 0; k < variants.Count; k++)
            {
                var read = variants[k].Read;
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < calls; i++) read();
                long t1 = Stopwatch.GetTimestamp();
                bytes[k] = Math.Max(bytes[k], GC.GetAllocatedBytesForCurrentThread() - b0);
                times[k].Add((t1 - t0) * 1e9 / Stopwatch.Frequency / calls);
            }
        for (int k = 0; k < variants.Count; k++)
        {
            var t = times[k];
            t.Sort();
            Console.WriteLine(FormattableString.Invariant($"report,read {variants[k].Name},{t[t.Count / 2]:F3},{bytes[k] / Math.Max(calls, 1)}"));
        }
    }
}
