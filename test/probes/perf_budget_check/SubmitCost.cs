// submit [runs] [calls], submit-repeat [runs] [calls]: the SDK's own cost
// per call on the submit paths
// the frame mode times live, offline. Creates no device, opens no section
// and needs no elevation, so the driver's turn and the wake of its worker,
// which the frame mode's times carry, are left out, and what remains is the
// part of a call this tree's code decides.
//
// Each controller is the SDK's own, built offline on a buffer of this
// probe's through the constructor every create path ends in, as ReportCost
// builds one. It builds against b4d29d9 too (-p:HmBase=true), so the two
// trees run the same calls on the same states:
//   - SubmitState with the frame mode's two states in turn on its profiles
//     and switch-pro
//   - SubmitRawReport on dualshock-4-v2 and switch-pro
//   - here, switch-pro's protocol host: a tick of its clock in the full
//     mode (0x30, sent on every tick) and in the simple mode (0x3F, sent
//     on change), each after a SubmitState that changes the state, as the
//     clock finds it between a consumer's frames
//   - one tick of steam-deck-composite's idle repeat, RepeatLastFrame
//     called directly: the clock's work from its wake to the section write
//   - submit-repeat: SubmitState on dualshock-4-v2, the armed dualsense-bt
//     and steam-deck-composite with their idle repeat running, as on a live
//     controller, where a consumer's frame also keeps what the repeat
//     sends. b4d29d9 has no repeat for the first two. A launch of its own, since
//     the repeat's clock wakes every period and would land on the other
//     variants' times
// Variants run interleaved run by run, at high process priority, and each
// prints its median ns per call and the most bytes the thread allocated in
// one run. A script launches it 30 times or more against each tree and takes
// p50 and p95 over the launches.
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

internal static class SubmitCost
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run(int runs, int calls, bool repeat)
    {
        using (var me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.High;
        var db = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var a = new HMGamepadState { Buttons = HMButton.A, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.25f, [HMAxis.Y] = 0.75f } };
        var b = new HMGamepadState { Buttons = HMButton.B, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.75f, [HMAxis.Y] = 0.25f } };
        var variants = new List<(string Name, Action<int> Call)>();
        var cleanups = new List<Action>();
        var made = new List<HMController>();
        int index = 7700;

        // The profiles as the catalog gives them, for the controllers that
        // keep their idle repeat, and copies without it.
        var repeating = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        db = ((ProfileDatabase)typeof(ProfileDatabase).GetMethod("LoadEmbeddedFresh", Static)!.Invoke(null, null)!)
            .All.ToDictionary(p => p.Id, StringComparer.Ordinal);

        HMController Make(string id, bool armed, bool repeat = false)
        {
            var p = repeat ? repeating[id] : db[id];
            // The idle repeat stays off unless asked for, as ReportCost turns
            // it off, so only these calls write. With it, the repeat's clock
            // runs as on a live controller and a consumer's frame does the
            // repeat's bookkeeping.
            if (!repeat)
            {
                if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
#if !HM_BASE
                p.Stream = null;
#endif
            }
            IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
            int at = index++;
            Cache("s_inputViews")[at] = view;
            Cache("s_outputViews")[at] = IntPtr.Zero;
            var ctor = typeof(HMController).GetConstructor(Instance, new[]
            {
                typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
            }) ?? throw new MissingMethodException("HMController's constructor changed.");
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            var c = (HMController)ctor.Invoke(new object?[] { context, at, new HMProfile(p), null, null, null });
            typeof(HMController).GetField("_deliverEvery", Instance)?.SetValue(c, false);
            if (armed) typeof(HMController).GetField("_extendedModeArmed", Instance)!.SetValue(c, true);
            made.Add(c);
            cleanups.Add(() =>
            {
                Cache("s_inputViews").Remove(at);
                Cache("s_outputViews").Remove(at);
                Marshal.FreeHGlobal(view);
            });
            return c;
        }

        if (repeat)
            foreach (var (id, armed) in new[] { ("dualshock-4-v2", false), ("dualsense-bt", true), ("steam-deck-composite", false) })
            {
                var c = Make(id, armed, repeat: true);
                variants.Add(($"SubmitState,{id}{(armed ? " armed" : "")} with its idle repeat", i => c.SubmitState((i & 1) == 0 ? a : b)));
            }
        else
            foreach (var (id, armed) in new[]
            {
                ("dualshock-4-v2", false), ("xbox-360-wired", false), ("dualsense-bt", true), ("dualsense-bt", false),
                ("steam-controller-2", false), ("dualshock-3-full", false), ("amazon-luna-usb", false), ("amazon-luna-ble", false),
                ("switch-pro", false),
            })
            {
                var c = Make(id, armed);
                variants.Add(($"SubmitState,{id}{(armed ? " armed" : "")}", i => c.SubmitState((i & 1) == 0 ? a : b)));
            }
        if (!repeat)
        {
            var c = Make("dualshock-4-v2", false);
            var raw = new byte[63];
            variants.Add(("SubmitRawReport,dualshock-4-v2", i => { raw[0] = (byte)i; c.SubmitRawReport(raw); }));
        }
        if (!repeat)
        {
            var c = Make("switch-pro", false);
            var raw = new byte[48];
            variants.Add(("SubmitRawReport,switch-pro", i => { raw[5] = (byte)i; c.SubmitRawReport(raw); }));
        }
