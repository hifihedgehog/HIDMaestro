using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-controller shared-memory IPC between the elevated user-mode SDK
/// process and the kernel-adjacent UMDF2 driver / XUSB companion. Owns the
/// pagefile-backed Global\HIDMaestroInput&lt;N&gt; (input) and Global\HIDMaestroOutput&lt;N&gt;
/// (output passthrough) named sections, plus the lock-free seqlock writer
/// for input frames.
///
/// <para>The class is currently <c>static</c> because both the SDK and the
/// in-tree test app go through the same instance: there's only ever one
/// per process. When we want to support multiple <see cref="HMContext"/>
/// instances per process the static state will move onto an instance,
/// but for now this matches how the test app's IPC code worked and lets
/// us extract without churning the test app's call sites.</para>
///
/// <para><b>SDDL note:</b> WUDFHost runs as LocalService and lacks
/// SeCreateGlobalPrivilege, so the kernel-side driver/companion CANNOT
/// create Global\ named sections themselves. The test app / SDK
/// (running elevated) pre-creates the sections with a permissive SDDL
/// that grants LocalService full access, then the driver/companion
/// OpenFileMappings them.</para>
/// </summary>
internal static partial class SharedMemoryIO
{
    // ── Layout constants: match driver/driver.h ──────────────────────────
    //
    // HIDMAESTRO_SHARED_INPUT (362 bytes, was 278 pre-v1.3.5):
    //   ULONG  SeqNo                offset 0
    //   ULONG  DataSize             offset 4
    //   UCHAR  Data[256]            offset 8     ← legacy report data, RID stripped
    //   UCHAR  GipData[14]          offset 264
    //   ULONG  ExtendedReportSize   offset 278   ← v1.3.5: 0 = legacy, >0 = use ExtendedReportData
    //   UCHAR  ExtendedReportData[80] offset 282 ← v1.3.5: full RID-included report (Sony BT 0x31 / 0x11)
    //
    // The driver writes EITHER legacy OR extended per frame (mode-switch),
    // not both. SDK clears ExtendedReportSize on legacy frames so the driver
    // doesn't reuse stale extended bytes from a prior arming.
    //
    // HIDMAESTRO_SHARED_OUTPUT: RING BUFFER as of v1.1.40.
    //   ULONG  Head                        offset 0   (monotonic; total writes by driver)
    //   ULONG  _Reserved                   offset 4
    //   HIDMAESTRO_OUTPUT_SLOT Slots[N]    offset 8
    //
    // Each slot (264 bytes):
    //   ULONG  SeqNo                  slot+0    (== Head value at the time of write)
    //   UCHAR  Source                 slot+4
    //   UCHAR  ReportId               slot+5
    //   USHORT DataSize               slot+6
    //   UCHAR  Data[256]              slot+8
    //
    // Pre-1.1.40 was a single slot, latest-write-wins. That coalesced
    // pid.dll's PID FFB write bursts (Set Effect → Set Constant Force →
    // Effect Operation Start, all within 1-3 ms) vs the SDK's 8 ms
    // poll interval. Middle packet (the magnitude) dropped, which is
    // why FFB forces never reached physical devices despite CreateEffect
    // succeeding. v1.1.40 ring with N=64 slots gives ~512 ms of reader
    // lag tolerance before oldest packets get overwritten: comfortable
    // headroom for a 125 Hz consumer vs ~3 ms write bursts.
    //
    // Data[] widened from 64→256 bytes 2026-04-23: DualSense BT report 0x31
    // is 78 bytes; Switch Pro standard input report can run to ~64.

    public const int DATA_OFFSET                = 8;
    public const int DATA_CAPACITY              = 256;
    public const int GIP_DATA_OFFSET            = DATA_OFFSET + DATA_CAPACITY;   // 264
    public const int GIP_DATA_LENGTH            = 14;
    // v1.3.5: vendor-blob extended report (mode-switch path).
    public const int EXTENDED_SIZE_OFFSET       = GIP_DATA_OFFSET + GIP_DATA_LENGTH; // 278
    public const int EXTENDED_DATA_OFFSET       = EXTENDED_SIZE_OFFSET + 4;          // 282
    public const int EXTENDED_DATA_CAPACITY     = 80;
    public const int SHARED_INPUT_SIZE          = EXTENDED_DATA_OFFSET + EXTENDED_DATA_CAPACITY; // 362

    // Output ring layout (must match driver/driver.h):
    public const int OUTPUT_RING_SLOTS  = 64;
    public const int OUTPUT_SLOT_SIZE   = 4 + 1 + 1 + 2 + 256;     // SeqNo + Source + RID + DataSize + Data[256]
    public const int OUTPUT_HEADER_SIZE = 4 + 4;                   // Head + _Reserved
    public const int SHARED_OUTPUT_SIZE = OUTPUT_HEADER_SIZE + OUTPUT_RING_SLOTS * OUTPUT_SLOT_SIZE;
    public const int OUTPUT_SLOT_OFFSET_SEQNO     = 0;
    public const int OUTPUT_SLOT_OFFSET_SOURCE    = 4;
    public const int OUTPUT_SLOT_OFFSET_REPORT_ID = 5;
    public const int OUTPUT_SLOT_OFFSET_SIZE      = 6;
    public const int OUTPUT_SLOT_OFFSET_DATA      = 8;

    // HIDMAESTRO_SHARED_PID_STATE: match driver/driver.h:
    //   ULONG   SeqNo                       offset  0   (seqlock)
    //   UCHAR   PidEnabled                  offset  4   (gate; 0 = no FFB)
    //   UCHAR   _pad0[3]                    offset  5
    //   UCHAR   BL_EffectBlockIndex         offset  8
    //   UCHAR   BL_LoadStatus               offset  9
    //   USHORT  BL_RAMPoolAvailable         offset 10
    //   USHORT  Pool_RAMPoolSize            offset 12
    //   UCHAR   Pool_MaxSimultaneousEffects offset 14
    //   UCHAR   Pool_MemoryManagement       offset 15
    //   UCHAR   State_EffectBlockIndex      offset 16
    //   UCHAR   State_Flags                 offset 17
    //   UCHAR   _pad1[2]                    offset 18
    //   ULONG   EbiAllocBitmap              offset 20  (v1.1.37)
    //   ULONG   EbiAllocatedCount           offset 24  (v1.1.37)
    //   UCHAR   PoolReportId                offset 28  (v1.3.7: 0=canonical 0x13)
    //   UCHAR   StateReportId               offset 29  (v1.3.7: 0=canonical 0x14)
    //   UCHAR   BlockLoadReportId           offset 30  (v1.3.7: 0=canonical 0x12)
    //   UCHAR   CreateNewEffectReportId     offset 31  (v1.3.7: 0=canonical 0x11)
    //   UCHAR   BlockFreeReportId           offset 32  (v1.3.7: 0=canonical 0x1B)
    //   UCHAR   DeviceControlReportId       offset 33  (v1.3.7: 0=canonical 0x1C)
    //   UCHAR   _pad2[2]                    offset 34
    //   total: 36 bytes (rounded to 40 for 8-byte alignment)
    public const int PID_STATE_SIZE                  = 40;
    public const int PID_OFFSET_SEQNO                = 0;
    public const int PID_OFFSET_ENABLED              = 4;
    public const int PID_OFFSET_BL_EBI               = 8;
    public const int PID_OFFSET_BL_LOADSTATUS        = 9;
    public const int PID_OFFSET_BL_RAMAVAIL          = 10;
    public const int PID_OFFSET_POOL_RAMSIZE         = 12;
    public const int PID_OFFSET_POOL_MAXSIM          = 14;
    public const int PID_OFFSET_POOL_MEMMGMT         = 15;
    public const int PID_OFFSET_STATE_EBI            = 16;
    public const int PID_OFFSET_STATE_FLAGS          = 17;
    public const int PID_OFFSET_EBI_BITMAP           = 20;
    public const int PID_OFFSET_EBI_COUNT            = 24;
    public const int PID_OFFSET_POOL_RID             = 28;
    public const int PID_OFFSET_STATE_RID            = 29;
    public const int PID_OFFSET_BL_RID               = 30;
    public const int PID_OFFSET_NEW_EFFECT_RID       = 31;
    public const int PID_OFFSET_BLOCK_FREE_RID       = 32;
    public const int PID_OFFSET_DEVICE_CONTROL_RID   = 33;

    // SDDL granting Local System, Builtin Admins, and LocalService full
    // access plus World read. LocalService is what WUDFHost runs as for
    // UMDF2, so the driver/companion need full access to read input and
    // write output. World read is for non-elevated diagnostic tools. The
    // owner is set to Administrators (O:BA), so the object's owner is
    // Administrators whatever the machine's "default owner for objects
    // created by members of the Administrators group" policy says, and an
    // owner check tells this object from one this release did not create.
    private const string Sddl =
        "O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GR;;;WD)";

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ  = 0x02;
    private const uint FILE_MAP_WRITE = 0x04;

