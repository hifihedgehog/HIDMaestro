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
    /// thread, and the sweep ran SharedMemoryIO.Cleanup under it. The output
    /// reader, which #45 stopped before the unmap, must still read its ring
    /// afterward.</summary>
    static void OfflineIdleRepeat(int index)
    {
        Console.WriteLine("\n-- The idle repeat across a sweep's release --");
        using var dev = new OfflineController("dualsense-composite", index);
        Check("the idle repeat writes the input section before the sweep", dev.Advances(), $"seq {dev.Seq}");
        int raised = 0;
        dev.C.OutputReceived += (_, p) => { if (p.ReportId == 0x02) Interlocked.Increment(ref raised); };
        Check("both sections are mapped before the sweep", Mapped(dev.Input) && Mapped(dev.Ring));
        SharedMemoryIO.Cleanup();
        // A dozen of the repeat's periods before this thread does anything
        // else, so the repeat is the first to touch what the sweep did to
        // its section, as it was in the live run.
        Thread.Sleep(50);
        Check("the sweep leaves a live controller's input and output sections mapped",
            Mapped(dev.Input) && Mapped(dev.Ring));
        Check("the idle repeat keeps writing its section after the sweep", dev.Advances(), $"seq {dev.SeqOrZero}");
        bool ring = Mapped(dev.Ring);
        if (ring) dev.Output(0x02, new byte[47]);
        Check("the output reader still reads its ring after the sweep",
            ring && WaitUntil(() => Volatile.Read(ref raised) == 1, 3000), ring ? $"{raised} raised" : "ring unmapped");
        dev.TearDown();
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
        SharedMemoryIO.Cleanup();
        Check("the sweep leaves a live controller's input section mapped", Mapped(dev.Input));
        uint before = dev.SeqOrZero;
        dev.C.SubmitState(in s);
        Check("SubmitState after the sweep writes the controller's own section", dev.SeqOrZero == before + 2, $"seq {before} to {dev.SeqOrZero}");
        dev.TearDown();
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
        SharedMemoryIO.Cleanup();
        bool mapped = Mapped(dev.Pid);
        Check("the sweep leaves a live controller's PID section mapped", mapped);
        dev.C.PublishPidPool(0xFFFF, 10, false, false);
        uint after = mapped ? dev.PidSeq : 0;
        Check("PublishPidPool after the sweep writes the controller's own PID section", after == before + 2, $"seq {before} to {after}");
        dev.TearDown();
    }

    /// <summary>A UMDF2 protocol host's clock (switch-pro, 15 ms) across the
    /// sweep's release. Report 0x30 goes out on the clock once a subcommand
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
        SharedMemoryIO.Cleanup();
        // Several clock periods before this thread touches anything.
        Thread.Sleep(100);
        Check("the protocol clock keeps writing its section after the sweep", dev.Advances(), $"seq {dev.SeqOrZero}");
        dev.TearDown();
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

    /// <summary>A PID publish that passed its first disposed check and waits
    /// for the PID lock while the controller is torn down. This check holds
    /// the lock, so the publish waits at that point every run.</summary>
    static void OfflinePidDuringDispose(int index)
    {
        Console.WriteLine("\n-- A PID publish in flight during the teardown --");
        using var dev = new OfflineController("dualsense-composite", index, pid: true);
        dev.StopIdleRepeat();
        dev.C.PublishPidPool(0xFFFF, 10, false, false);
        object pidLock = typeof(HMController).GetField("_pidStateLock", AnyInstance)!.GetValue(dev.C)!;
        string outcome = "none";
        Thread publisher, teardown;
        using var tornDown = new ManualResetEventSlim();
        Monitor.Enter(pidLock);
        try
        {
            publisher = new Thread(() =>
            {
                try { dev.C.PublishPidPool(0x00FF, 4, false, false); outcome = "published"; }
                catch (ObjectDisposedException) { outcome = "refused as disposed"; }
            }) { IsBackground = true, Name = "S45 offline PID publish" };
            publisher.Start();
            WaitUntil(() => (publisher.ThreadState & ThreadState.WaitSleepJoin) != 0, 3000);
            Thread.Sleep(50);
            teardown = new Thread(() => { dev.TearDown(); tornDown.Set(); }) { IsBackground = true, Name = "S45 offline teardown" };
            teardown.Start();
            Check("the teardown waits for a PID publish in flight before the section goes",
                !tornDown.Wait(1000));
        }
        finally { Monitor.Exit(pidLock); }
        bool joined = publisher.Join(5000) & teardown.Join(5000);
        Check("the publish either wrote before the teardown or was refused, and the process lives",
            joined && outcome != "none", outcome);
        Check("the teardown unmapped the PID section", !Mapped(dev.Pid));
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

    // â”€â”€ The offline controller â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
        /// it and closes the handle.</summary>
        static IntPtr Plant(string views, string handles, int index, int size)
        {
            IntPtr h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, (uint)size, null);
            if (h == IntPtr.Zero) throw new InvalidOperationException($"CreateFileMappingW failed: {Marshal.GetLastWin32Error()}");
            IntPtr v = MapViewOfFile(h, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
            if (v == IntPtr.Zero) throw new InvalidOperationException($"MapViewOfFile failed: {Marshal.GetLastWin32Error()}");
            Cache(views)[index] = v;
            Cache(handles)[index] = h;
            return v;
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

    /// <summary>Whether <paramref name="view"/> is committed memory, as a
    /// mapped view is and an unmapped one is not.</summary>
    static bool Mapped(IntPtr view) =>
        VirtualQuery(view, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != UIntPtr.Zero
        && mbi.State == MEM_COMMIT;

    const uint PAGE_READWRITE = 0x04;
    const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    const uint MEM_COMMIT = 0x1000;
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