#if !HM_BASE
        if (!repeat)
        {
            // One tick of the idle repeat, the work from the clock's wake to
            // the section write: the last state encoded again and written.
            // Called directly, on a controller without its clock, after one
            // SubmitState, whose state every call repeats.
            var c = Make("steam-deck-composite", false);
            var tick = typeof(HMController).GetMethod("RepeatLastFrame", Instance)!.CreateDelegate<Action>(c);
            typeof(HMController).GetField("_idleThread", Instance)!.SetValue(c, new System.Threading.Thread(() => { }));
            c.SubmitState(a);
            variants.Add(("idle tick,steam-deck-composite", i => tick()));
        }
#endif
#if !HM_BASE
        // The protocol host's tick, in each of switch-pro's two modes. A
        // subcommand 0x02 (device info) is the host speaking the protocol,
        // which selects the full mode. The simple mode is the device before
        // any host speaks it.
        foreach (bool full in repeat ? Array.Empty<bool>() : new[] { true, false })
        {
            var c = Make("switch-pro", false);
            var host = (ProtocolHost)typeof(HMController).GetField("_host", Instance)!.GetValue(c)!;
            if (full)
            {
                var cmd = new byte[49];
                cmd[0] = 0x01; cmd[10] = 0x02;
                host.OnOutput(cmd, 49, 0);
                host.Tick(0);
            }
            variants.Add(($"SubmitState and a tick,switch-pro {(full ? "0x30" : "0x3F")}", i =>
            {
                c.SubmitState((i & 1) == 0 ? a : b);
                host.Tick(0);
            }));
        }
#endif

        var times = variants.Select(_ => new List<double>()).ToList();
        var bytes = new long[variants.Count];
        // Past the tiered JIT's call-count threshold and its background
        // recompile, as Ds3Cost warms up: a launch with few variants
        // otherwise measures some of them before their tier-1 code is in.
        foreach (var (_, call) in variants)
            for (int i = 0; i < Math.Min(calls, 20_000); i++) call(i);
        System.Threading.Thread.Sleep(500);
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
            Console.WriteLine(FormattableString.Invariant($"submit,{variants[k].Name},{t[t.Count / 2]:F3},{bytes[k]}"));
        }
        // The repeat's clock stops before its view is freed. b4d29d9 runs
        // no repeat for these profiles and has no StopThreads.
        var stop = typeof(HMController).GetMethod("StopThreads", Instance);
        if (stop != null) foreach (var c in made) stop.Invoke(c, null);
        foreach (var c in cleanups) c();
    }