    // CreateEventW EVENT_MODIFY_STATE | SYNCHRONIZE: open existing named
    // events for SetEvent and waiting from the SDK side.
    private const uint EVENT_MODIFY_STATE        = 0x0002;
    private const uint SYNCHRONIZE               = 0x00100000;

    private static readonly Dictionary<int, IntPtr> s_inputHandles  = new();
    private static readonly Dictionary<int, IntPtr> s_inputViews    = new();
    private static readonly Dictionary<int, IntPtr> s_inputEvents   = new();
    private static readonly Dictionary<int, IntPtr> s_companionInputEvents = new();
    // Issue #67: delivery acknowledgement, guarded by the s_inputViews lock.
    private static readonly Dictionary<int, IntPtr> s_inputAckHandles = new();
    private static readonly Dictionary<int, IntPtr> s_inputAckViews   = new();
    private static readonly Dictionary<int, IntPtr> s_inputAckEvents  = new();
    // Issue #67: the server ends of each controller's owner pipe, guarded by
    // the s_inputViews lock.
    private static readonly Dictionary<int, List<IntPtr>> s_ownerPipes = new();
    // v2.0.0: the version handshake, guarded by the s_inputViews lock.
    private static readonly Dictionary<int, IntPtr> s_versionHandles = new();
    private static readonly Dictionary<int, IntPtr> s_versionViews = new();

    // HIDMAESTRO_VERSION_INFO (driver/driver.h), 64 bytes.
    public const int VERSION_INFO_SIZE = 64;
    public const int VERSION_OFFSET_LAYOUT = 0;
    public const int VERSION_OFFSET_SDK_PROTOCOL_MIN = 4;
    public const int VERSION_OFFSET_SDK_PROTOCOL_MAX = 8;
    public const int VERSION_OFFSET_SDK_VERSION = 12;
    public const int VERSION_OFFSET_DRIVER_PROTOCOL = 20;
    public const int VERSION_OFFSET_DRIVER_VERSION = 24;
    public const int VERSION_OFFSET_DRIVER_CAPS = 32;
    // v2.0.0: the reserved bytes from 36 (the design note's decision (c)).
    public const int VERSION_OFFSET_DRIVER_PROTOCOL_MIN = 36;
    public const int VERSION_OFFSET_COMPANION_PROTOCOL = 40;
    public const int VERSION_OFFSET_COMPANION_PROTOCOL_MIN = 44;
    public const int VERSION_OFFSET_IDENTITY_TAG = 48;
    public const int VERSION_OFFSET_DRIVER_CONFIG_ERROR = 56;
    public const int VERSION_OFFSET_COMPANION_CONFIG_ERROR = 60;
    public const int VERSION_LAYOUT = 1;
    /// <summary>Protocol 2 is the v1.12.0 layout set, InputAck and the
    /// owner pipe included. Protocol 3 adds the version handshake, and
    /// protocol 4 the version contract: the identity tag and the
    /// identity entry, under which a driver reads a controller's v2.0.0
    /// values. This SDK runs on protocol 4 alone.</summary>
    public const int SDK_PROTOCOL_MIN = 4, SDK_PROTOCOL = 4;

