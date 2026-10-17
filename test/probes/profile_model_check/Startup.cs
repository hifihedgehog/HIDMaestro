// startup: one launch's cold cost of what a program meets first, offline. A
// script takes p50 and p95 over 30 launches or more, interleaved with a
// base build's.
//
// Prints "startup,<what>,<ms>":
//   build    the first HMProfileBuilder.Build in a process, of a generic
//            pad (VID 1234, a plain descriptor), before anything loads the
//            catalog
//   build2   a later Build of the same pad, the mean of 100
//   load     with the argument "load", alone: the first
//            ProfileDatabase.LoadEmbedded in a process, the catalog's parse,
//            checks and resolution
// Without the argument the order is the one a program that builds its own
// profile meets, and the catalog it may never need is not loaded. Public
// members only, so the same probe runs against the base SDK. Not part of
// check.
using System;
using System.Diagnostics;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Startup
{
    public static int Run(string? what)
    {
        const string descriptor = "05010905A101850115002501750195100509190129108102C0";
        static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

        if (what == "load")
        {
            long l = Stopwatch.GetTimestamp();
            var all = ProfileDatabase.LoadEmbedded();
            Console.WriteLine($"startup,load,{Ms(l):F3}");
            GC.KeepAlive(all);
            return 0;
        }
        long t = Stopwatch.GetTimestamp();
        var p = new HMProfileBuilder().Id("user-pad").Vid(0x1234).Pid(0x5678).DescriptorHex(descriptor).Build();
        Console.WriteLine($"startup,build,{Ms(t):F3}");

        t = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100; i++)
            p = new HMProfileBuilder().Id("user-pad-" + i).Vid(0x1234).Pid(0x5678).DescriptorHex(descriptor).Build();
        Console.WriteLine($"startup,build2,{Ms(t) / 100:F4}");
        GC.KeepAlive(p);
        return 0;
    }
}