#if !HM_BASE
    /// <summary>submit-bytes [states]: every catalog profile a UMDF2
    /// controller presents, built offline as above, armed and not where it
    /// arms, given the same random states and raw reports from a fixed seed.
    /// Prints a hash of the whole input section after each call, one line
    /// per controller, so two trees' outputs compare line by line: a change
    /// that leaves every frame's bytes alone prints the same lines. The
    /// states carry their own sensor stamps, so no frame reads the
    /// clock, and a protocol host's clock is ticked by hand, so its counters
    /// count ticks.</summary>
    public static void Bytes(int states)
    {
        var db = ((ProfileDatabase)typeof(ProfileDatabase).GetMethod("LoadEmbeddedFresh", Static)!.Invoke(null, null)!).All;
        var ctor = typeof(HMController).GetConstructor(Instance, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
        }) ?? throw new MissingMethodException("HMController's constructor changed.");
        var all = Enum.GetValues<HMAxis>();
        int index = 7800;
        foreach (var p in db.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (p.RequiresUsbipBackend) continue;
            p.Stream = null;
            if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
            foreach (bool armed in new[] { false, true })
            {
                IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
                Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
                int at = index++;
                Cache("s_inputViews")[at] = view;
                Cache("s_outputViews")[at] = IntPtr.Zero;
                string name = p.Id + (armed ? " armed" : "");
                try
                {
                    var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
                    HMController c;
                    try { c = (HMController)ctor.Invoke(new object?[] { context, at, new HMProfile(p), null, null, null }); }
                    catch (TargetInvocationException e) { Console.WriteLine($"bytes,{name},skip {e.InnerException?.GetType().Name}"); continue; }
                    typeof(HMController).GetField("_deliverEvery", Instance)!.SetValue(c, false);
                    // A device whose protocol the SDK runs ticks its host's
                    // clock after each call, and in its second pass a host
                    // has spoken first (Ds3Cost's subcommand). Any other
                    // device's second pass is armed, where it arms.
                    var host = (ProtocolHost?)typeof(HMController).GetField("_host", Instance)!.GetValue(c);
                    if (armed && host != null)
                    {
                        var cmd = new byte[49];
                        cmd[0] = 0x01; cmd[10] = 0x02;
                        host.OnOutput(cmd, cmd.Length, 0);
                    }
                    else if (armed)
                    {
                        if (typeof(HMController).GetField("_extendedReportBuffer", Instance)!.GetValue(c) == null) continue;
                        typeof(HMController).GetField("_extendedModeArmed", Instance)!.SetValue(c, true);
                    }
                    var rng = new Random(20261019);
                    var section = new byte[SharedMemoryIO.SHARED_INPUT_SIZE];
                    ulong hash = 14695981039346656037UL;
                    void Fold()
                    {
                        Marshal.Copy(view, section, 0, section.Length);
                        foreach (byte x in section) hash = (hash ^ x) * 1099511628211UL;
                    }
                    int rawLength = p.StateBlock != null ? p.StateBlockSpecForCodec().Size
                        : Math.Clamp(p.GetOrBuildReportBuilder().InputReportByteSize - 1, 1, SharedMemoryIO.DATA_CAPACITY);
                    var raw = new byte[rawLength];
                    uint stamp = 1000;
                    for (int i = 0; i < states; i++)
                    {
                        var st = new HMGamepadState
                        {
                            Buttons = (HMButton)(uint)rng.Next(),
                            Hat = (HMHat)rng.Next(0, 9),
                            PressureA = (byte)rng.Next(256),
                            AccelGY = (float)(rng.NextDouble() * 4 - 2),
                            GyroDpsX = (float)(rng.NextDouble() * 400 - 200),
                            BatteryLevel = (byte)rng.Next(11),
                            TouchpadFinger0Active = rng.Next(2) == 0,
                            TouchpadFinger0X = (ushort)rng.Next(1920),
                            SensorTimestamp = stamp += (uint)rng.Next(1, 20000),
                        };
                        int shape = rng.Next(8);
                        if (shape == 1) st.HatDegrees = (float)(rng.NextDouble() * 360);
                        if (shape != 0)
                        {
                            st.Axes = new Dictionary<HMAxis, float>();
                            foreach (var k in all)
                                if (rng.Next(3) != 0) st.Axes[k] = shape == 2 && rng.Next(4) == 0 ? float.NaN : (float)(rng.NextDouble() * 1.2 - 0.1);
                        }
                        c.SubmitState(in st);
                        Fold();
                        if (host != null)
                        {
                            host.Tick(0);
                            Fold();
                        }
                        if ((i & 3) == 0)
                        {
                            rng.NextBytes(raw);
                            c.SubmitRawReport(raw);
                            Fold();
                        }
                    }
                    Console.WriteLine($"bytes,{name},{hash:X16}");
                    typeof(HMController).GetMethod("StopThreads", Instance)!.Invoke(c, null);
                }
                finally
                {
                    Cache("s_inputViews").Remove(at);
                    Cache("s_outputViews").Remove(at);
                    Marshal.FreeHGlobal(view);
                }
            }
        }
    }
#endif

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;
}
