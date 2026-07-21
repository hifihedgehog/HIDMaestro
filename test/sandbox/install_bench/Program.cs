// Times each phase of a cold SDK launch: HMContext ctor, InstallDriver,
// first CreateController. Run elevated. Used by the 2026-07-21 perf audit
// to break down the launch wall-clock; not part of the battery.
using System;
using System.Diagnostics;
using HIDMaestro;

var total = Stopwatch.StartNew();
var sw = Stopwatch.StartNew();
using var ctx = new HMContext();
Console.WriteLine($"ctor:          {sw.ElapsedMilliseconds} ms");

sw.Restart();
ctx.InstallDriver();
Console.WriteLine($"InstallDriver: {sw.ElapsedMilliseconds} ms");

sw.Restart();
ctx.LoadDefaultProfiles();
Console.WriteLine($"LoadProfiles:  {sw.ElapsedMilliseconds} ms");

sw.Restart();
var pad = ctx.CreateController(ctx.GetProfile("dualsense")!);
Console.WriteLine($"CreateCtrl:    {sw.ElapsedMilliseconds} ms");

sw.Restart();
pad.Dispose();
Console.WriteLine($"Dispose:       {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"total:         {total.ElapsedMilliseconds} ms");
