// --offline: HMVRController's submit lock and disposed checks, on channels
// of this process's own memory, so no section is created, nothing needs
// elevation and no SteamVR runs. The input frame is a single-writer seqlock
// in the channel, so two submits must never write it at once, and a submit
// or a status read must never touch a channel Dispose has released. Every
// default run of the probe runs these first.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Offline
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    public static void Run()
    {
        Console.WriteLine("\n-- offline: the submit lock and the released channel --");
        Single();
        Concurrent();
        RacingDispose();
        HeldAcrossDispose();
        AfterDispose();
    }

    /// <summary>A channel on memory of this process, with unnamed events.
    /// Its Dispose zeroes the view pointer and closes the events, and the
    /// memory itself stays readable until <see cref="Free"/>, so a write a
    /// submit makes after Dispose shows in the sequence number, and a read
    /// through the zeroed view throws rather than ending the process.</summary>
    sealed class Rig : IDisposable
    {
        public readonly IntPtr Memory;
        public readonly VrSharedMemory.Channel Channel;
        public readonly HMVRController Vr;

        public Rig()
        {
            Memory = Marshal.AllocHGlobal(VrSharedMemory.SectionSize);
            unsafe { new Span<byte>((void*)Memory, VrSharedMemory.SectionSize).Clear(); }
            Channel = new VrSharedMemory.Channel
            {
                View = Memory,
                InputEvent = CreateEventW(IntPtr.Zero, false, false, null),
                HapticEvent = CreateEventW(IntPtr.Zero, false, false, null),
            };
            Vr = Make(Channel);
        }

        public unsafe uint Seq => Volatile.Read(ref *(uint*)(Memory + VrSharedMemory.InputSeqNoOffset));

        public Thread Reader => (Thread)typeof(HMVRController).GetField("_hapticThread", Any)!.GetValue(Vr)!;

        /// <summary>Disposes the controller if the check has not, waits for
        /// its reader to release the channel, then frees the memory.</summary>
        public void Dispose()
        {
            Vr.Dispose();
            Reader.Join(10_000);
            Marshal.FreeHGlobal(Memory);
        }
    }

    /// <summary>The controller on <paramref name="ch"/>: through the
    /// constructor that takes a channel, or, on an SDK without one, by the
    /// public constructor's own steps.</summary>
    static HMVRController Make(VrSharedMemory.Channel ch)
    {
        var ctor = typeof(HMVRController).GetConstructor(Any, null, new[] { typeof(VrSharedMemory.Channel) }, null);
        if (ctor != null) return (HMVRController)ctor.Invoke(new object[] { ch });
        var vr = (HMVRController)RuntimeHelpers.GetUninitializedObject(typeof(HMVRController));
        void Set(string name, object value) => typeof(HMVRController).GetField(name, Any)!.SetValue(vr, value);
        Set("_channel", ch);
        Set("_frame", new byte[VrSharedMemory.InputFrameSize]);
        Set("_cts", new CancellationTokenSource());
        Set("_lifetimeLock", new object());
        VrSharedMemory.WriteConfig(ch, handsActive: 0x3);
        var loop = (ThreadStart)Delegate.CreateDelegate(typeof(ThreadStart), vr, typeof(HMVRController).GetMethod("HapticLoop", Any)!);
        var reader = new Thread(loop) { IsBackground = true, Name = "HMVRController.HapticLoop" };
        Set("_hapticThread", reader);
        reader.Start();
        return vr;
    }

    /// <summary>State <paramref name="t"/>: every scalar and both hands'
    /// buttons carry t, so a frame mixed from two states shows.</summary>
    static HMVRState State(int t)
    {
        var h = new HMVRHandState { Buttons = (HMVRButton)(uint)t, Trigger = t, Grip = t, StickX = t, StickY = t };
        return new HMVRState { Left = h, Right = h };
    }

    /// <summary>The state a frame carries, or -1 when its two hands or its
    /// fields disagree.</summary>
    static unsafe int StateOf(byte[] frame)
    {
        fixed (byte* f = frame)
        {
            int t = -1;
            for (int hand = 0; hand < 2; hand++)
            {
                byte* s = f + 8 + hand * VrSharedMemory.ControllerStateSize;
                int buttons = (int)*(uint*)s;
                float* scalars = (float*)(s + VrSharedMemory.StateScalarsOffset);
                for (int i = 0; i < 4; i++)
                    if (scalars[i] != buttons) return -1;
                if (t >= 0 && buttons != t) return -1;
                t = buttons;
            }
            return t;
        }
    }

    /// <summary>The frame as the driver reads it: an even sequence number,
    /// the bytes, then the same number again.</summary>
    static unsafe bool TryRead(IntPtr memory, byte[] into, out uint seq)
    {
        byte* v = (byte*)memory;
        seq = Volatile.Read(ref *(uint*)(v + VrSharedMemory.InputSeqNoOffset));
        if ((seq & 1) != 0) return false;
        new ReadOnlySpan<byte>(v + VrSharedMemory.InputFrameOffset, VrSharedMemory.InputFrameSize).CopyTo(into);
        Interlocked.MemoryBarrier();
        return Volatile.Read(ref *(uint*)(v + VrSharedMemory.InputSeqNoOffset)) == seq;
    }

    /// <summary>The control: one thread, each frame read right after its
    /// submit is the state submitted.</summary>
    static void Single()
    {
        using var rig = new Rig();
        var frame = new byte[VrSharedMemory.InputFrameSize];
        int wrong = 0;
        for (int t = 1; t <= 1000; t++)
        {
            rig.Vr.SubmitState(State(t));
            if (!TryRead(rig.Memory, frame, out _) || StateOf(frame) != t) wrong++;
        }
        Program.Check("offline: one thread's 1,000 submits each read back as the state submitted", wrong == 0, $"{wrong} differ");
    }

    /// <summary>Four threads submit their own state at once while a reader
    /// takes every frame it sees. A frame mixed from two submits, packed in
    /// one shared buffer or written into the seqlock by two writers at
    /// once, is none of the states.</summary>
    static void Concurrent()
    {
        using var rig = new Rig();
        const int Threads = 4, Submits = 20_000;
        int stop = 0, mixed = 0, read = 0, failed = 0;
        string first = "";
        var seen = new bool[Threads + 1];
        var reader = new Thread(() =>
        {
            var frame = new byte[VrSharedMemory.InputFrameSize];
            uint last = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                if (!TryRead(rig.Memory, frame, out uint seq) || seq == last) { Thread.SpinWait(8); continue; }
                last = seq;
                read++;
                int t = StateOf(frame);
                if (t is >= 1 and <= Threads) seen[t] = true;
                else if (mixed++ == 0) first = $", first {Convert.ToHexString(frame, 8, 24)}";
            }
        }) { IsBackground = true, Name = "offline frame reader" };
        reader.Start();
        var go = new ManualResetEventSlim();
        var submitters = Enumerable.Range(1, Threads).Select(t => new Thread(() =>
        {
            var s = State(t);
            go.Wait();
            try { for (int i = 0; i < Submits; i++) rig.Vr.SubmitState(in s); }
            catch (Exception) { Interlocked.Increment(ref failed); }
        }) { IsBackground = true, Name = $"offline submitter {t}" }).ToList();
        submitters.ForEach(s => s.Start());
        go.Set();
        submitters.ForEach(s => s.Join());
        Volatile.Write(ref stop, 1);
        reader.Join();
        int threadsSeen = seen.Count(x => x);
        Program.Check("offline: four threads submitting at once never write a frame mixed from two submits",
            mixed == 0 && failed == 0,
            $"{read} frames read, {mixed} mixed, {failed} submitter(s) threw, states of {threadsSeen} threads seen{first}");
    }

    /// <summary>Forty-eight threads submit, more than the machine has
    /// processors, so some are preempted between a submit's start and its
    /// write, while Dispose runs. Each submit returns before Dispose does
    /// or is refused with ObjectDisposedException, and nothing is written
    /// once Dispose has returned.</summary>
    static void RacingDispose()
    {
        const int Trials = 20, Threads = 48;
        int late = 0, odd = 0, refused = 0;
        var others = new List<string>();
        for (int trial = 0; trial < Trials; trial++)
        {
            var rig = new Rig();
            var started = new CountdownEvent(Threads);
            var submitters = Enumerable.Range(1, Threads).Select(t => new Thread(() =>
            {
                var s = State(t);
                bool counted = false;
                try
                {
                    for (int i = 0; ; i++)
                    {
                        rig.Vr.SubmitState(in s);
                        if (i == 50) { started.Signal(); counted = true; }
                    }
                }
                catch (ObjectDisposedException) { Interlocked.Increment(ref refused); }
                catch (Exception e) { lock (others) others.Add(e.GetType().Name); }
                finally { if (!counted) started.Signal(); }
            }) { IsBackground = true, Name = $"racing submitter {t}" }).ToList();
            submitters.ForEach(s => s.Start());
            started.Wait();
            rig.Vr.Dispose();
            uint atReturn = rig.Seq;
            submitters.ForEach(s => s.Join());
            if (rig.Seq != atReturn) late++;
            if ((rig.Seq & 1) != 0) odd++;
            rig.Dispose();
        }
        Program.Check("offline: submits racing Dispose return or are refused with ObjectDisposedException, and none writes after Dispose returns",
            others.Count == 0 && late == 0 && odd == 0,
            $"{Trials} trials of {Threads} threads, {refused} refused, other exceptions: {(others.Count == 0 ? "none" : string.Join(", ", others.GroupBy(n => n).Select(g => $"{g.Count()} {g.Key}")))}, trials with a write after Dispose {late}, left mid-write {odd}");
    }

    /// <summary>A submit held between its disposed check and its write while
    /// Dispose runs to the end, through the SDK's hook at that point, then
    /// let go: it is refused with ObjectDisposedException and writes
    /// nothing.</summary>
    static void HeldAcrossDispose()
    {
        var hook = typeof(HMVRController).GetField("BeforeSubmitLock", BindingFlags.Static | BindingFlags.NonPublic);
        if (hook == null)
        {
            Program.Check("offline: a submit held past its disposed check while Dispose runs is refused and writes nothing", false,
                "the SDK has no point between a submit's check and its write to hold it at");
            return;
        }
        var rig = new Rig();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread? held = null;
        string outcome = "none";
        hook.SetValue(null, (Action)(() =>
        {
            if (Thread.CurrentThread != held) return;
            entered.Set();
            release.Wait(15_000);
        }));
        try
        {
            held = new Thread(() =>
            {
                try { rig.Vr.SubmitState(State(7)); outcome = "returned"; }
                catch (ObjectDisposedException) { outcome = "ObjectDisposedException"; }
                catch (Exception e) { outcome = e.GetType().Name; }
            }) { IsBackground = true, Name = "held submitter" };
            held.Start();
            bool inHook = entered.Wait(10_000);
            uint before = rig.Seq;
            rig.Vr.Dispose();
            bool readerEnded = rig.Reader.Join(10_000);
            release.Set();
            held.Join();
            Program.Check("offline: a submit held past its disposed check while Dispose runs is refused and writes nothing",
                inHook && readerEnded && outcome == "ObjectDisposedException" && rig.Seq == before,
                $"held {inHook}, reader ended {readerEnded}, the submit {outcome}, sequence {before} then {rig.Seq}");
        }
        finally
        {
            release.Set();
            hook.SetValue(null, null);
            rig.Dispose();
        }
    }

    /// <summary>After Dispose a submit is refused with ObjectDisposedException,
    /// and DriverConnected, ControllersLive and GetHmdPose answer as if
    /// nothing were attached, without reading the released channel.</summary>
    static void AfterDispose()
    {
        var rig = new Rig();
        rig.Vr.Dispose();
        rig.Reader.Join(10_000);
        string submit;
        try { rig.Vr.SubmitState(State(3)); submit = "returned"; }
        catch (ObjectDisposedException) { submit = "ObjectDisposedException"; }
        catch (Exception e) { submit = e.GetType().Name; }
        Program.Check("offline: a submit after Dispose is refused with ObjectDisposedException", submit == "ObjectDisposedException", submit);
        string Read(Func<string> f) { try { return f(); } catch (Exception e) { return e.GetType().Name; } }
        string driver = Read(() => rig.Vr.DriverConnected.ToString());
        string live = Read(() => rig.Vr.ControllersLive.ToString());
        string pose = Read(() => rig.Vr.GetHmdPose().Valid.ToString());
        Program.Check("offline: after Dispose, DriverConnected, ControllersLive and GetHmdPose answer false without reading the released channel",
            driver == "False" && live == "False" && pose == "False", $"{driver}, {live}, {pose}");
        rig.Dispose();
    }
}
