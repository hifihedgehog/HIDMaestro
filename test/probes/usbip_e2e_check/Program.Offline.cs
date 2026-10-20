// S45 offline: the device sweep's release of this process's sections
// (SharedMemoryIO.Cleanup, its last step) while a controller is alive, and
// a controller's teardown while one of its threads or a PID call is still
// in flight. No device, no elevation: each controller runs on sections this
// probe maps itself, unnamed and pagefile-backed, handed in through
// SharedMemoryIO's caches as device_oracle_check's cases hand in process
// memory. A real mapping is the point: an unmapped view is an access
// violation on the next write, which is how the live S45 died.
//
// "--offline" runs every case. "--offline <case>" runs one, so the
// pre-fix code shows each case's own failure: there the first write to a
// released view ends the process with 0xC0000005.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.NonPublic;

    // Far from the indexes a running program takes, and from the ones the
    // offline probes use in their own processes.
    const int OfflineBase = 7970;

    static readonly (string Name, Action<int> Run)[] OfflineCases =
    {
        ("idle", OfflineIdleRepeat),
        ("submit", OfflineSubmit),
        ("pid", OfflinePid),
        ("host", OfflineHostClock),
        ("late-tick", OfflineLateTick),
        ("pid-dispose", OfflinePidDuringDispose),
        ("stop", OfflineStopThreads),
        ("gate", OfflineCleanupGate),
        ("create", OfflineCreateRefusedAfterHold),
        ("removed", OfflineDriverRemoved),
    };

    static int Offline(string? only)
    {
        Console.WriteLine("=== S45, offline: a sweep's release of the process's sections, and a teardown, with live controllers ===");
        // A fault in the pre-fix code ends the process with its exit code
        // and no error dialog.
        SetErrorMode(QuietErrorMode);
        int ran = 0;
        for (int i = 0; i < OfflineCases.Length; i++)
        {
            if (only != null && OfflineCases[i].Name != only) continue;
            OfflineCases[i].Run(OfflineBase + i);
            ran++;
        }
        if (ran == 0)
        {
            Console.WriteLine($"no offline case named '{only}': {string.Join(", ", OfflineCases.Select(c => c.Name))}");
            return 1;
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>S45's crash: dualsense-composite's idle repeat (stream
    /// periodMs 4) writes its input section every 4 ms on the device clock's
    /// thread, and the sweep ran SharedMemoryIO.Cleanup under it. The sweep
    /// leaves the sections mapped, stops the repeat and the output reader of
    /// the device it removed, and SubmitState still writes the section.</summary>
    static void OfflineIdleRepeat(int index)
    {
        Console.WriteLine("\n-- The idle repeat across a sweep's release --");
        using var dev = new OfflineController("dualsense-composite", index);
        Check("the idle repeat writes the input section before the sweep", dev.Advances(), $"seq {dev.Seq}");
        var threads = dev.Threads();
        Check("both sections are mapped before the sweep", Mapped(dev.Input) && Mapped(dev.Ring));
        // An index beside it that no controller holds, as a closed
        // program's or a failed create's: the sweep releases that one.
        var unheld = Unheld(index + 200);
        Sweep();
        Check("the sweep releases an index no controller holds, beside the held one", unheld.All(v => !Mapped(v)));
        Check("the sweep leaves a live controller's input and output sections mapped",
            Mapped(dev.Input) && Mapped(dev.Ring));
        Check("the sweep stops the idle repeat and the output reader of the device it removed",
            threads.Count == 2 && threads.All(t => !t.IsAlive), string.Join(", ", threads.Where(t => t.IsAlive).Select(t => t.Name)));
        var s = new HMGamepadState { Buttons = HMButton.A };
        uint before = dev.SeqOrZero;
        dev.C.SubmitState(in s);
        Check("SubmitState after the sweep writes the controller's own section", dev.SeqOrZero == before + 2, $"seq {before} to {dev.SeqOrZero}");
        dev.TearDown();
        ReleasedOnceUnheld(index);
    }

    /// <summary>A consumer's own SubmitState on a controller it still holds
    /// after the sweep. The idle repeat is stopped first, so the consumer's
    /// thread is the only writer.</summary>
    static void OfflineSubmit(int index)
    {
        Console.WriteLine("\n-- SubmitState across a sweep's release --");
        using var dev = new OfflineController("dualsense-composite", index);
        dev.StopIdleRepeat();
        var s = new HMGamepadState { Buttons = HMButton.A };
        // Once before the sweep, so nothing the first call compiles can
        // take the address a released view leaves free.
        uint first = dev.Seq;
        dev.C.SubmitState(in s);
        Check("SubmitState writes the controller's section before the sweep", dev.Seq == first + 2, $"seq {first} to {dev.Seq}");
        Sweep();
        Check("the sweep leaves a live controller's input section mapped", Mapped(dev.Input));
        uint before = dev.SeqOrZero;
        dev.C.SubmitState(in s);
        Check("SubmitState after the sweep writes the controller's own section", dev.SeqOrZero == before + 2, $"seq {before} to {dev.SeqOrZero}");
        dev.TearDown();
        ReleasedOnceUnheld(index);
    }

    /// <summary>A PID publish after the sweep, on the PID section the
    /// controller mapped before it.</summary>
    static void OfflinePid(int index)
    {
        Console.WriteLine("\n-- A PID publish across a sweep's release --");
        using var dev = new OfflineController("dualsense-composite", index, pid: true);
        dev.StopIdleRepeat();
        dev.C.PublishPidPool(0xFFFF, 10, false, false);
        uint before = dev.PidSeq;
        Sweep();
        bool mapped = Mapped(dev.Pid);
        Check("the sweep leaves a live controller's PID section mapped", mapped);
        dev.C.PublishPidPool(0xFFFF, 10, false, false);
        uint after = mapped ? dev.PidSeq : 0;
        Check("PublishPidPool after the sweep writes the controller's own PID section", after == before + 2, $"seq {before} to {after}");
        dev.TearDown();
        ReleasedOnceUnheld(index);
    }

    /// <summary>A UMDF2 protocol host's clock (switch-pro, 15 ms) across the
    /// sweep's release, which stops it with the device. Report 0x30 goes out on the clock once a subcommand
    /// sets protocolSeen, so one goes through the ring first, as SDL's open
    /// sends them.</summary>
    static void OfflineHostClock(int index)
    {
        Console.WriteLine("\n-- A protocol host's clock across a sweep's release --");
        using var dev = new OfflineController("switch-pro", index);
        dev.StopIdleRepeat();
        dev.StartProtocol();
        var subcommand = new byte[48];
        subcommand[9] = 0x48;
        dev.Output(0x01, subcommand);
        Thread.Sleep(100);
        Check("the protocol clock writes the input section before the sweep", dev.Advances(), $"seq {dev.Seq}");
        var threads = dev.Threads();
        Sweep();
        Check("the sweep stops the protocol clock and the output reader of the device it removed",
            threads.Count == 2 && threads.All(t => !t.IsAlive), string.Join(", ", threads.Where(t => t.IsAlive).Select(t => t.Name)));
        var host = (ProtocolHost)typeof(HMController).GetField("_host", AnyInstance)!.GetValue(dev.C)!;
        uint before = dev.SeqOrZero;
        host.Tick(0);
        Check("a protocol tick after the sweep writes the controller's own section", Mapped(dev.Input) && dev.Seq != before,
            $"seq {before} to {dev.SeqOrZero}");
        dev.TearDown();
        ReleasedOnceUnheld(index);
    }

    /// <summary>A protocol host tick that comes after the teardown unmapped
    /// the section: what the clock's thread runs when Dispose's bounded
    /// join of it gave up. The tick is run here on this thread.</summary>
    static void OfflineLateTick(int index)
    {
        Console.WriteLine("\n-- A protocol tick after the teardown --");
        using var dev = new OfflineController("switch-pro", index);
        dev.StopIdleRepeat();
        dev.StartProtocol();
        // A subcommand sets protocolSeen, so report 0x30 goes out on every
        // tick, the late one included.
        var subcommand = new byte[48];
        subcommand[9] = 0x48;
        dev.Output(0x01, subcommand);
        Thread.Sleep(100);
        var host = (ProtocolHost)typeof(HMController).GetField("_host", AnyInstance)!.GetValue(dev.C)!;
        uint before = dev.Seq;
        host.Tick(0);
        uint after = dev.Seq;
        host.Tick(0);
        Check("each tick writes the input section while the controller lives", after != before && dev.Seq != after,
            $"seq {before} to {after} to {dev.Seq}");
        dev.TearDown();
        Check("the teardown unmapped the input section", !Mapped(dev.Input));
        host.Tick(0);
        Check("a tick after the teardown writes nothing and the process lives", true);
    }

    /// <summary>A PID call that passed its first disposed check while the
    /// controller is torn down. The SDK's hook holds the call between that
    /// check and the PID lock until the teardown has taken the lock, marked
    /// the controller disposed and unmapped the section, so on every run
    /// the check inside the lock is what refuses it. Each of the four PID
    /// members in turn.</summary>
    static void OfflinePidDuringDispose(int index)
    {
        Console.WriteLine("\n-- A PID call in flight during the teardown --");
        var hook = typeof(HMController).GetField("BeforePidLock", AnyStatic);
        Check("HMController has the hook that holds a PID call before its lock", hook != null);
        if (hook == null) return;
        var calls = new (string Name, Action<HMController> Call)[]
        {
            ("PublishPidPool", c => c.PublishPidPool(0x00FF, 4, false, false)),
            ("PublishPidBlockLoad", c => c.PublishPidBlockLoad(1, PidLoadStatus.Success, 0x00FF)),
            ("PublishPidState", c => c.PublishPidState(1, PidStateFlags.ActuatorsEnabled)),
            ("GetCurrentPidBlockLoad", c => c.GetCurrentPidBlockLoad()),
        };
        foreach (var (name, call) in calls)
        {
            using var dev = new OfflineController("dualsense-composite", index, pid: true);
            dev.StopIdleRepeat();
            // Maps the PID view and compiles the paths before the race.
            dev.C.PublishPidPool(0xFFFF, 10, false, false);
            object pidLock = typeof(HMController).GetField("_pidStateLock", AnyInstance)!.GetValue(dev.C)!;
            using var parked = new ManualResetEventSlim();
            using var go = new ManualResetEventSlim();
            using var tornDown = new ManualResetEventSlim();
            string outcome = "none";
            Thread? caller = null;
            caller = new Thread(() =>
            {
                try { call(dev.C); outcome = "completed"; }
                catch (ObjectDisposedException) { outcome = "refused as disposed"; }
                catch (Exception e) { outcome = e.GetType().Name; }
            }) { IsBackground = true, Name = "S45 offline PID call" };
            var teardown = new Thread(() => { dev.TearDown(); tornDown.Set(); }) { IsBackground = true, Name = "S45 offline teardown" };
            hook.SetValue(null, (Action)(() =>
            {
                if (Thread.CurrentThread != caller) return;
                parked.Set();
                go.Wait(5000);
            }));
            bool waited;
            try
            {
                caller.Start();
                Check($"{name}: the call passed its first disposed check and waits before the PID lock", parked.Wait(3000));
                // The teardown takes the PID lock after the submit lock, so
                // while this thread holds it the teardown cannot finish.
                Monitor.Enter(pidLock);
                try
                {
                    teardown.Start();
                    waited = !tornDown.Wait(250);
                }
                finally { Monitor.Exit(pidLock); }
                Check($"{name}: the teardown waits for the PID lock before the section goes", waited);
                Check($"{name}: the teardown ends while the call waits", tornDown.Wait(5000));
                Check($"{name}: the teardown unmapped the PID section", !Mapped(dev.Pid));
            }
            finally
            {
                go.Set();
                hook.SetValue(null, null);
            }
            bool joined = caller.Join(5000) & teardown.Join(5000);
            Check($"{name}: the call then takes the lock and the check inside it refuses it, and the process lives",
                joined && outcome == "refused as disposed", outcome);
        }
    }

    /// <summary>What a create whose controller fails after its constructor
    /// (StartProtocol throwing) calls before its teardown unmaps the
    /// sections: StopThreads must leave no thread that uses them.</summary>
    static void OfflineStopThreads(int index)
    {
        Console.WriteLine("\n-- StopThreads before a failed create's teardown --");
        var stop = typeof(HMController).GetMethod("StopThreads", AnyInstance);
        Check("HMController has the StopThreads a failed create calls", stop != null);
        if (stop == null) return;
        foreach (var (id, i) in new[] { ("dualsense-composite", index), ("switch-pro", index + 100) })
        {
            using var dev = new OfflineController(id, i);
            dev.StartProtocol();
            var threads = dev.Threads();
            Check($"{id}: the controller runs its threads before the stop", threads.All(t => t.IsAlive),
                string.Join(", ", threads.Select(t => t.Name)));
            stop.Invoke(dev.C, null);
            Check($"{id}: StopThreads ends every one of them", threads.All(t => !t.IsAlive),
                string.Join(", ", threads.Where(t => t.IsAlive).Select(t => t.Name)));
            Check($"{id}: and forgets the output reader", !OutputPumpRegistered(i));
            SharedMemoryIO.DestroyController(i);
            Thread.Sleep(50);
            Check($"{id}: its sections unmapped after it, and the process lives", !Mapped(dev.Input) && !Mapped(dev.Ring));
        }
    }

    /// <summary>A create maps its index before its controller holds it, and
    /// only CreateGate keeps the sweep's release out of that window: the
    /// create holds the read side, the sweep the write side. So the release
    /// refuses to run without the write side, and leaves the mapping.</summary>
    static void OfflineCleanupGate(int index)
    {
        Console.WriteLine("\n-- The release outside the sweep's gate --");
        // An index mapped and not held, as a create's is before its
        // controller's constructor.
        IntPtr input = OfflineController.Plant("s_inputViews", "s_inputHandles", index, SharedMemoryIO.SHARED_INPUT_SIZE);
        Check("the release refuses to run without CreateGate", CleanupRefused(out string how), how);
        Check("and leaves the mapping", Mapped(input));
        OwnerSweep.CreateGate.EnterReadLock();
        try
        {
            Check("the release refuses to run under the read side a create holds", CleanupRefused(out how), how);
            Check("and leaves the create's mapping", Mapped(input));
        }
        finally { OwnerSweep.CreateGate.ExitReadLock(); }
        Sweep();
        Check("under the write side, as the sweep runs it, it releases the mapping", !Mapped(input));
    }

    /// <summary>A create whose controller exists and holds its index, and
    /// then meets HMResourceInUseException: the type SetupController throws
    /// when it refuses before it makes a device, which the create rethrows
    /// without a teardown. Thrown after the constructor, as StartProtocol
    /// would throw it, the create must still tear down: the hold ends, the
    /// sections unmap and the output reader stops. The create runs through
    /// HMContext.CreateUnderLease with its machine steps replaced: a lease
    /// on a probe index, sections the probe maps for the devices, and a
    /// teardown that does what TeardownController does first
    /// (SharedMemoryIO.DestroyController).</summary>
    static void OfflineCreateRefusedAfterHold(int index)
    {
        Console.WriteLine("\n-- A create refused once its controller holds its index --");
        var t = typeof(HMContext);
        var acquire = t.GetField("AcquireLease", AnyStatic);
        var setup = t.GetField("SetupDevices", AnyStatic);
        var teardown = t.GetField("TeardownDevices", AnyStatic);
        var built = t.GetField("AfterControllerBuilt", AnyStatic);
        var create = t.GetMethod("CreateUnderLease", AnyInstance);
        Check("HMContext has the create's machine steps a check can replace",
            acquire != null && setup != null && teardown != null && built != null && create != null);
        if (acquire == null || setup == null || teardown == null || built == null || create == null) return;
        object?[] saved = { acquire.GetValue(null), setup.GetValue(null), teardown.GetValue(null), built.GetValue(null) };
        IntPtr input = IntPtr.Zero, ring = IntPtr.Zero;
        int teardowns = 0;
        List<Thread> threads = new();
        Exception? thrown = null;
        try
        {
            acquire.SetValue(null, (Func<int, string?, string, string, ControllerLease>)((slot, key, backend, id) => OfflineLease(index)));
            setup.SetValue(null, (Func<int, ControllerProfile, DeviceIdentity?, Action<string>?, string?>)((i, p, identity, created) =>
            {
                input = OfflineController.Plant("s_inputViews", "s_inputHandles", i, SharedMemoryIO.SHARED_INPUT_SIZE);
                ring = OfflineController.Plant("s_outputViews", "s_outputHandles", i, SharedMemoryIO.SHARED_OUTPUT_SIZE);
                SharedMemoryIO.MarkOutputStart(i);
                return null;
            }));
            teardown.SetValue(null, (Action<int, string?>)((i, instanceId) =>
            {
                teardowns++;
                SharedMemoryIO.DestroyController(i);
            }));
            built.SetValue(null, (Action<HMController>)(c =>
            {
                if (typeof(HMController).GetField("_outputThread", AnyInstance)!.GetValue(c) is Thread o) threads.Add(o);
                throw new HMResourceInUseException("refused after the controller holds its index", "probe", null);
            }));
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(CatalogJson("switch-pro")))!;
            p.ValidateDeviceModel();
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            try { create.Invoke(context, new object?[] { 0, new HMProfile(p), null }); }
            catch (TargetInvocationException e) { thrown = e.InnerException; }
        }
        finally
        {
            acquire.SetValue(null, saved[0]);
            setup.SetValue(null, saved[1]);
            teardown.SetValue(null, saved[2]);
            built.SetValue(null, saved[3]);
        }
        Check("the create throws the refusal", thrown is HMResourceInUseException, thrown?.GetType().Name ?? "nothing");
        Check("the controller ran its output reader before the refusal", threads.Count == 1);
        Check("the create tore down what it made", teardowns == 1, $"{teardowns} teardown(s)");
        bool held = Held(index);
        Check("the controller's hold ended", !held);
        Check("its input and output sections are unmapped", input != IntPtr.Zero && !Mapped(input) && !Mapped(ring));
        Check("its output reader ended", threads.All(o => !o.IsAlive));
        // What the old code leaves, so the cases after this one start clean.
        if (held || teardowns == 0) SharedMemoryIO.DestroyController(index);
    }

    /// <summary>A lease on <paramref name="index"/> that holds no machine
    /// claim and wrote no record, so its Dispose has nothing to give back.</summary>
    static ControllerLease OfflineLease(int index)
    {
        var ctor = typeof(ControllerLease).GetConstructor(AnyInstance, new[]
        {
            typeof(int), typeof(DeviceIdentity), typeof(Microsoft.Win32.SafeHandles.SafeFileHandle), typeof(Microsoft.Win32.SafeHandles.SafeFileHandle),
        }) ?? throw new MissingMethodException("ControllerLease's private constructor changed.");
        return (ControllerLease)ctor.Invoke(new object[]
        {
            index, DeviceIdentity.ForIndex(index),
            new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false), new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false),
        });
    }

    static bool Held(int index) =>
        (bool)typeof(SharedMemoryIO).GetMethod("IsHeld", AnyStatic)!.Invoke(null, new object[] { index })!;

    static bool CleanupRefused(out string how)
    {
        try
        {
            SharedMemoryIO.Cleanup();
            how = "it ran";
            return false;
        }
        catch (InvalidOperationException e)
        {
            how = e.Message;
            return true;
        }
    }

    /// <summary>A controller the sweep kept while it removed the device:
    /// nothing waits for, or reports, the driver that served it. A keyboard
    /// and a protocol host, each on a planted acknowledgement and version
    /// section a driver has announced itself in, as driver.c's
    /// TryOpenInputAck and AnnounceVersion write them.</summary>
    static void OfflineDriverRemoved(int index)
    {
        Console.WriteLine("\n-- What a kept controller reads of a removed driver --");
        using (var dev = new OfflineController("apple-keyboard-a1243", index))
        {
            var (ack, _) = dev.Announce(SharedMemoryIO.INPUT_ACK_CAP_DELIVERED | SharedMemoryIO.INPUT_ACK_CAP_RELEASE | SharedMemoryIO.INPUT_ACK_WATCH_ENDED);
            Check("before the sweep the controller reports the driver's version", dev.C.DriverVersion == new Version(2, 0, 0), $"{dev.C.DriverVersion}");
            Check("and that the driver releases its keys if the program ends", dev.C.ReleasesOnConsumerExit);
            HMKey[] key = { HMKey.A };
            dev.C.SubmitKeyboard(HMKeyModifiers.None, key);
            long waited = Timed(() => dev.C.SubmitKeyboard(HMKeyModifiers.None, Array.Empty<HMKey>()));
            Check("before the sweep a frame waits for the acknowledgement of the one before it", waited >= 150, $"{waited} ms");
            // The driver acknowledges, so the next frame finds no miss.
            Marshal.WriteInt32(ack, SharedMemoryIO.INPUT_ACK_OFFSET_DELIVERED, (int)dev.Seq);
            dev.C.SubmitKeyboard(HMKeyModifiers.None, key);
            Sweep();
            Check("after the sweep the controller reports no driver version", dev.C.DriverVersion == null, $"{dev.C.DriverVersion}");
            Check("and no release on exit", !dev.C.ReleasesOnConsumerExit);
            Check("and the driver's own marker bit stays",
                (Marshal.ReadInt32(ack, SharedMemoryIO.INPUT_ACK_OFFSET_CAPS) & SharedMemoryIO.INPUT_ACK_WATCH_ENDED) != 0);
            uint before = dev.Seq;
            long three = Timed(() =>
            {
                dev.C.SubmitKeyboard(HMKeyModifiers.None, Array.Empty<HMKey>());
                dev.C.SubmitKeyboard(HMKeyModifiers.None, key);
                dev.C.SubmitKeyboard(HMKeyModifiers.None, Array.Empty<HMKey>());
            });
            // A frame that waits for an acknowledgement waits 200 ms
            // (HMController.DeliveryTimeoutMs), so three that wait take 600 ms.
            // Three that do not take well under a millisecond, and 400 ms is
            // room for a machine busy with other work.
            Check("three keyboard frames after the sweep wait for no acknowledgement", three < 400, $"{three} ms");
            Check("and each one writes the controller's own section", dev.Seq == before + 6, $"seq {before} to {dev.Seq}");
            dev.TearDown();
        }
        using (var dev = new OfflineController("switch-pro", index + 100))
        {
            var (_, version) = dev.Announce(SharedMemoryIO.INPUT_ACK_CAP_DELIVERED);
            dev.StopIdleRepeat();
            dev.StartProtocol();
            var subcommand = new byte[48];
            subcommand[9] = 0x48;
            dev.Output(0x01, subcommand);
            Thread.Sleep(100);
            var host = (ProtocolHost)typeof(HMController).GetField("_host", AnyInstance)!.GetValue(dev.C)!;
            // The clock stops first, so the reply set out below is still
            // waiting when the sweep runs: one the driver never delivered.
            host.Dispose();
            typeof(ProtocolHost).GetField("_replySeq", AnyInstance)!.SetValue(host, dev.Seq + 1000);
            typeof(ProtocolHost).GetField("_replyPending", AnyInstance)!.SetValue(host, true);
            Sweep();
            Check("after the sweep the protocol host's controller reports no driver",
                Marshal.ReadInt32(version, SharedMemoryIO.VERSION_OFFSET_DRIVER_PROTOCOL) == 0 && dev.C.DriverVersion == null);
            // The wait it guards is one 200 ms DeliveryTimeoutMs, too close to
            // what a busy machine adds for a time to tell them apart. The
            // sweep's DeviceRemoved clears the pending reply, and a send with
            // none pending waits for nothing (ProtocolHost.AwaitReplyLocked).
            // The send's time is printed.
            bool pending = (bool)typeof(ProtocolHost).GetField("_replyPending", AnyInstance)!.GetValue(host)!;
            uint before = dev.Seq;
            long tick = Timed(() => host.Tick(200));
            Console.WriteLine($"    info: the protocol send after the sweep took {tick} ms");
            Check("a protocol send after the sweep waits for no reply the removed driver never delivered", !pending, $"reply pending {pending}, {tick} ms");
            Check("and writes the controller's own section", dev.Seq != before, $"seq {before} to {dev.Seq}");
            dev.TearDown();
        }
    }

    static long Timed(Action a)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        a();
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Sections mapped on <paramref name="index"/> with no
    /// controller, as a closed program or a failed create leaves them.</summary>
    static IntPtr[] Unheld(int index) => new[]
    {
        OfflineController.Plant("s_inputViews", "s_inputHandles", index, SharedMemoryIO.SHARED_INPUT_SIZE),
        OfflineController.Plant("s_outputViews", "s_outputHandles", index, SharedMemoryIO.SHARED_OUTPUT_SIZE),
        OfflineController.Plant("s_pidStateViews", "s_pidStateHandles", index, SharedMemoryIO.PID_STATE_SIZE),
    };

    /// <summary>Once the controller is torn down its index is no longer
    /// held: fresh sections on it with no controller are the sweep's to
    /// release. A hold the teardown never ended keeps them mapped.</summary>
    static void ReleasedOnceUnheld(int index)
    {
        var fresh = Unheld(index);
        Sweep();
        Check("after the teardown the sweep releases new sections on the index", fresh.All(v => !Mapped(v)));
    }

    /// <summary>SharedMemoryIO.Cleanup as the sweep runs it, under
    /// CreateGate's write side (DeviceOrchestrator.RemoveEverything).</summary>
    static void Sweep()
    {
        OwnerSweep.CreateGate.EnterWriteLock();
        try { SharedMemoryIO.Cleanup(); }
        finally { OwnerSweep.CreateGate.ExitWriteLock(); }
    }

    // ── The offline controller ─────────────────────────────────────────

    sealed class OfflineController : IDisposable
    {
        public readonly HMController C;
        public readonly IntPtr Input, Ring, Pid;
        readonly int _index;
        uint _head;
        bool _tornDown;

        public OfflineController(string profileId, int index, bool pid = false)
        {
            _index = index;
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(CatalogJson(profileId)))!;
            p.ValidateDeviceModel();
            Input = Plant("s_inputViews", "s_inputHandles", index, SharedMemoryIO.SHARED_INPUT_SIZE);
            Ring = Plant("s_outputViews", "s_outputHandles", index, SharedMemoryIO.SHARED_OUTPUT_SIZE);
            if (pid) Pid = Plant("s_pidStateViews", "s_pidStateHandles", index, SharedMemoryIO.PID_STATE_SIZE);
            SharedMemoryIO.MarkOutputStart(index);
            var ctor = typeof(HMController).GetConstructor(AnyInstance, new[]
            {
                typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
            }) ?? throw new MissingMethodException("HMController's private constructor changed.");
            // A context that never initialized: the controller is never
            // handed to it, and TearDown stops what Dispose would.
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            C = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
        }

        /// <summary>An unnamed pagefile-backed section, mapped and handed to
        /// SharedMemoryIO's caches as its own mapping, so its release unmaps
        /// it and closes the handle. A mark past the bytes the SDK uses tells
        /// this view from another mapping at its address once it is
        /// unmapped: a 64 KiB pagefile-backed view of this process's own has
        /// been seen there.</summary>
        public static IntPtr Plant(string views, string handles, int index, int size)
        {
            IntPtr h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, (uint)(size + 8), null);
            if (h == IntPtr.Zero) throw new InvalidOperationException($"CreateFileMappingW failed: {Marshal.GetLastWin32Error()}");
            IntPtr v = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
            if (v == IntPtr.Zero) throw new InvalidOperationException($"MapViewOfFile failed: {Marshal.GetLastWin32Error()}");
            long mark = Random.Shared.NextInt64() ^ (long)v;
            Marshal.WriteInt64(v, size, mark);
            lock (s_planted) s_planted[v] = (size, mark);
            Cache(views)[index] = v;
            Cache(handles)[index] = h;
            return v;
        }

        /// <summary>The acknowledgement and version sections, planted, with
        /// a driver's announcement in them: <paramref name="caps"/> in the
        /// acknowledgement, and version 2.0.0 with protocol 4 in the driver's
        /// half of the version section, protocol last as driver.c's
        /// AnnounceVersion writes it.</summary>
        public (IntPtr Ack, IntPtr Version) Announce(int caps)
        {
            IntPtr ack = Plant("s_inputAckViews", "s_inputAckHandles", _index, SharedMemoryIO.INPUT_ACK_SIZE);
            IntPtr version = Plant("s_versionViews", "s_versionHandles", _index, SharedMemoryIO.VERSION_INFO_SIZE);
            Marshal.WriteInt32(ack, SharedMemoryIO.INPUT_ACK_OFFSET_CAPS, caps);
            Marshal.WriteInt16(version, SharedMemoryIO.VERSION_OFFSET_DRIVER_VERSION, 2);
            Marshal.WriteInt32(version, SharedMemoryIO.VERSION_OFFSET_DRIVER_PROTOCOL, 4);
            return (ack, version);
        }

        public uint Seq => (uint)Marshal.ReadInt32(Input, 0);

        /// <summary>The sequence number, or 0 once the section is unmapped,
        /// which this check must not read.</summary>
        public uint SeqOrZero => Mapped(Input) ? Seq : 0;
        public uint PidSeq => (uint)Marshal.ReadInt32(Pid, SharedMemoryIO.PID_OFFSET_SEQNO);

        /// <summary>Whether the input section's sequence number moves within
        /// 500 ms.</summary>
        public bool Advances()
        {
            if (!Mapped(Input)) return false;
            uint start = Seq;
            return WaitUntil(() => Seq != start, 500);
        }

        public void StopIdleRepeat()
        {
            if (typeof(HMController).GetField("_idleClock", AnyInstance)?.GetValue(C) is IDeviceClock clock)
            {
                clock.Stop(1000);
                typeof(HMController).GetField("_idleClock", AnyInstance)!.SetValue(C, null);
            }
        }

        public void StartProtocol() => typeof(HMController).GetMethod("StartProtocol", AnyInstance)!.Invoke(C, null);

        /// <summary>The controller's threads: its output reader, its idle
        /// repeat's clock and its protocol host's clock, those it has.</summary>
        public List<Thread> Threads()
        {
            var list = new List<Thread>();
            if (typeof(HMController).GetField("_outputThread", AnyInstance)!.GetValue(C) is Thread o) list.Add(o);
            if (typeof(HMController).GetField("_idleClock", AnyInstance)?.GetValue(C) is IDeviceClock idle) list.Add(idle.Thread);
            if (typeof(HMController).GetField("_host", AnyInstance)?.GetValue(C) is ProtocolHost { ClockThread: { } h }) list.Add(h);
            return list;
        }

        /// <summary>An output report into the ring, as the driver's
        /// PublishOutput writes one (driver.c).</summary>
        public void Output(byte id, byte[] data)
        {
            uint seq = _head + 1;
            int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)((seq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
            Marshal.WriteByte(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, (byte)HMOutputSource.HidOutput);
            Marshal.WriteByte(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, id);
            Marshal.WriteInt16(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, (short)data.Length);
            if (data.Length > 0) Marshal.Copy(data, 0, Ring + slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, data.Length);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)seq);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(Ring, 0, (int)seq);
            _head = seq;
        }

        /// <summary>Dispose's half without the context's bookkeeping, which
        /// this controller has none of: its threads stop, then
        /// DestroyController unmaps its sections, as HMContext's teardown
        /// does. StopThreads where the SDK has it, else the stop sequence
        /// Dispose ran inline before StopThreads existed.</summary>
        public void TearDown()
        {
            if (_tornDown) return;
            _tornDown = true;
            if (typeof(HMController).GetMethod("StopThreads", AnyInstance) is { } stop)
                stop.Invoke(C, null);
            else
                StopAsDisposeDid(C);
            SharedMemoryIO.DestroyController(_index);
        }

        public void Dispose() => TearDown();
    }

    /// <summary>The thread stops of Dispose before StopThreads existed, in
    /// its order, for the pre-fix run.</summary>
    static void StopAsDisposeDid(HMController c)
    {
        object submitLock = typeof(HMController).GetField("_submitLock", AnyInstance)!.GetValue(c)!;
        lock (submitLock) typeof(HMController).GetField("_disposed", AnyInstance)!.SetValue(c, true);
        (typeof(HMController).GetField("_idleClock", AnyInstance)?.GetValue(c) as IDeviceClock)?.Stop(500);
        (typeof(HMController).GetField("_host", AnyInstance)?.GetValue(c) as IDisposable)?.Dispose();
        try { ((CancellationTokenSource)typeof(HMController).GetField("_outputCts", AnyInstance)!.GetValue(c)!).Cancel(); }
        catch (ObjectDisposedException) { }
        (typeof(HMController).GetField("_outputThread", AnyInstance)!.GetValue(c) as Thread)?.Join(500);
        SharedMemoryIO.UnregisterOutputPump(c.Index);
    }

    static bool OutputPumpRegistered(int index)
    {
        var pumps = (System.Collections.IDictionary)typeof(SharedMemoryIO).GetField("s_outputPumps", AnyStatic)!.GetValue(null)!;
        lock (pumps) return pumps.Contains(index);
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, AnyStatic)!.GetValue(null)!;

    static string CatalogJson(string id)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string t = sr.ReadToEnd();
            if (JsonNode.Parse(t) is JsonObject o && (string?)o["id"] == id) return t;
        }
        throw new InvalidOperationException($"no catalog profile {id}");
    }

    static bool WaitUntil(Func<bool> done, int ms)
    {
        long deadline = Environment.TickCount64 + ms;
        while (!done())
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(1);
        }
        return true;
    }

    static readonly Dictionary<IntPtr, (int Size, long Mark)> s_planted = new();

    /// <summary>Whether the section this probe planted at
    /// <paramref name="view"/> is still mapped there: committed memory of a
    /// mapped view based at that address, carrying the plant's mark. An
    /// unmapped view's range can hold another mapping at once.</summary>
    static bool Mapped(IntPtr view)
    {
        if (VirtualQuery(view, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == UIntPtr.Zero
            || mbi.State != MEM_COMMIT || mbi.Type != MEM_MAPPED || mbi.AllocationBase != view)
            return false;
        (int Size, long Mark) plant;
        lock (s_planted)
            if (!s_planted.TryGetValue(view, out plant)) return true;
        return (ulong)mbi.RegionSize >= (ulong)plant.Size + 8 && Marshal.ReadInt64(view, plant.Size) == plant.Mark;
    }

    const uint PAGE_READWRITE = 0x04;
    const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_MAPPED = 0x40000;
    const uint QuietErrorMode = 0x0001 | 0x0002 | 0x8000; // SEM_FAILCRITICALERRORS, SEM_NOGPFAULTERRORBOX, SEM_NOOPENFILEERRORBOX

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    static extern UIntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
}
