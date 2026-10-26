// vr-submit [runs] [calls]: HMVRController.SubmitState's own cost per call,
// offline. The controller runs on a channel of this probe's memory with
// unnamed events, as vr_controller_smoke's offline phase builds one, so no
// section is created, nothing needs elevation and no SteamVR runs. A call
// packs both hands, writes the input frame through its seqlock and signals
// the input event, as a live submit does, so whatever a tree adds around
// that work shows. A tree without the constructor that takes a channel gets
// its controller by the public constructor's own steps.
//   - SubmitState with both hands' buttons, triggers, grips and sticks
//   - the same with both hands' poses, which pack each pose too
// Variants run interleaved run by run, at high process priority, and each
// prints its median ns per call and the most bytes the thread allocated in
// one run, as SubmitCost's do. A script launches it 30 times or more against
// each tree and takes p50 and p95 over the launches.
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

internal static class VrCost
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    public static void Submit(int runs, int calls)
    {
        using (var me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.High;
        IntPtr memory = Marshal.AllocHGlobal(VrSharedMemory.SectionSize);
        Marshal.Copy(new byte[VrSharedMemory.SectionSize], 0, memory, VrSharedMemory.SectionSize);
        var channel = new VrSharedMemory.Channel
        {
            View = memory,
            InputEvent = CreateEventW(IntPtr.Zero, false, false, null),
            HapticEvent = CreateEventW(IntPtr.Zero, false, false, null),
        };
        var vr = Make(channel);
        HMVRState a = State(1, pose: false), b = State(2, pose: false);
        HMVRState pa = State(1, pose: true), pb = State(2, pose: true);
        var variants = new List<(string Name, Action<int> Call)>
        {
            ("SubmitState,vr", i => vr.SubmitState((i & 1) == 0 ? a : b)),
            ("SubmitState,vr with poses", i => vr.SubmitState((i & 1) == 0 ? pa : pb)),
        };

        var times = variants.Select(_ => new List<double>()).ToList();
        var bytes = new long[variants.Count];
        // Past the tiered JIT's call-count threshold and its background
        // recompile, as SubmitCost warms up.
        foreach (var (_, call) in variants)
            for (int i = 0; i < Math.Min(calls, 20_000); i++) call(i);
        Thread.Sleep(500);
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
        // The reader releases the channel as it ends, before the memory goes.
        vr.Dispose();
        ((Thread)typeof(HMVRController).GetField("_hapticThread", Instance)!.GetValue(vr)!).Join(10_000);
        Marshal.FreeHGlobal(memory);
    }

    /// <summary>State <paramref name="t"/> on both hands, with each hand's
    /// pose when <paramref name="pose"/> is set.</summary>
    static HMVRState State(int t, bool pose)
    {
        var h = new HMVRHandState
        {
            Buttons = (HMVRButton)(uint)t,
            Trigger = 0.25f * t, Grip = 0.5f, StickX = -0.25f * t, StickY = 0.75f,
            PoseValid = pose,
            PositionX = 0.1f * t, PositionY = 1.2f, PositionZ = -0.3f,
            OrientationW = 1f, OrientationX = 0.01f * t, OrientationY = 0f, OrientationZ = 0f,
        };
        return new HMVRState { Left = h, Right = h };
    }

    /// <summary>The controller on <paramref name="ch"/>: through the
    /// constructor that takes a channel, or, on a tree without one, by the
    /// public constructor's own steps.</summary>
    static HMVRController Make(VrSharedMemory.Channel ch)
    {
        var ctor = typeof(HMVRController).GetConstructor(Instance, null, new[] { typeof(VrSharedMemory.Channel) }, null);
        if (ctor != null) return (HMVRController)ctor.Invoke(new object[] { ch });
        var vr = (HMVRController)RuntimeHelpers.GetUninitializedObject(typeof(HMVRController));
        void Set(string name, object value) => typeof(HMVRController).GetField(name, Instance)!.SetValue(vr, value);
        Set("_channel", ch);
        Set("_frame", new byte[VrSharedMemory.InputFrameSize]);
        Set("_cts", new CancellationTokenSource());
        Set("_lifetimeLock", new object());
        VrSharedMemory.WriteConfig(ch, handsActive: 0x3);
        var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), vr, typeof(HMVRController).GetMethod("HapticLoop", Instance)!);
        var reader = new Thread(loop) { IsBackground = true, Name = "HMVRController.HapticLoop" };
        Set("_hapticThread", reader);
        reader.Start();
        return vr;
    }
}
