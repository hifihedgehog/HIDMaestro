// encode [runs] [calls]: the codec's encode of the budget table's
// codec-encoded input reports, offline: dualsense-bt's 0x31, steam-controller-2's
// 0x42, dualshock-3-full's native report and one gamecube-adapter port block,
// each through VendorBlobCodec.EncodeInput on 64 states drawn from a fixed
// seed. One launch prints each report's median ns per call. It builds against
// 9bd6279, b22324e and this tree alike, and program_equivalence_check's
// state-cost mode times the state programs these reports lower into on the
// same states (EncodeStates, linked into both).
// The codec vocabulary this measures (ports, PortLayout) is not in
// b4d29d9, so the HmBase build leaves the file out.
#if !HM_BASE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class EncodeCost
{
    public static readonly string[] Ids = { "dualsense-bt", "steam-controller-2", "dualshock-3-full", "gamecube-adapter" };

    public static void Run(int runs, int calls)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var db = ProfileDatabase.LoadEmbedded();
        var (states, slots) = EncodeStates.Make();
        var variants = new List<(string Name, Action<int> Batch)>();
        foreach (var id in Ids)
        {
            var p = db.All.First(x => x.Id == id);
            var spec = id == "gamecube-adapter" ? PortLayout.Of(p.ExtendedReport, id, output: false)!.FieldSpec : p.ExtendedReport!;
            var buffer = new byte[spec.Size];
            var enc = new VendorBlobCodec.EncoderState();
            variants.Add((id, n =>
            {
                for (int i = 0; i < n; i++)
                {
                    int j = i & 63;
                    VendorBlobCodec.EncodeInput(spec, in states[j], slots[j * 6], slots[j * 6 + 1], slots[j * 6 + 2],
                        slots[j * 6 + 3], slots[j * 6 + 4], slots[j * 6 + 5], buffer, enc);
                }
            }));
        }
        // Past the tiered JIT's call-count threshold and its background
        // recompile, as ProgramCost warms its variants.
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
            Console.WriteLine(FormattableString.Invariant($"encode,{variants[k].Name},codec,{t[t.Count / 2]:F3}"));
        }
    }
}

/// <summary>The 64 states and slot sets both timings run on: every field a
/// codec report reads, drawn from one seed, and a stamp that advances.</summary>
internal static class EncodeStates
{
    public static (HMGamepadState[] States, float[] Slots) Make()
    {
        var r = new Random(20261017);
        var states = new HMGamepadState[64];
        var slots = new float[64 * 6];
        uint stamp = 1000;
        for (int i = 0; i < states.Length; i++)
        {
            stamp += (uint)r.Next(1, 40000);
            short S() => (short)r.Next(short.MinValue, short.MaxValue + 1);
            byte P() => (byte)(r.Next(3) == 0 ? r.Next(256) : 0);
            states[i] = new HMGamepadState
            {
                Buttons = (HMButton)(uint)r.NextInt64(0, 1L << 32),
                Hat = (HMHat)r.Next(9),
                SensorTimestamp = stamp,
                GyroPitch = S(), GyroYaw = S(), GyroRoll = S(), AccelX = S(), AccelY = S(), AccelZ = S(),
                AccelGX = (float)(r.NextDouble() * 4 - 2), AccelGY = (float)(r.NextDouble() * 4 - 2), AccelGZ = (float)(r.NextDouble() * 4 - 2),
                GyroDpsX = (float)(r.NextDouble() * 400 - 200), GyroDpsY = (float)(r.NextDouble() * 400 - 200), GyroDpsZ = (float)(r.NextDouble() * 400 - 200),
                TouchpadFinger0Active = r.Next(2) == 0, TouchpadFinger1Active = r.Next(2) == 0,
                TouchpadFinger0X = (ushort)r.Next(1920), TouchpadFinger0Y = (ushort)r.Next(1080),
                TouchpadFinger1X = (ushort)r.Next(1920), TouchpadFinger1Y = (ushort)r.Next(1080),
                TouchpadFinger0Id = (byte)r.Next(128), TouchpadFinger1Id = (byte)r.Next(128),
                BatteryLevel = (byte)r.Next(11), BatteryCharging = r.Next(2) == 0, BatteryFull = r.Next(4) == 0,
                PressureA = P(), PressureB = P(), PressureX = P(), PressureY = P(), PressureLeftBumper = P(), PressureRightBumper = P(),
                PressureDpadUp = P(), PressureDpadRight = P(), PressureDpadDown = P(), PressureDpadLeft = P(),
            };
            for (int k = 0; k < 6; k++) slots[i * 6 + k] = (float)r.NextDouble();
        }
        return (states, slots);
    }
}
#endif