    /// <summary>The driver's version and protocol from the handshake, or
    /// null until a driver of protocol 3 or later has written them. A
    /// v1.12.0 driver never does, and a USB/IP controller has no driver.</summary>
    public static (Version Version, int Protocol)? DriverVersion(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (!s_versionViews.TryGetValue(controllerIndex, out IntPtr v) || v == IntPtr.Zero) return null;
            int protocol = Marshal.ReadInt32(v, VERSION_OFFSET_DRIVER_PROTOCOL);
            if (protocol == 0) return null;
            Thread.MemoryBarrier();
            var version = new Version(
                (ushort)Marshal.ReadInt16(v, VERSION_OFFSET_DRIVER_VERSION + 0),
                (ushort)Marshal.ReadInt16(v, VERSION_OFFSET_DRIVER_VERSION + 2),
                (ushort)Marshal.ReadInt16(v, VERSION_OFFSET_DRIVER_VERSION + 4));
            return (version, protocol);
        }
    }

    /// <summary>v2.0.0: the driver's half of the version section: the
    /// protocols it serves, from <c>DriverProtocolMin</c> to
    /// <c>DriverProtocol</c>, its version, and its error word, which names
    /// the value it could not read when it failed a start
    /// (<c>HIDMAESTRO_CONFIG_ERROR_*</c> in driver.h). All zero, and the
    /// version null, until a driver writes it, as a driver older than 2.0.0
    /// never does.</summary>
    public static (int Protocol, int ProtocolMin, Version? Version, int ConfigError) DriverHalf(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (!s_versionViews.TryGetValue(controllerIndex, out IntPtr v) || v == IntPtr.Zero) return (0, 0, null, 0);
            int error = Marshal.ReadInt32(v, VERSION_OFFSET_DRIVER_CONFIG_ERROR);
            int protocol = Marshal.ReadInt32(v, VERSION_OFFSET_DRIVER_PROTOCOL);
            if (protocol == 0) return (0, 0, null, error);
            Thread.MemoryBarrier();
            int min = Marshal.ReadInt32(v, VERSION_OFFSET_DRIVER_PROTOCOL_MIN);
            var version = new Version(
                (ushort)Marshal.ReadInt16(v, VERSION_OFFSET_DRIVER_VERSION + 0),
                (ushort)Marshal.ReadInt16(v, VERSION_OFFSET_DRIVER_VERSION + 2),
                (ushort)Marshal.ReadInt16(v, VERSION_OFFSET_DRIVER_VERSION + 4));
            return (protocol, min, version, error);
        }
    }

    /// <summary>v2.0.0: writes the controller's identity tag into its
    /// version section, which <see cref="EnsureInputMapping"/> made. A
    /// driver reads the controller's v2.0.0 values only when the tag is
    /// its own devnode's, so this runs before any PnP work.</summary>
    public static void WriteIdentityTag(int controllerIndex, ulong tag)
    {
        lock (s_inputViews)
        {
            if (!s_versionViews.TryGetValue(controllerIndex, out IntPtr v) || v == IntPtr.Zero)
                throw new InvalidOperationException($"Controller {controllerIndex} has no version section to write its identity tag into.");
            Marshal.WriteInt64(v, VERSION_OFFSET_IDENTITY_TAG, unchecked((long)tag));
        }
    }

    // HIDMAESTRO_INPUT_ACK (driver/driver.h): DeliveredSeqNo at 0, the
    // driver's capability bits at 4, then the release frames: a seqlock
    // word, a count, and HIDMAESTRO_RELEASE_SLOTS frames of a 4-byte size
    // and 80 bytes of report.
    public const int INPUT_ACK_OFFSET_DELIVERED = 0;
    public const int INPUT_ACK_OFFSET_CAPS = 4;
    public const int INPUT_ACK_OFFSET_RELEASE_SEQ = 8;
    public const int INPUT_ACK_OFFSET_RELEASE_COUNT = 12;
    public const int INPUT_ACK_OFFSET_RELEASE = 16;
    public const int RELEASE_SLOTS = 8;
    public const int RELEASE_FRAME_CAP = 80;
    public const int RELEASE_SLOT_SIZE = 4 + RELEASE_FRAME_CAP;
    public const int INPUT_ACK_SIZE = INPUT_ACK_OFFSET_RELEASE + RELEASE_SLOTS * RELEASE_SLOT_SIZE;
    public const int INPUT_ACK_CAP_DELIVERED = 0x1;
    /// <summary>The driver watches this controller's owner pipe and sends
    /// the release frames when it closes.</summary>
    public const int INPUT_ACK_CAP_RELEASE = 0x2;
    /// <summary>Bit 31 of the capability word is the driver's own: it
    /// marks an owner watch that has ended, and the zeroing at create clears
    /// it. An SDK never sets it and tests the capabilities one bit at a
    /// time. The census reads it (OwnerCensus.LegacyPipeLive).</summary>
    public const int INPUT_ACK_WATCH_ENDED = unchecked((int)0x80000000);
    // Two instances, so a driver that restarts under a live controller can
    // connect again: the SDK never re-arms an instance a client left.
    private const int OWNER_PIPE_INSTANCES = 2;
    private static readonly Dictionary<int, IntPtr> s_outputHandles = new();
    private static readonly Dictionary<int, IntPtr> s_outputViews   = new();
    private static readonly Dictionary<int, IntPtr> s_outputEvents  = new();
    // Issue #67: where each controller's output reader starts, guarded by
    // the s_outputViews lock. See MarkOutputStart.
    private static readonly Dictionary<int, uint> s_outputStartSeq = new();
    private static readonly Dictionary<int, IntPtr> s_pidStateHandles = new();
    private static readonly Dictionary<int, IntPtr> s_pidStateViews   = new();

    /// <summary>The indexes whose objects a live controller in this process
    /// holds: from its constructor, before it maps them, until
    /// <see cref="DestroyController"/>, which its teardown calls once its
    /// threads have stopped.
    ///
    /// <para>A controller caches its views and events and uses them from
    /// threads it alone stops: the output reader, the idle repeat's clock,
    /// a protocol host's clock, and the consumer's own submits. The device
    /// sweep ends in <see cref="Cleanup"/> while such a controller can still
    /// be alive, and a view unmapped under any of those threads is an access
    /// violation that takes the process down (issue #45 for the output
    /// reader, then the idle repeat of a USB/IP DualSense). So the sweep
    /// leaves a held index alone, as it leaves another program's sections,
    /// and the controller's own Dispose releases them.</para></summary>
    private static readonly HashSet<int> s_held = new();

    /// <summary>Marks the index's objects as a live controller's, so
    /// <see cref="Cleanup"/> leaves them. Called by the controller's
    /// constructor before it maps them. <see cref="DestroyController"/> ends
    /// the hold.</summary>
    public static void Hold(int controllerIndex)
    {
        lock (s_held) s_held.Add(controllerIndex);
    }

    private static bool IsHeld(int controllerIndex)
    {
        lock (s_held) return s_held.Contains(controllerIndex);
    }

    /// <summary>Live output-poll threads, keyed by controller index (issue
    /// #45).
    ///
    /// <para>A poll thread dereferences its output view on every iteration
    /// and is stopped only by its owning <c>HMController.Dispose</c>.
    /// Unmapping that view while the thread runs is an access violation on a
    /// background thread, so <see cref="DestroyController"/> stops the
    /// index's registered thread before it unmaps anything.</para></summary>
    private static readonly Dictionary<int, (CancellationTokenSource Cts, Thread Thread)> s_outputPumps = new();

    /// <summary>Records a controller's output-poll thread so any later unmap
    /// can stop it first. Called once the thread is running.</summary>
    public static void RegisterOutputPump(int controllerIndex, CancellationTokenSource cts, Thread thread)
    {
        lock (s_outputPumps) s_outputPumps[controllerIndex] = (cts, thread);
    }

    /// <summary>Drops a controller's poll-thread registration without
    /// stopping it. For <c>HMController.Dispose</c>, which has already
    /// canceled and joined its own thread.</summary>
    public static void UnregisterOutputPump(int controllerIndex)
    {
        lock (s_outputPumps) s_outputPumps.Remove(controllerIndex);
    }

    /// <summary>Cancels and joins the named controller's poll thread, if one
    /// is registered. Idempotent, and safe to call on an already-stopped
    /// thread.</summary>
    private static void StopOutputPump(int controllerIndex)
    {
        (CancellationTokenSource Cts, Thread Thread) pump;
        lock (s_outputPumps)
        {
            if (!s_outputPumps.TryGetValue(controllerIndex, out pump)) return;
            s_outputPumps.Remove(controllerIndex);
        }
        JoinPump(pump);
    }

    /// <summary>Cancel then join, outside the registry lock.
    ///
    /// <para>The join must not happen under <c>s_outputPumps</c>: the poll
    /// loop calls back into this class, so holding a lock across the join
    /// invites a deadlock with a thread that is trying to acquire it. The
    /// remove-then-join shape above is what keeps that impossible.</para>
    ///
    /// <para>Joining the current thread would deadlock outright, so that
    /// case cancels and returns rather than waiting on itself.</para></summary>
    private static void JoinPump((CancellationTokenSource Cts, Thread Thread) pump)
    {
        try { pump.Cts?.Cancel(); } catch { /* already disposed */ }
        if (pump.Thread == null || !pump.Thread.IsAlive) return;
        if (ReferenceEquals(pump.Thread, Thread.CurrentThread)) return;
        try { pump.Thread.Join(TimeoutScale.Apply(500)); } catch { }
    }

    /// <summary>Every kernel object name one controller's contract uses,
    /// for <see cref="HMController.SectionNames"/>.</summary>
    public static IReadOnlyList<string> ObjectNames(int controllerIndex) => new[]
    {
        $@"Global\HIDMaestroInput{controllerIndex}",
        $@"Global\HIDMaestroInputEvent{controllerIndex}",
        $@"Global\HIDMaestroCompanionInputEvent{controllerIndex}",
        $@"Global\HIDMaestroInputAck{controllerIndex}",
        $@"Global\HIDMaestroInputAckEvent{controllerIndex}",
        $@"Global\HIDMaestroOutput{controllerIndex}",
        $@"Global\HIDMaestroOutputEvent{controllerIndex}",
        $@"Global\HIDMaestroPidState{controllerIndex}",
        $@"Global\HIDMaestroVersion{controllerIndex}",
        $@"\\.\pipe\HIDMaestroOwner{controllerIndex}",
    };

    /// <summary>Returns the view pointer for the controller's INPUT section,
    /// creating the section on first call. Thread-safe via per-call lock
    /// callers can issue concurrent EnsureInputMapping requests for distinct
    /// controllerIndex values without races. Idempotent for the same index.</summary>
    public static IntPtr EnsureInputMapping(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            string name = $@"Global\HIDMaestroInput{controllerIndex}";
            IntPtr h = IntPtr.Zero, view = IntPtr.Zero;
            try
            {
                // Issue #67: the owner pipe. The driver connects and keeps a
                // read pending that never gets data. Windows closes these ends
                // when this process ends, however it ends, and the read
                // completes, which is how the driver learns to let go of the
                // keys and buttons this process held. Best effort: without it
                // the device keeps its last report until it is removed.
                //
                // The first instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE.
                // A v1.12.0 program holds this name for the index it uses and
                // takes no claim, so failing here means one started on this index
                // after the claim was checked. Both would write these sections,
                // so the create stops instead, and before it writes anything:
                // a section of this name that exists can be that program's,
                // and the zeroing below would wipe its frame, its
                // acknowledgement and its release frames.
                var pipes = new List<IntPtr>();
                for (int k = 0; k < OWNER_PIPE_INSTANCES; k++)
                {
                    IntPtr ph = CreateOwnerPipe($@"\\.\pipe\HIDMaestroOwner{controllerIndex}", first: k == 0);
                    if (ph != INVALID_HANDLE_VALUE) { pipes.Add(ph); continue; }
                    if (k == 0 && Marshal.GetLastPInvokeError() is ERROR_ACCESS_DENIED or ERROR_PIPE_BUSY)
                    {
                        // The catch below releases what this call made.
                        throw new HMResourceInUseException(
                            $"Transport index {controllerIndex} is in use by a running program built on HIDMaestro 1.12.0, which started on it during this create. Create the controller again.",
                            $"transport index {controllerIndex}", null);
                    }
                }
                s_ownerPipes[controllerIndex] = pipes;

                (h, view) = CreateSection(name, SHARED_INPUT_SIZE);

                // Zero-init: pagefile-backed sections start zero already, but we
                // explicitly write SeqNo=0 so a stale section from a previous
                // run (under the same name) doesn't carry forward a non-zero
                // sequence number that the driver would treat as "no change".
                // Bulk Marshal.Copy from a freshly allocated zero buffer is one
                // P/Invoke. The prior per-byte loop was 278 P/Invokes per setup.
                Marshal.Copy(new byte[SHARED_INPUT_SIZE], 0, view, SHARED_INPUT_SIZE);

                // Companion input doorbell (perf audit 2026-07-21). The XUSB
                // companion's WAIT_FOR_INPUT pump was purely 8 ms
                // timer-quantized, which put a 0-8 ms (median ~4 ms) phase
                // delay on the WGI/GameInput input path and 125 idle wakes/s
                // per Xbox controller. A SECOND named auto-reset event (the
                // main input event is consumed by the HID driver's worker,
                // an auto-reset event cannot serve two waiters) lets the
                // companion complete a parked WAIT_FOR_INPUT at frame
                // arrival. Old companions never open it: timer fallback.
                string cevName = $@"Global\HIDMaestroCompanionInputEvent{controllerIndex}";
                IntPtr cev = CreateNamedEvent(cevName);
                s_companionInputEvents[controllerIndex] = cev;

                // Issue #67: the driver stores each delivered frame's SeqNo here
                // and signals the event, so keyboard and mouse writers can send
                // every frame instead of the latest one. A separate section, so
                // the input layout native writers depend on is unchanged. The
                // driver opens both alongside the input section, since it
                // cannot create Global\ objects itself, and sets the capability
                // word. An older driver leaves the word zero and nobody waits.
                (IntPtr ah, IntPtr av) = CreateSection($@"Global\HIDMaestroInputAck{controllerIndex}", INPUT_ACK_SIZE);
                Marshal.Copy(new byte[INPUT_ACK_SIZE], 0, av, INPUT_ACK_SIZE);
                s_inputAckHandles[controllerIndex] = ah;
                s_inputAckViews[controllerIndex] = av;
                s_inputAckEvents[controllerIndex] = CreateNamedEvent($@"Global\HIDMaestroInputAckEvent{controllerIndex}");

                // v2.0.0: the version handshake (HIDMAESTRO_VERSION_INFO in
                // driver.h). This SDK's half goes in now. The driver writes its
                // half when it opens the input section, so the section must
                // exist before the event below, like the acknowledgement.
                (IntPtr vh, IntPtr vv) = CreateSection($@"Global\HIDMaestroVersion{controllerIndex}", VERSION_INFO_SIZE);
                Marshal.Copy(new byte[VERSION_INFO_SIZE], 0, vv, VERSION_INFO_SIZE);
                Marshal.WriteInt32(vv, VERSION_OFFSET_LAYOUT, VERSION_LAYOUT);
                Marshal.WriteInt32(vv, VERSION_OFFSET_SDK_PROTOCOL_MIN, SDK_PROTOCOL_MIN);
                Marshal.WriteInt32(vv, VERSION_OFFSET_SDK_PROTOCOL_MAX, SDK_PROTOCOL);
                var sdk = typeof(SharedMemoryIO).Assembly.GetName().Version ?? new Version(0, 0, 0);
                Marshal.WriteInt16(vv, VERSION_OFFSET_SDK_VERSION + 0, (short)sdk.Major);
                Marshal.WriteInt16(vv, VERSION_OFFSET_SDK_VERSION + 2, (short)sdk.Minor);
                Marshal.WriteInt16(vv, VERSION_OFFSET_SDK_VERSION + 4, (short)Math.Max(0, sdk.Build));
                s_versionHandles[controllerIndex] = vh;
                s_versionViews[controllerIndex] = vv;

                // Driver signaling event. Auto-reset (manual_reset=FALSE), not
                // initially set. The driver's per-device worker thread waits on
                // this with a 50ms safety timeout to replace the old 1ms busy poll.
                // SDDL matches the section so WUDFHost (LocalService) can open it.
                // Created last: the worker maps the input section and the
                // acknowledgement once, when this event first opens, so both
                // must exist by then.
                string evName = $@"Global\HIDMaestroInputEvent{controllerIndex}";
                IntPtr ev = CreateNamedEvent(evName);

                s_inputHandles[controllerIndex] = h;
                s_inputViews[controllerIndex] = view;
                s_inputEvents[controllerIndex] = ev;
                return view;
            }
            catch
            {
                // Everything this call made goes before the exception
                // leaves it: nothing is cached for the index until the last
                // step succeeds, so no later DestroyController would find
                // the input section, and the other objects would keep the
                // index's names alive in this process.
                ReleaseInputExtrasLocked(controllerIndex);
                if (view != IntPtr.Zero) UnmapViewOfFile(view);
                if (h != IntPtr.Zero) CloseHandle(h);
                throw;
            }
        }
    }

    /// <summary>Try to open the driver-created output-ring doorbell
    /// <c>Global\HIDMaestroOutputEvent&lt;N&gt;</c> (issue #34). The DRIVER
    /// creates this event (companion opens-or-creates the same name), so
    /// open success doubles as capability detection: success means the
    /// installed driver signals per published packet and the reader can
    /// block on it; failure means an older driver and the caller keeps
    /// the 8 ms poll cadence. Returns IntPtr.Zero when absent. The caller
    /// owns the returned handle.
    ///
    /// <para>Any program can create a <c>Global\</c> event. The driver
    /// uses an existing one only when a driver host or an elevated program
    /// made it under a DACL, or a driver host made it under the NULL DACL a
    /// driver before v2.0.0 gave it (driver.c CreateHostEvent). The driver
    /// never signals one that fails the same test, so the reader keeps the
    /// poll cadence instead of waiting on it.</para></summary>
    public static IntPtr TryOpenOutputEvent(int controllerIndex)
    {
        IntPtr ev = OpenEventW(SYNCHRONIZE | READ_CONTROL, false,
            $@"Global\HIDMaestroOutputEvent{controllerIndex}");
        if (ev == IntPtr.Zero) return ev;
        using (var h = new Microsoft.Win32.SafeHandles.SafeWaitHandle(ev, ownsHandle: false))
            if (ObjectOwner.HasDacl(h) ? ObjectOwner.IsOwnedByAdmin(h, allowLocalService: true) : ObjectOwner.IsOwnedByService(h))
                return ev;
        CloseHandle(ev);
        return IntPtr.Zero;
    }

    /// <summary>Companion input doorbell handle for
    /// <c>Global\HIDMaestroCompanionInputEvent&lt;N&gt;</c> (created in
    /// EnsureInputMapping). IntPtr.Zero when the mapping was never
    /// created in this process.</summary>
    public static IntPtr GetCompanionInputEvent(int controllerIndex)
    {
        lock (s_inputViews)
            return s_companionInputEvents.TryGetValue(controllerIndex, out var ev) ? ev : IntPtr.Zero;
    }

    /// <summary>Create (or return) the output-ring doorbell
    /// <c>Global\HIDMaestroOutputEvent&lt;N&gt;</c> from the SDK side. In
    /// UMDF2 mode the DRIVER creates this event; the USB/IP backend
    /// (issue #39) has no driver, so its in-process device emulator
    /// creates it here, with the same SDDL, BEFORE the HMController's
    /// OutputPollLoop calls <see cref="TryOpenOutputEvent"/>, so the
    /// reader comes up in event mode exactly as it does against the
    /// driver. Cached per index and closed by DestroyController.</summary>
    public static IntPtr EnsureOutputEvent(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputEvents.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;
            IntPtr ev = CreateNamedEvent($@"Global\HIDMaestroOutputEvent{controllerIndex}");
            s_outputEvents[controllerIndex] = ev;
            return ev;
        }
    }

    /// <summary>Open an independent SYNCHRONIZE handle to
    /// <c>Global\HIDMaestroInputEvent&lt;N&gt;</c> for a waiter. The handle
    /// EnsureInputMapping caches is the writer's SetEvent handle; the
    /// USB/IP device emulator's input pump is the auto-reset event's sole
    /// waiter (the role the UMDF2 driver's worker plays otherwise) and
    /// owns the returned handle.</summary>
    public static IntPtr OpenInputEventForWait(int controllerIndex)
    {
        return OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, false,
            $@"Global\HIDMaestroInputEvent{controllerIndex}");
    }

    /// <summary>Returns the view pointer for the controller's OUTPUT section,
    /// creating the section on first call. The driver/companion attach to
    /// this section read-write to publish captured rumble/haptics/FFB/LED.</summary>
    public static IntPtr EnsureOutputMapping(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            string name = $@"Global\HIDMaestroOutput{controllerIndex}";
            (IntPtr h, IntPtr view) = CreateSection(name, SHARED_OUTPUT_SIZE);

            // Zero-init: pagefile sections start zero, BUT a residual kernel
            // object from a prior SDK process (kept alive by a still-loaded
            // driver view) may retain a non-zero SeqNo and stale Data. Without
            // this, a fresh OutputPollLoop sees `SeqNo != lastSeq(0)` on its
            // first sample and replays prior-session FFB as a brand-new
            // OutputReceived packet: on repeat if a consumer process's
            // XInput/HID handle still talks to the ghost slot.
            // Bulk Marshal.Copy from a freshly allocated zero buffer is one
            // P/Invoke; the prior per-byte loop was ~17K P/Invokes per setup
            // (SHARED_OUTPUT_SIZE = 16,904 bytes).
            Marshal.Copy(new byte[SHARED_OUTPUT_SIZE], 0, view, SHARED_OUTPUT_SIZE);

            s_outputHandles[controllerIndex] = h;
            s_outputViews[controllerIndex] = view;
            return view;
        }
    }

    /// <summary>Record the output ring's Head as the point this controller's
    /// reader starts from. Called once per create, after the section exists
    /// and before the device does (issue #67). A host writes to a device as
    /// soon as it starts, which is before the controller object and its
    /// reader exist: Windows sends a new keyboard its LED state then, and a
    /// Sony Bluetooth host reads the feature reports that arm the extended
    /// report. A reader that started at the Head it found skipped those.
    /// Anything an earlier controller at this index left stays behind the
    /// mark.</summary>
    public static void MarkOutputStart(int controllerIndex)
    {
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr view) && view != IntPtr.Zero)
                s_outputStartSeq[controllerIndex] = (uint)Marshal.ReadInt32(view, 0);
        }
    }

    /// <summary>The mark <see cref="MarkOutputStart"/> left for this index,
    /// removed as it is read, or null when there is none.</summary>
    public static uint? TakeOutputStart(int controllerIndex)
    {
        lock (s_outputViews)
            return s_outputStartSeq.Remove(controllerIndex, out uint seq) ? seq : null;
    }

    /// <summary>Returns the view pointer for the controller's PID FFB
    /// state section, creating the section on first call. The driver
    /// attaches read-only and reads on every IOCTL_UMDF_HID_GET_FEATURE.
    /// Lazy: a non-FFB consumer that never publishes PID state never
    /// triggers this: the section simply doesn't exist and the driver
    /// falls back to STATUS_NO_SUCH_DEVICE / STATUS_NOT_SUPPORTED for
    /// any PID GetFeature, matching pre-FFB behavior.</summary>
    public static IntPtr EnsurePidStateMapping(int controllerIndex)
    {
        lock (s_pidStateViews)
        {
            if (s_pidStateViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            string name = $@"Global\HIDMaestroPidState{controllerIndex}";
            (IntPtr h, IntPtr view) = CreateSection(name, PID_STATE_SIZE);

            // v1.1.38: initialize with vJoy-compatible defaults rather
            // than zeros. vJoy's Ffb_ResetPIDData
            // (vJoy-Brunner/driver/sys/hid.c:2627) sets RAMPoolSize=200,
            // MaxSimultaneousEffects=10, MemoryManagement=0 unconditionally
            // at device-add. dinput8 reads Pool defaults during enumeration
            // (before any consumer has called PublishPidPool); zero
            // RAMPoolSize / MaxSim could lead dinput into degenerate
            // branches (divide-by-zero, no-effects path).
            //
            // PidEnabled stays 0: the consumer still has to PublishPidPool
            // to flip that, at which point GetFeature(0x13 Pool) starts
            // returning the consumer's values (overwriting these defaults).
            // Until then, GetFeature(0x13) returns STATUS_NO_SUCH_DEVICE
            // per the vJoy "FFB not enabled" convention, but if a path
            // ever bypasses that gate, sane bytes are present.
            // PID_STATE_SIZE is small (24 bytes) but bulk-zero is still
            // a single P/Invoke vs PID_STATE_SIZE separate WriteBytes.
            Marshal.Copy(new byte[PID_STATE_SIZE], 0, view, PID_STATE_SIZE);
            Marshal.WriteInt16(view, PID_OFFSET_POOL_RAMSIZE, 200);
            Marshal.WriteByte (view, PID_OFFSET_POOL_MAXSIM, 10);
            // MemoryManagement stays 0 (host-managed pool, no shared param blocks).

            s_pidStateHandles[controllerIndex] = h;
            s_pidStateViews[controllerIndex] = view;
            return view;
        }
    }

    /// <summary>Atomic seqlock write of the full PID state struct. The
    /// caller has already mutated the relevant fields; this method
    /// publishes the new SeqNo so the driver's seqlocked read picks up
    /// a consistent snapshot. Single-writer (the SDK consumer) →
    /// single-reader (the driver per-IOCTL); seqlock retry handles the
    /// (rare) reader-mid-write window.</summary>
    private static void PidStateBeginWrite(IntPtr view, ref uint seqNo)
    {
        // Mark write-in-progress (odd seqNo)
        uint pending = seqNo + 1;
        Marshal.WriteInt32(view, PID_OFFSET_SEQNO, (int)pending);
        Thread.MemoryBarrier();
    }

    private static void PidStateEndWrite(IntPtr view, ref uint seqNo)
    {
        // Mark write-complete (even seqNo)
        Thread.MemoryBarrier();
        seqNo += 2;
        Marshal.WriteInt32(view, PID_OFFSET_SEQNO, (int)seqNo);
    }

    /// <summary>Publish PID Pool Report fields and flip PidEnabled to 1.
    /// First call enables FFB on this controller; subsequent calls update
    /// the pool state.</summary>
    public static void WritePidPool(IntPtr view, ref uint seqNo,
                                    ushort ramPoolSize, byte maxSimultaneousEffects,
                                    bool deviceManagedPool, bool sharedParameterBlocks)
    {
        PidStateBeginWrite(view, ref seqNo);
        Marshal.WriteByte(view,  PID_OFFSET_ENABLED,           1);
        Marshal.WriteInt16(view, PID_OFFSET_POOL_RAMSIZE,      (short)ramPoolSize);
        Marshal.WriteByte(view,  PID_OFFSET_POOL_MAXSIM,       maxSimultaneousEffects);
        byte memMgmt = (byte)((deviceManagedPool ? 0x01 : 0x00) |
                              (sharedParameterBlocks ? 0x02 : 0x00));
        Marshal.WriteByte(view,  PID_OFFSET_POOL_MEMMGMT,      memMgmt);
        PidStateEndWrite(view, ref seqNo);
    }

    /// <summary>Publish PID Block Load Report fields. Single-slot
    /// last-write-wins per HID PID 1.0 §5.5: call from the
    /// OutputReceived handler that received the Create New Effect
    /// SetFeature, before returning, so the host's matching
    /// GetFeature(BlockLoad) reads this snapshot.</summary>
    public static void WritePidBlockLoad(IntPtr view, ref uint seqNo,
                                         byte effectBlockIndex, byte loadStatus,
                                         ushort ramPoolAvailable)
    {
        PidStateBeginWrite(view, ref seqNo);
        Marshal.WriteByte(view,  PID_OFFSET_BL_EBI,        effectBlockIndex);
        Marshal.WriteByte(view,  PID_OFFSET_BL_LOADSTATUS, loadStatus);
        Marshal.WriteInt16(view, PID_OFFSET_BL_RAMAVAIL,   (short)ramPoolAvailable);
        PidStateEndWrite(view, ref seqNo);
    }

    /// <summary>Publish the descriptor-derived PID Report ID overrides.
    /// Called once per controller right after section creation. Driver
    /// reads these from shared state and uses them in
    /// IOCTL_UMDF_HID_GET_FEATURE / IOCTL_UMDF_HID_SET_FEATURE /
    /// IOCTL_UMDF_HID_SET_OUTPUT_REPORT instead of the canonical
    /// 0x11/0x12/0x13/0x14/0x1B/0x1C constants. Profiles whose
    /// descriptor uses the canonical IDs (every builder-emitted
    /// AddPidFfbBlock descriptor) get zero overrides written here, so
    /// the driver's `if (sec-&gt;XxxReportId) ...` guards keep canonical
    /// behavior. Outside the seqlock: written exactly once at
    /// section setup, before any consumer publishes Pool/State/BL.</summary>
    public static void WritePidReportIds(IntPtr view,
        byte poolRid, byte stateRid, byte blockLoadRid,
        byte createNewEffectRid, byte blockFreeRid, byte deviceControlRid)
    {
        Marshal.WriteByte(view, PID_OFFSET_POOL_RID,           poolRid);
        Marshal.WriteByte(view, PID_OFFSET_STATE_RID,          stateRid);
        Marshal.WriteByte(view, PID_OFFSET_BL_RID,             blockLoadRid);
        Marshal.WriteByte(view, PID_OFFSET_NEW_EFFECT_RID,     createNewEffectRid);
        Marshal.WriteByte(view, PID_OFFSET_BLOCK_FREE_RID,     blockFreeRid);
        Marshal.WriteByte(view, PID_OFFSET_DEVICE_CONTROL_RID, deviceControlRid);
    }

    /// <summary>Publish PID State Report fields.</summary>
    public static void WritePidState(IntPtr view, ref uint seqNo,
                                     byte effectBlockIndex, byte stateFlags)
    {
        PidStateBeginWrite(view, ref seqNo);
        Marshal.WriteByte(view, PID_OFFSET_STATE_EBI,   effectBlockIndex);
        Marshal.WriteByte(view, PID_OFFSET_STATE_FLAGS, stateFlags);
        PidStateEndWrite(view, ref seqNo);
    }

    /// <summary>v1.1.37: seqlocked read of the Block Load fields the
    /// driver populates synchronously inside its
    /// IOCTL_UMDF_HID_SET_FEATURE(0x11) handler. Returns the EBI the
    /// driver just assigned, the load status (1=Success, 2=Full,
    /// 3=Error), and the remaining RAM pool. Retries on writer-mid-update
    /// (very brief; the driver writes 4 bytes under SeqNo).</summary>
    public static (byte ebi, byte loadStatus, ushort ramPoolAvailable)
        ReadPidBlockLoad(IntPtr view)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint seq1 = (uint)Marshal.ReadInt32(view, PID_OFFSET_SEQNO);
            if ((seq1 & 1) != 0) { Thread.SpinWait(8); continue; } /* writer mid-update */
            Thread.MemoryBarrier();
            byte ebi   = Marshal.ReadByte(view,  PID_OFFSET_BL_EBI);
            byte stat  = Marshal.ReadByte(view,  PID_OFFSET_BL_LOADSTATUS);
            ushort ram = (ushort)Marshal.ReadInt16(view, PID_OFFSET_BL_RAMAVAIL);
            Thread.MemoryBarrier();
            uint seq2 = (uint)Marshal.ReadInt32(view, PID_OFFSET_SEQNO);
            if (seq1 == seq2) return (ebi, stat, ram);
        }
        // Fall through after retries: return whatever the last sample was.
        return (
            Marshal.ReadByte(view,  PID_OFFSET_BL_EBI),
            Marshal.ReadByte(view,  PID_OFFSET_BL_LOADSTATUS),
            (ushort)Marshal.ReadInt16(view, PID_OFFSET_BL_RAMAVAIL));
    }

    /// <summary>Returns the signaling event handle for a controller's input
    /// section, or <see cref="IntPtr.Zero"/> if no mapping has been created
    /// for that index yet. Used by <see cref="HMController"/> to cache the
    /// handle alongside the view pointer so <see cref="WriteInputFrame"/>
    /// can signal it per frame without a dictionary lookup.</summary>
    public static IntPtr GetInputEvent(int controllerIndex)
    {
        lock (s_inputViews)
        {
            return s_inputEvents.TryGetValue(controllerIndex, out IntPtr ev) ? ev : IntPtr.Zero;
        }
    }

    /// <summary>True once the driver serving this controller has opened the
    /// acknowledgement and announced it stores delivered sequence numbers
    /// (issue #67).</summary>
    public static bool InputAckSupported(int controllerIndex)
    {
        lock (s_inputViews)
        {
            return s_inputAckViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero
                && (Marshal.ReadInt32(v, INPUT_ACK_OFFSET_CAPS) & INPUT_ACK_CAP_DELIVERED) != 0;
        }
    }

    /// <summary>Whether the driver watches this controller's owner pipe and
    /// will send the release frames when this process lets go of it.</summary>
    public static bool ReleaseOnExitSupported(int controllerIndex)
    {
        lock (s_inputViews)
        {
            return s_inputAckViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero
                && (Marshal.ReadInt32(v, INPUT_ACK_OFFSET_CAPS) & INPUT_ACK_CAP_RELEASE) != 0;
        }
    }

    /// <summary>Store the frames the driver sends if this process goes
    /// away with input held: one per input report, each the report that
    /// lets go of everything. A seqlock write. The caller is the
    /// controller's only writer (it holds its submit lock).</summary>
    public static void WriteReleaseFrames(int controllerIndex, IReadOnlyList<byte[]> frames)
    {
        IntPtr view;
        lock (s_inputViews)
        {
            if (!s_inputAckViews.TryGetValue(controllerIndex, out view) || view == IntPtr.Zero) return;
        }
        int seq = Marshal.ReadInt32(view, INPUT_ACK_OFFSET_RELEASE_SEQ);
        if ((seq & 1) == 0) seq++;
        Marshal.WriteInt32(view, INPUT_ACK_OFFSET_RELEASE_SEQ, seq);
        Thread.MemoryBarrier();
        int count = Math.Min(frames.Count, RELEASE_SLOTS);
        for (int i = 0; i < count; i++)
        {
            byte[] f = frames[i];
            int size = Math.Min(f.Length, RELEASE_FRAME_CAP);
            int at = INPUT_ACK_OFFSET_RELEASE + i * RELEASE_SLOT_SIZE;
            Marshal.WriteInt32(view, at, size);
            Marshal.Copy(f, 0, view + at + 4, size);
        }
        Marshal.WriteInt32(view, INPUT_ACK_OFFSET_RELEASE_COUNT, count);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(view, INPUT_ACK_OFFSET_RELEASE_SEQ, seq + 1);
    }

    /// <summary>Wait until a READ_REPORT has carried the frame written with
    /// <paramref name="seqNo"/> (or a later one). False on timeout, or when
    /// the controller has no acknowledgement.</summary>
    public static bool WaitInputDelivered(int controllerIndex, uint seqNo, int timeoutMs)
    {
        IntPtr view, ev;
        lock (s_inputViews)
        {
            if (!s_inputAckViews.TryGetValue(controllerIndex, out view) || view == IntPtr.Zero) return false;
            s_inputAckEvents.TryGetValue(controllerIndex, out ev);
        }
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            uint delivered = (uint)Marshal.ReadInt32(view, INPUT_ACK_OFFSET_DELIVERED);
            if ((int)(delivered - seqNo) >= 0) return true;
            long left = deadline - Environment.TickCount64;
            if (left <= 0) return false;
            if (ev == IntPtr.Zero) { Thread.Sleep(1); continue; }
            WaitForSingleObject(ev, (uint)Math.Min(left, 50));
        }
    }

    /// <summary>Atomic seqlock write of a new input frame. Single-writer
    /// (the SDK consumer's input loop) → many-readers (driver + companion)
    /// pattern is safe lock-free: readers retry on SeqNo mismatch. After
    /// publishing the new sequence, signals <paramref name="eventHandle"/>
    /// so the driver's worker thread can wake immediately instead of
    /// busy-polling the section.
    ///
    /// <para><c>seqNo</c> is updated in place; the caller maintains it
    /// across frames so it survives mapping resets.</para>
    ///
    /// <para><paramref name="eventHandle"/> may be <see cref="IntPtr.Zero"/>
    /// if no signaling event exists (e.g. older driver that still polls
    /// without opening the event). The SetEvent call is skipped in that
    /// case: the write still completes normally.</para></summary>
    public static void WriteInputFrame(IntPtr view, IntPtr eventHandle, ref uint seqNo,
                                       byte[] data, int dataLen, byte[]? gipData,
                                       int dataOffset = 0,
                                       byte[]? extendedData = null, int extendedLen = 0,
                                       IntPtr companionEvent = default)
    {
        // 1. Mark write in progress (odd seqNo)
        uint pending = seqNo + 1;
        Marshal.WriteInt32(view, 0, (int)pending);
        Thread.MemoryBarrier();

        // 2. Write payload (DataSize + Data + GipData + ExtendedReport*).
        // v1.3.0: bulk Marshal.Copy replaces the prior per-byte
        // Marshal.WriteByte loops, which were 256 + 14 = 270 P/Invoke
        // calls per frame. At 250 Hz × 6 controllers that was ~400 000
        // P/Invokes/sec; a single bulk copy per region is two
        // P/Invokes/frame total. We don't zero the unused data tail past
        // dataLen: driver/consumer reads DataSize and uses only
        // data[0..DataSize-1]; the tail is irrelevant. T26-2: gipData
        // is null for non-Xbox profiles (no XUSB companion bound), so we
        // can skip the 14-byte copy entirely. Section is zero-initialized
        // at create time, so the GIP slice stays zeros.
        //
        // v1.3.5: when extendedData != null AND extendedLen > 0, copy
        // the full RID-included extended report into ExtendedReportData
        // and set ExtendedReportSize. Driver branches on
        // ExtendedReportSize > 0 and emits ExtendedReportData verbatim
        // (no FirstInputReportId prepend). When extendedData is null we
        // explicitly clear ExtendedReportSize so a previously-armed
        // controller's stale extended bytes don't leak through after
        // a legacy-mode frame (e.g. if armOn fires were ever rolled back
        //: currently they aren't, but cheap insurance).
        Marshal.WriteInt32(view, 4, dataLen);
        if (dataLen > 0)
            Marshal.Copy(data, dataOffset, view + DATA_OFFSET, dataLen);
        if (gipData != null)
            Marshal.Copy(gipData, 0, view + GIP_DATA_OFFSET, GIP_DATA_LENGTH);
        if (extendedData != null && extendedLen > 0)
        {
            int copyLen = Math.Min(extendedLen, EXTENDED_DATA_CAPACITY);
            Marshal.Copy(extendedData, 0, view + EXTENDED_DATA_OFFSET, copyLen);
            Marshal.WriteInt32(view, EXTENDED_SIZE_OFFSET, copyLen);
        }
        else
        {
            // Mandatory per-frame clear: named sections (Global\HIDMaestroInput<N>)
            // can be opened concurrently by other processes (CreateFileMappingW
            // returns existing handle for any caller passing the same name + SDDL).
            // Across emulate-app restarts the SDK re-zeros at create time, but
            // BETWEEN restarts a foreign writer could have left ExtendedReportSize
            // > 0 + ExtendedReportData populated; with the legacy path running
            // (USB Sony, generic profiles) that stale extended buffer would be
            // emitted by the driver, breaking input. Cost is one P/Invoke per
            // frame.
            Marshal.WriteInt32(view, EXTENDED_SIZE_OFFSET, 0);
        }

        // 3. Mark write complete (even seqNo)
        Thread.MemoryBarrier();
        seqNo = pending + 1;
        Marshal.WriteInt32(view, 0, (int)seqNo);

        // 4. Wake the driver's worker thread. Auto-reset event: one
        // successful wait consumes the signal. If the driver is still
        // processing a previous frame the signal stays latched until
        // it returns to WaitForMultipleObjects.
        if (eventHandle != IntPtr.Zero)
            SetEvent(eventHandle);

        // 5. Companion doorbell (perf audit 2026-07-21): only frames that
        // carry GIP bytes feed the XUSB companion's WAIT_FOR_INPUT pump,
        // so non-Xbox profiles pay nothing here.
        if (companionEvent != IntPtr.Zero && gipData != null)
            SetEvent(companionEvent);
    }

    /// <summary>v1.1.40 ring read. Reads the next output packet at
    /// SeqNo <paramref name="lastSeq"/>+1 if available. Returns true with
    /// the packet bytes if a fresh slot is published; false if nothing
    /// new since the last call. On success <paramref name="lastSeq"/>
    /// advances by 1 and the slot's data is copied into <paramref name="dataBuf"/>.
    ///
    /// Caller pumps this in a loop on each poll iteration so all slots
    /// between the previous LastSeen and current Head get drained: see
    /// <see cref="HMController.OutputPollLoop"/>. Pre-1.1.40 single-slot
    /// channel coalesced PID FFB packet bursts; this ring drains them.</summary>
    public static bool TryReadOutputFrame(
        IntPtr view, ref uint lastSeq,
        out byte source, out byte reportId, out int dataSize, byte[] dataBuf)
    {
        source = 0; reportId = 0; dataSize = 0;

        uint head = (uint)Marshal.ReadInt32(view, 0);
        if (head == lastSeq) return false;

        uint nextSeq = lastSeq + 1;

        // Reader fell more than RING_SLOTS behind: oldest packets have
        // been overwritten. Skip ahead to the oldest still-readable slot
        // and continue from there. This is a real lossy edge case: if
        // a consumer is processing significantly slower than the driver
        // is producing, the tail of the burst wins over the head.
        if (head > nextSeq + (uint)OUTPUT_RING_SLOTS - 1)
            nextSeq = head - (uint)OUTPUT_RING_SLOTS + 1;

        int slotIdx = (int)((nextSeq - 1) % (uint)OUTPUT_RING_SLOTS);
        int slotBase = OUTPUT_HEADER_SIZE + slotIdx * OUTPUT_SLOT_SIZE;

        // Per-slot seqlock: read SeqNo, fields, then SeqNo again, and
        // retry on mismatch. Since the audit of #34, producers RESERVE their
        // sequence via InterlockedIncrement on Head and publish the
        // slot's SeqNo last, so a mismatch here is either a slot whose
        // reserving producer hasn't finished writing (retry next wake;
        // its doorbell signal follows the publish) or a 64-lap overwrite
        // (handled by the skip-ahead above).
        int retries = 4;
        uint slotSeqAfter = 0;
        do
        {
            uint slotSeqBefore = (uint)Marshal.ReadInt32(view, slotBase + OUTPUT_SLOT_OFFSET_SEQNO);
            if (slotSeqBefore != nextSeq)
            {
                // Slot hasn't been written for our expected SeqNo yet.
                // This can happen briefly between the writer incrementing
                // Head and finishing slot.SeqNo. Treat as no new data,
                // try again on next poll.
                return false;
            }
            source = Marshal.ReadByte(view, slotBase + OUTPUT_SLOT_OFFSET_SOURCE);
            reportId = Marshal.ReadByte(view, slotBase + OUTPUT_SLOT_OFFSET_REPORT_ID);
            ushort sz = (ushort)Marshal.ReadInt16(view, slotBase + OUTPUT_SLOT_OFFSET_SIZE);
            if (sz > dataBuf.Length) sz = (ushort)dataBuf.Length;
            dataSize = sz;
            // v1.3.0: bulk Marshal.Copy replaces the prior per-byte
            // Marshal.ReadByte loop. Same per-frame win as the writer side
            // (1 P/Invoke instead of N), running on the SDK's output poll
            // thread which fires every 8 ms for every controller.
            if (sz > 0)
                Marshal.Copy(view + slotBase + OUTPUT_SLOT_OFFSET_DATA, dataBuf, 0, sz);
            Thread.MemoryBarrier();
            slotSeqAfter = (uint)Marshal.ReadInt32(view, slotBase + OUTPUT_SLOT_OFFSET_SEQNO);
            if (slotSeqAfter == slotSeqBefore) break;
            // Slot was being rewritten while we read; retry.
        } while (--retries > 0);

        if (slotSeqAfter == 0) return false; // unstable read after retries
        lastSeq = nextSeq;
        return true;
    }

    /// <summary>The objects an input mapping makes beside the input section
    /// and its event: the companion doorbell, the acknowledgement section
    /// and event, the owner pipe and the version section. Caller holds
    /// s_inputViews.</summary>
    private static void ReleaseInputExtrasLocked(int controllerIndex)
    {
        // The companion doorbell is created per input mapping too, and
        // a recreate of this index makes a new one.
        if (s_companionInputEvents.Remove(controllerIndex, out IntPtr cev) && cev != IntPtr.Zero)
            CloseHandle(cev);
        if (s_inputAckViews.Remove(controllerIndex, out IntPtr av) && av != IntPtr.Zero)
            UnmapViewOfFile(av);
        if (s_inputAckHandles.Remove(controllerIndex, out IntPtr ah) && ah != IntPtr.Zero)
            CloseHandle(ah);
        if (s_inputAckEvents.Remove(controllerIndex, out IntPtr aev) && aev != IntPtr.Zero)
            CloseHandle(aev);
        if (s_ownerPipes.Remove(controllerIndex, out var pipes))
            foreach (var ph in pipes) CloseHandle(ph);
        if (s_versionViews.Remove(controllerIndex, out IntPtr vv) && vv != IntPtr.Zero)
            UnmapViewOfFile(vv);
        if (s_versionHandles.Remove(controllerIndex, out IntPtr vh) && vh != IntPtr.Zero)
            CloseHandle(vh);
    }

    /// <summary>The input section, its event and the objects mapped beside
    /// it, for one index. Caller holds s_inputViews.</summary>
    private static void ReleaseInputLocked(int controllerIndex)
    {
        if (s_inputViews.Remove(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
            UnmapViewOfFile(v);
        if (s_inputHandles.Remove(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
            CloseHandle(h);
        if (s_inputEvents.Remove(controllerIndex, out IntPtr ev) && ev != IntPtr.Zero)
            CloseHandle(ev);
        ReleaseInputExtrasLocked(controllerIndex);
    }

    /// <summary>The output section and its event, for one index. Caller
    /// holds s_outputViews.</summary>
    private static void ReleaseOutputLocked(int controllerIndex)
    {
        if (s_outputViews.Remove(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
            UnmapViewOfFile(v);
        if (s_outputHandles.Remove(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
            CloseHandle(h);
        if (s_outputEvents.Remove(controllerIndex, out IntPtr oe) && oe != IntPtr.Zero)
            CloseHandle(oe);
        s_outputStartSeq.Remove(controllerIndex);
    }

    /// <summary>The PID state section, for one index. Caller holds
    /// s_pidStateViews.</summary>
    private static void ReleasePidStateLocked(int controllerIndex)
    {
        if (s_pidStateViews.Remove(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
            UnmapViewOfFile(v);
        if (s_pidStateHandles.Remove(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
            CloseHandle(h);
    }

    /// <summary>Releases the input and output sections for a single
    /// controller, and ends its hold (<see cref="Hold"/>). Called by the
    /// controller's teardown once its threads have stopped, and by a create
    /// that failed before a controller held the index. Idempotent.</summary>
    public static void DestroyController(int controllerIndex)
    {
        // Before any unmap: this controller's poll thread reads the output
        // view every iteration (issue #45). The hold ends after it, so a
        // sweep running now leaves the view until the thread has stopped.
        StopOutputPump(controllerIndex);
        lock (s_held) s_held.Remove(controllerIndex);

        lock (s_inputViews) ReleaseInputLocked(controllerIndex);
        lock (s_outputViews) ReleaseOutputLocked(controllerIndex);
        lock (s_pidStateViews) ReleasePidStateLocked(controllerIndex);
    }

    /// <summary>Releases every section and event the process maps for an
    /// index no live controller holds. The device sweep's last step. A held
    /// index keeps its objects: its controller's threads and the consumer's
    /// submits still use them, and its Dispose releases them
    /// (<see cref="s_held"/>). Each group decides under its own lock, so an
    /// index a constructor holds before it maps is either released before
    /// that mapping or kept. Safe to call multiple times.</summary>
    public static void Cleanup()
    {
        lock (s_inputViews)
        {
            foreach (int i in Indexes(s_inputViews.Keys, s_inputHandles.Keys, s_inputEvents.Keys,
                                      s_companionInputEvents.Keys, s_inputAckViews.Keys, s_inputAckHandles.Keys,
                                      s_inputAckEvents.Keys, s_ownerPipes.Keys, s_versionViews.Keys, s_versionHandles.Keys))
                if (!IsHeld(i)) ReleaseInputLocked(i);
        }
        lock (s_outputViews)
        {
            foreach (int i in Indexes(s_outputViews.Keys, s_outputHandles.Keys, s_outputEvents.Keys, s_outputStartSeq.Keys))
                if (!IsHeld(i)) ReleaseOutputLocked(i);
        }
        lock (s_pidStateViews)
        {
            foreach (int i in Indexes(s_pidStateViews.Keys, s_pidStateHandles.Keys))
                if (!IsHeld(i)) ReleasePidStateLocked(i);
        }
    }

    /// <summary>Every index the key sets name, copied, so the caller can
    /// remove entries as it walks them.</summary>
    private static HashSet<int> Indexes(params IEnumerable<int>[] keySets)
    {
        var all = new HashSet<int>();
        foreach (var keys in keySets) all.UnionWith(keys);
        return all;
    }

    // ── private helpers ───────────────────────────────────────────────────

    private static (IntPtr handle, IntPtr view) CreateSection(string name, int size)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                Sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new Win32Exception();

        SECURITY_ATTRIBUTES sa = new()
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        IntPtr saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, saPtr, false);

        IntPtr hMap;
        try
        {
            hMap = CreateFileMappingW(new IntPtr(-1), saPtr,
                PAGE_READWRITE, 0, (uint)size, name);
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }

        if (hMap == IntPtr.Zero)
            throw new Win32Exception();

        IntPtr view = MapViewOfFile(hMap, FILE_MAP_WRITE | FILE_MAP_READ,
            0, 0, (UIntPtr)size);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            CloseHandle(hMap);
            throw new Win32Exception(err);
        }
        return (hMap, view);
    }

    /// <summary>Creates a named auto-reset event under the same SDDL used
    /// for the sections so WUDFHost (LocalService) can OpenEvent it. Auto
    /// reset so a single Wait consumes the signal; manual_reset=FALSE means
    /// the dwFlags argument to CreateEventExW is 0 (not MANUAL_RESET).</summary>
    // The owner pipe admits the system, administrators and LocalService,
    // which WUDFHost runs as. No read access for everyone: one stray client
    // would take the instance the driver needs. Owner Administrators (O:BA),
    // as for the sections.
    private const string OwnerPipeSddl = "O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)";
    private const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_ACCESS_DENIED = 5, ERROR_INVALID_HANDLE = 6, ERROR_PIPE_BUSY = 231, ERROR_ALREADY_EXISTS = 183;
    private const uint READ_CONTROL = 0x00020000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>One instance of the owner pipe. The SDK never writes to it
    /// and never reads from it: its only job is to close when this process
    /// does. Every instance keeps v1.12.0's access mode, type, instance
    /// count and timeout, which all instances of one pipe must share, so a
    /// v1.12.0 driver connects to it unchanged.</summary>
    private static IntPtr CreateOwnerPipe(string name, bool first)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(OwnerPipeSddl, 1, out IntPtr sd, IntPtr.Zero))
            return INVALID_HANDLE_VALUE;
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        IntPtr saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        IntPtr pipe;
        int error;
        try
        {
            Marshal.StructureToPtr(sa, saPtr, false);
            pipe = CreateNamedPipeW(name, PIPE_ACCESS_OUTBOUND | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
                                    PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS,
                                    (uint)OWNER_PIPE_INSTANCES, 0, 0, 0, saPtr);
            error = Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }
        // The frees above run P/Invokes of their own. The caller reads the
        // create's error, so it is put back last.
        Marshal.SetLastPInvokeError(error);
        return pipe;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, IntPtr securityAttributes);

    private static IntPtr CreateNamedEvent(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                Sddl, 1, out IntPtr sd, IntPtr.Zero))
            throw new Win32Exception();

        SECURITY_ATTRIBUTES sa = new()
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        IntPtr saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
        Marshal.StructureToPtr(sa, saPtr, false);

        IntPtr ev;
        int error;
        try
        {
            // dwFlags = 0 → auto-reset, not initially set.
            ev = CreateEventExW(saPtr, name, 0, EVENT_MODIFY_STATE | SYNCHRONIZE | READ_CONTROL);
            error = Marshal.GetLastPInvokeError();
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }

        if (ev == IntPtr.Zero)
            throw new Win32Exception(error);
        // An event that already existed came from a controller of this index
        // before, unless another program made it first, after the transport
        // allocator looked (ForeignObjectAt), which its owner, neither
        // SYSTEM nor Administrators, shows. Its maker could wait on it and
        // take the signals meant for the driver. Only the
        // output doorbell may be LocalService-owned (the driver host made
        // it). The SDK's own events are owned by Administrators.
        if (error == ERROR_ALREADY_EXISTS && !IsOwnedByAdmin(ev, allowLocalService: name.Contains("OutputEvent", StringComparison.Ordinal)))
        {
            string? owner;
            using (var h = new Microsoft.Win32.SafeHandles.SafeWaitHandle(ev, ownsHandle: false))
                owner = ObjectOwner.OwnerSid(h);
            CloseHandle(ev);
            // v1.12.0 is named as a possible maker only beside an owner it
            // could have left.
            string found = owner != null
                ? $"is owned by {owner}, so this release did not create it and a controller does not use it. Another program made it, such as one that is not elevated or one built on HIDMaestro 1.12.0 or older."
                : "has an owner this process could not read, so a controller does not use it.";
            throw new HMResourceInUseException(
                $"{name} {found} Create the controller again: it takes another transport index.",
                name, null);
        }
        return ev;
    }

    private static bool IsOwnedByAdmin(IntPtr handle, bool allowLocalService)
    {
        using var h = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: false);
        return ObjectOwner.IsOwnedByAdmin(h, allowLocalService);
    }

    // A transport index's named sections and events, by the part of the
    // name before the index. The driver host, which runs as LocalService,
    // creates StopEvent and OutputEvent. The SDK creates the rest, owned by
    // Administrators, so only those two may be LocalService-owned.
    private static readonly string[] IndexSections = { "Input", "InputAck", "Output", "PidState", "Version" };
    private static readonly string[] IndexEvents = { "InputEvent", "CompanionInputEvent", "InputAckEvent", "OutputEvent", "StopEvent" };

    private static bool DriverCreates(string eventKind) => eventKind is "OutputEvent" or "StopEvent";

    /// <summary>The first of a transport index's named sections and events
    /// that this release did not create, or null when there is none: one
    /// owned by neither SYSTEM nor Administrators (nor, for the driver's two
    /// events, LocalService), or one that does not open as its own kind or
    /// for READ_CONTROL. Who made it is not known: a program that is not
    /// elevated, a service, or any elevated program on a host whose default
    /// owner is the user (KB 947721), a v1.12.0 program included.
    /// Any local program can create a <c>Global\</c> event, or take a
    /// section's name with an object of another kind, before HIDMaestro
    /// does, and keep it. The driver and the SDK would then signal and wait
    /// on objects that program holds, or fail to create theirs. The
    /// transport allocator skips such an index. An object that exists and
    /// opens as its own kind is one a controller of the index left when its
    /// owner is Administrators (the SDK) or SYSTEM, or, for the two events
    /// the driver host creates, LocalService (<see cref="ObjectOwner"/>).
    ///
    /// <para>The result names the object, what was found, and what that
    /// shows (<see cref="Finding"/>). Only an object whose owner was read
    /// can be a v1.12.0 program's: one of another kind, or one that refuses
    /// READ_CONTROL, cannot. An open that failed otherwise, or an owner that
    /// could not be read, shows nothing about who made the object, and the
    /// index is skipped all the same.</para></summary>
    internal static ForeignObject? ForeignObjectAt(int controllerIndex)
    {
        foreach (string kind in IndexSections)
        {
            string name = $@"Global\HIDMaestro{kind}{controllerIndex}";
            if (Unacceptable(OpenFileMappingW(READ_CONTROL, false, name), allowLocalService: false) is { } why)
                return why with { Description = $"{name} {why.Description}" };
        }
        foreach (string kind in IndexEvents)
        {
            string name = $@"Global\HIDMaestro{kind}{controllerIndex}";
            if (Unacceptable(OpenEventW(READ_CONTROL, false, name), DriverCreates(kind)) is { } why)
                return why with { Description = $"{name} {why.Description}" };
        }
        return null;
    }

    /// <summary>What an index's object showed: an owner other than this
    /// release's was read, it is not this release's (another kind, or it
    /// refuses READ_CONTROL), or nothing about its maker was read.</summary>
    internal enum Finding { OwnerRead, NotOurs, Unread }

    /// <summary>An object that keeps a transport index from use: what was
    /// found, and what that shows.</summary>
    internal readonly record struct ForeignObject(string Description, Finding Finding);

    /// <summary>Null for an open that found nothing, or found an object of
    /// the expected kind owned by Administrators or SYSTEM, or by
    /// LocalService when <paramref name="allowLocalService"/>. Otherwise
    /// what was found: the owner, or an open refused for access or for the
    /// kind of object, which also counts as made by someone else, or an
    /// open that failed otherwise or an owner not read, which shows
    /// nothing about the maker.</summary>
    private static ForeignObject? Unacceptable(IntPtr handle, bool allowLocalService)
    {
        if (handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastPInvokeError();
            return err switch
            {
                ERROR_FILE_NOT_FOUND => null,
                ERROR_INVALID_HANDLE => new ForeignObject("is another kind of object", Finding.NotOurs),
                ERROR_ACCESS_DENIED => new ForeignObject("refuses this process READ_CONTROL, so its owner was not read", Finding.NotOurs),
                _ => new ForeignObject($"did not open (Win32 error {err})", Finding.Unread),
            };
        }
        try
        {
            if (IsOwnedByAdmin(handle, allowLocalService)) return null;
            using var h = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, ownsHandle: false);
            return ObjectOwner.OwnerSid(h) is { } owner
                ? new ForeignObject($"is owned by {owner}", Finding.OwnerRead)
                : new ForeignObject("has an owner that could not be read", Finding.Unread);
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out IntPtr SecurityDescriptor, IntPtr SecurityDescriptorSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateEventExW(IntPtr lpEventAttributes, string lpName, uint dwFlags, uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
