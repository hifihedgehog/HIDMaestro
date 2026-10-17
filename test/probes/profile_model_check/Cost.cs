// cost: one launch's measure of what the answer tables cost, offline. A
// script takes p50 and p95 over 30 launches or more.
//
// Each line is "cost,<what>,<subject>,<median ns or bytes>": the median of
// 15 batches.
//   lookup     one GET_REPORT(Feature) through FeatureStubTable.Lookup and
//              Answer, the USB/IP device's path: report 0x05 of
//              dualsense-bt at 64 bytes, and Triton's command channel, a SET
//              of 0x83 on report 1 then its GET. Triton's GET 0x83 is
//              measured twice: reading Steam's config through its value
//              source, the path a device on a machine with Steam takes and
//              the one the base had, and with a reader that finds no file,
//              which serves the captured stamp
//   alloc      bytes the thread allocated per such GET: 0 for an answer
//              that is a slice of the table's buffer, and the registry and
//              file reads' own for Triton's GET through Steam's config
//   compile    FeatureStubTable.From for a controller at create, and the
//              FeatureAnswers blob the create writes for UMDF2
// Against the base SDK it measures what the base has: Triton's answer to
// 0x83 through the table-level lastMessage Lookup, which returns a new
// array per GET. The base's Sony answers are UsbipEmulatedDevice code a
// device instance runs, which this offline mode does not build.
// Not part of check: a measurement, with no verdict of its own.
using System;
using System.Diagnostics;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Cost
{
    const int Batches = 15;

    static double Median(Func<double> batch)
    {
        var v = Enumerable.Range(0, Batches).Select(_ => batch()).OrderBy(x => x).ToArray();
        return v[v.Length / 2];
    }

    /// <summary>Median ns per call over batches of <paramref name="calls"/>.</summary>
    static double Ns(int calls, Action call)
    {
        for (int i = 0; i < calls; i++) call();
        return Median(() =>
        {
            long t = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++) call();
            return (Stopwatch.GetTimestamp() - t) * 1e9 / Stopwatch.Frequency / calls;
        });
    }

    static double Bytes(int calls, Action call)
    {
        call();
        long a = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < calls; i++) call();
        return (GC.GetAllocatedBytesForCurrentThread() - a) / (double)calls;
    }

    public static int Run()
    {
        bool tree = typeof(FeatureStubTable).GetMethod("OnSetFeature") != null;
        return tree ? Tree() : Base();
    }

    static int Base()
    {
        var all = ProfileDatabase.LoadEmbedded().All;
        var triton = all.First(p => p.Id == "steam-controller-2");
        var from = typeof(FeatureStubTable).GetMethod("From", new[] { typeof(ControllerProfile) })!;
        object table = from.Invoke(null, new object[] { triton })!;
        var lookup = typeof(FeatureStubTable).GetMethod("Lookup", new[] { typeof(byte), typeof(ushort) })!;
        var get = (Func<byte, ushort, byte[]?>)Delegate.CreateDelegate(typeof(Func<byte, ushort, byte[]?>), table, lookup);
        int sink = 0;
        void Tri() => sink += get(0x83, 64)![0];
        Console.WriteLine($"cost,lookup,steam-controller-2 GET 0x83 reading Steam's config,{Ns(2_000, Tri):F1}");
        Console.WriteLine($"cost,alloc,steam-controller-2 GET 0x83 reading Steam's config,{Bytes(1_000, Tri):F1}");
        GC.KeepAlive(sink);
        return 0;
    }

    static int Tree()
    {
        var all = ProfileDatabase.LoadEmbedded().All;
        var ds = all.First(p => p.Id == "dualsense-bt");
        var triton = all.First(p => p.Id == "steam-controller-2");
        var dsTable = FeatureStubTable.From(ds, 0, "HM-CTL-0000")!;
        // The captured stamp: a reader that finds no file, as on a machine
        // without Steam. The base has no such path: it read Steam's config.
        var tritonTable = FeatureStubTable.From(triton, 0, "FXA9960200001", _ => null)!;
        // Steam's config, read through the value source as a device reads it.
        var tritonSteam = FeatureStubTable.From(triton, 0, "FXA9960200001")!;
        tritonSteam.OnSetFeature(1, new byte[] { 0x01, 0x83, 0x00 });
        byte[] set = { 0x01, 0x83, 0x00 };
        int sink = 0;
        void Ds() { int n = dsTable.Lookup(0x05, 64, out _); sink += dsTable.Answer(n)[1]; }
        void Tri() { tritonTable.OnSetFeature(1, set); int n = tritonTable.Lookup(1, 64, out _); sink += tritonTable.Answer(n)[0]; }
        tritonTable.OnSetFeature(1, set);
        void TriGet() { int n = tritonTable.Lookup(1, 64, out _); sink += tritonTable.Answer(n)[0]; }
        void TriSteam() { int n = tritonSteam.Lookup(1, 64, out _); sink += tritonSteam.Answer(n)[0]; }
        Console.WriteLine($"cost,lookup,steam-controller-2 GET 0x83 without Steam's config,{Ns(200_000, TriGet):F1}");
        Console.WriteLine($"cost,alloc,steam-controller-2 GET 0x83 without Steam's config,{Bytes(10_000, TriGet):F1}");
        Console.WriteLine($"cost,lookup,steam-controller-2 GET 0x83 reading Steam's config,{Ns(2_000, TriSteam):F1}");
        Console.WriteLine($"cost,alloc,steam-controller-2 GET 0x83 reading Steam's config,{Bytes(1_000, TriSteam):F1}");
        Console.WriteLine($"cost,lookup,dualsense-bt 0x05,{Ns(200_000, Ds):F1}");
        Console.WriteLine($"cost,lookup,steam-controller-2 SET 0x83 then GET,{Ns(200_000, Tri):F1}");
        Console.WriteLine($"cost,alloc,dualsense-bt 0x05,{Bytes(10_000, Ds):F1}");
        Console.WriteLine($"cost,alloc,steam-controller-2 SET 0x83 then GET,{Bytes(10_000, Tri):F1}");
        Console.WriteLine($"cost,compile,FeatureStubTable.From dualsense-bt,{Ns(2_000, () => sink += FeatureStubTable.From(ds, 1, "HM-CTL-0001")!.Entries.Count):F1}");
        Console.WriteLine($"cost,compile,FeatureAnswers blob dualsense-bt,{Ns(2_000, () => sink += DeviceOrchestrator.FeatureAnswersBlob(ds, 1).Length):F1}");
        Console.WriteLine($"cost,compile,FeatureStubTable.From steam-controller-2,{Ns(2_000, () => sink += FeatureStubTable.From(triton, 1, "FXA9960200001", _ => null)!.Entries.Count):F1}");
        GC.KeepAlive(sink);
        return 0;
    }
}
