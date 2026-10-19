// report [runs] [calls]: the hot-path cost of the one arithmetic of the
// data-driven design (decision (a)), offline. Creates no device, opens no
// section and needs no elevation.
//
// One launch measures, interleaved run by run so load elsewhere on the
// machine lands on every variant alike, at high process priority:
//   - BuildReportInto, the descriptor path, on four profiles
//   - EncodeInput, the codec, on four profiles, and on ps-move where it
//     carries a codec report
//   - SubmitState on the budget table's profiles, through the SDK's own
//     controller built offline on a buffer of this probe's, as
//     device_oracle_check's case 1 builds it: the descriptor path, the XUSB
//     slice, the armed codec, the USB/IP codec and packer, a port encoder
// and prints each variant's median ns per call and the bytes the thread
// allocated per call. A script launches it 30 times or more against this
// tree and against the tree before the change, and takes p50 and p95 over
// the launches (scripts in the lane's report).
// The idle repeat and arithmetic this measures (ControllerProfile.Stream)
// are not in b4d29d9, so the HmBase build leaves the file out.
#if !HM_BASE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class ReportCost
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run(int runs, int calls)
    {
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var db = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var rng = new Random(20261016);
        var variants = new List<(string Name, Action<int> Call)>();
        var cleanups = new List<Action>();

        // The descriptor path: whole reports from 1,024 random axis sets.
        foreach (var id in new[] { "amazon-luna-usb", "amazon-luna-ble", "xbox-one-s", "dualshock-4-v2", "xbox-360-wired" })
        {
            var b = db[id].GetOrBuildReportBuilder();
            var axes = new Dictionary<HMAxis, float>[1024];
            for (int i = 0; i < axes.Length; i++)
            {
                axes[i] = new Dictionary<HMAxis, float>();
                foreach (var k in b.AxisFields.Keys) axes[i][k] = (float)rng.NextDouble();
            }
            var buf = new byte[b.InputReportByteSize];
            variants.Add(($"BuildReportInto,{id}", i => b.BuildReportInto(buf, axes[i & 1023], i % 9, (uint)i)));
        }

        // The codec: whole reports from 1,024 random states.
        foreach (var id in new[] { "dualsense-bt", "dualshock-3-full", "steam-controller-2", "switch2-pro-controller" })
        {
            var spec = db[id].ExtendedReport!;
            var states = new HMGamepadState[1024];
            var slots = new float[1024 * 6];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = RandomState(rng);
                for (int k = 0; k < 6; k++) slots[i * 6 + k] = (float)rng.NextDouble();
            }
            var buf = new byte[spec.Size];
            var enc = new VendorBlobCodec.EncoderState();
            variants.Add(($"EncodeInput,{id}", i =>
            {
                int j = i & 1023;
                VendorBlobCodec.EncodeInput(spec, in states[j], slots[j * 6], slots[j * 6 + 1], slots[j * 6 + 2],
                    slots[j * 6 + 3], slots[j * 6 + 4], slots[j * 6 + 5], buf, enc);
            }));
        }

        // SubmitState on the budget table's profiles, offline. The idle
        // pump is off, as case 1 turns it off, so only these calls write.
        int index = 7600;
        foreach (var (id, armed) in new[]
        {
            ("dualshock-4-v2", false), ("xbox-360-wired", false), ("dualsense-bt", true), ("steam-controller-2", false),
            ("switch2-pro-controller-composite", false), ("gamecube-adapter", false), ("amazon-luna-usb", false), ("amazon-luna-ble", false),
            // The two profiles that carry a captured descriptor (lane DD-6).
            ("ps-move", false), ("xbox-360-flight-stick", false),
        })
        {
            var p = db[id];
            if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
#if !DD4_BASE
            p.Stream = null;
#endif
            IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
            int at = index++;
            var c = Offline(p, at, view);
            if (armed) typeof(HMController).GetField("_extendedModeArmed", Instance)!.SetValue(c, true);
            var hp = new HMProfile(p);
            var keys = hp.Sticks.SelectMany(s => new[] { s.XAxis, s.YAxis }).Concat(hp.Triggers.Select(t => t.Axis))
                .Concat(new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz }).Where(a => a != HMAxis.None).Distinct().ToArray();
            var states = new HMGamepadState[1024];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = RandomState(rng);
                var axes = new Dictionary<HMAxis, float>();
                foreach (var k in keys) axes[k] = (float)rng.NextDouble();
                states[i].Axes = axes;
                states[i].SensorTimestamp = (uint)(i + 1) * 12000;
            }
            variants.Add(($"SubmitState,{id}{(armed ? " armed" : "")}", i => c.SubmitState(in states[i & 1023])));
            cleanups.Add(() =>
            {
                Cache("s_inputViews").Remove(at);
                Cache("s_outputViews").Remove(at);
                Marshal.FreeHGlobal(view);
            });
        }

        // ps-move's codec report, from lane DD-6 on, with random motion in
        // every calibrated field it writes. Its own generator, so the other
        // variants' inputs are those of a tree without it.
        if (db["ps-move"].ExtendedReport is { } moveSpec)
        {
            var moveRng = new Random(20261017);
            var states = new HMGamepadState[1024];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = RandomState(moveRng);
                states[i].AccelGX = (float)(moveRng.NextDouble() * 4 - 2);
                states[i].AccelGZ = (float)(moveRng.NextDouble() * 4 - 2);
                states[i].GyroDpsY = (float)(moveRng.NextDouble() * 400 - 200);
                states[i].GyroDpsZ = (float)(moveRng.NextDouble() * 400 - 200);
            }
            var buf = new byte[moveSpec.Size];
            var enc = new VendorBlobCodec.EncoderState();
            variants.Add(("EncodeInput,ps-move", i => VendorBlobCodec.EncodeInput(moveSpec, in states[i & 1023],
                0.5f, 0.5f, 0.5f, 0.5f, (float)(i & 255) / 255f, 0f, buf, enc)));
        }

        var times = variants.Select(_ => new List<double>()).ToList();
        var bytes = new long[variants.Count];
        foreach (var (_, call) in variants)
            for (int i = 0; i < Math.Min(calls, 20_000); i++) call(i);
        for (int r = 0; r < runs; r++)
            for (int k = 0; k < variants.Count; k++)
            {
                var call = variants[k].Call;
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < calls; i++) call(i);
                long t1 = Stopwatch.GetTimestamp();
                bytes[k] = Math.Max(bytes[k], GC.GetAllocatedBytesForCurrentThread() - b0);
                times[k].Add((t1 - t0) * 1e9 / Stopwatch.Frequency / calls);
            }
        for (int k = 0; k < variants.Count; k++)
        {
            var t = times[k];
            t.Sort();
            Console.WriteLine(FormattableString.Invariant($"report,{variants[k].Name},{t[t.Count / 2]:F3},{bytes[k]}"));
        }
        foreach (var c in cleanups) c();
    }

    static HMGamepadState RandomState(Random rng) => new()
    {
        Buttons = (HMButton)(uint)rng.Next(),
        Hat = (HMHat)rng.Next(0, 9),
        PressureA = (byte)rng.Next(256),
        AccelX = (short)rng.Next(-20000, 20000),
        AccelZ = (short)rng.Next(-20000, 20000),
        AccelGY = (float)(rng.NextDouble() * 4 - 2),
        GyroDpsX = (float)(rng.NextDouble() * 400 - 200),
        BatteryLevel = (byte)rng.Next(11),
        TouchpadFinger0Active = rng.Next(2) == 0,
        TouchpadFinger0X = (ushort)rng.Next(1920),
        TouchpadFinger0Y = (ushort)rng.Next(1080),
    };

    /// <summary>The SDK's own controller, built offline on a buffer of this
    /// probe's, through the private constructor every create path ends in:
    /// no section, event or driver is created.</summary>
    static HMController Offline(ControllerProfile p, int index, IntPtr view)
    {
        Cache("s_inputViews")[index] = view;
        Cache("s_outputViews")[index] = IntPtr.Zero;
        var ctor = typeof(HMController).GetConstructor(Instance, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
        }) ?? throw new MissingMethodException("HMController's private constructor changed.");
        var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        var c = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
        // Keyboard and mouse pacing waits a millisecond between frames.
        typeof(HMController).GetField("_deliverEvery", Instance)?.SetValue(c, false);
        return c;
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;
}
#endif
