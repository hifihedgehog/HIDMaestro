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
/// in-tree test app go through the same instance — there's only ever one
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
internal static class SharedMemoryIO
{
    // ── Layout constants — match driver/driver.h ──────────────────────────
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
    // HIDMAESTRO_SHARED_OUTPUT — RING BUFFER as of v1.1.40.
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
    // lag tolerance before oldest packets get overwritten — comfortable
    // headroom for a 125 Hz consumer vs ~3 ms write bursts.
    //
    // Data[] widened from 64→256 bytes 2026-04-23: DualSense BT report 0x31
    // is 78 bytes; Switch Pro standard input report can run to ~64.

    public const int DATA_OFFSET                = 8;
    public const int DATA_CAPACITY              = 256;
    public const int GIP_DATA_OFFSET            = DATA_OFFSET + DATA_CAPACITY;   // 264
    public const int GIP_DATA_LENGTH            = 14;
    // v1.3.5 — vendor-blob extended report (mode-switch path).
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

    // HIDMAESTRO_SHARED_PID_STATE — match driver/driver.h:
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
    //   UCHAR   PoolReportId                offset 28  (v1.3.7 — 0=canonical 0x13)
    //   UCHAR   StateReportId               offset 29  (v1.3.7 — 0=canonical 0x14)
    //   UCHAR   BlockLoadReportId           offset 30  (v1.3.7 — 0=canonical 0x12)
    //   UCHAR   CreateNewEffectReportId     offset 31  (v1.3.7 — 0=canonical 0x11)
    //   UCHAR   BlockFreeReportId           offset 32  (v1.3.7 — 0=canonical 0x1B)
    //   UCHAR   DeviceControlReportId       offset 33  (v1.3.7 — 0=canonical 0x1C)
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
    // write output. World read is for non-elevated diagnostic tools.
    private const string Sddl =
        "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GR;;;WD)";

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ  = 0x02;
    private const uint FILE_MAP_WRITE = 0x04;

    // CreateEventW EVENT_MODIFY_STATE | SYNCHRONIZE — open existing named
    // events for SetEvent and waiting from the SDK side.
    private const uint EVENT_MODIFY_STATE        = 0x0002;
    private const uint SYNCHRONIZE               = 0x00100000;

    private static readonly Dictionary<int, IntPtr> s_inputHandles  = new();
    private static readonly Dictionary<int, IntPtr> s_inputViews    = new();
    private static readonly Dictionary<int, IntPtr> s_inputEvents   = new();
    private static readonly Dictionary<int, IntPtr> s_outputHandles = new();
    private static readonly Dictionary<int, IntPtr> s_outputViews   = new();
    private static readonly Dictionary<int, IntPtr> s_pidStateHandles = new();
    private static readonly Dictionary<int, IntPtr> s_pidStateViews   = new();

    /// <summary>Returns the view pointer for the controller's INPUT section,
    /// creating the section on first call. Thread-safe via per-call lock —
    /// callers can issue concurrent EnsureInputMapping requests for distinct
    /// controllerIndex values without races. Idempotent for the same index.</summary>
    public static IntPtr EnsureInputMapping(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr existing))
                return existing;

            string name = $@"Global\HIDMaestroInput{controllerIndex}";
            (IntPtr h, IntPtr view) = CreateSection(name, SHARED_INPUT_SIZE);

            // Zero-init: pagefile-backed sections start zero already, but we
            // explicitly write SeqNo=0 so a stale section from a previous
            // run (under the same name) doesn't carry forward a non-zero
            // sequence number that the driver would treat as "no change".
            // Bulk Marshal.Copy from a freshly allocated zero buffer is one
            // P/Invoke; the prior per-byte loop was 278 P/Invokes per setup.
            Marshal.Copy(new byte[SHARED_INPUT_SIZE], 0, view, SHARED_INPUT_SIZE);

            // Companion signaling event. Auto-reset (manual_reset=FALSE), not
            // initially set. The driver's per-device worker thread waits on
            // this with a 50ms safety timeout to replace the old 1ms busy poll.
            // SDDL matches the section so WUDFHost (LocalService) can open it.
            string evName = $@"Global\HIDMaestroInputEvent{controllerIndex}";
            IntPtr ev = CreateNamedEvent(evName);

            s_inputHandles[controllerIndex] = h;
            s_inputViews[controllerIndex] = view;
            s_inputEvents[controllerIndex] = ev;
            return view;
        }
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
            // OutputReceived packet — on repeat if a consumer process's
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

    /// <summary>Returns the view pointer for the controller's PID FFB
    /// state section, creating the section on first call. The driver
    /// attaches read-only and reads on every IOCTL_UMDF_HID_GET_FEATURE.
    /// Lazy: a non-FFB consumer that never publishes PID state never
    /// triggers this — the section simply doesn't exist and the driver
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

            // v1.1.38 — initialize with vJoy-compatible defaults rather
            // than zeros. vJoy's Ffb_ResetPIDData
            // (vJoy-Brunner/driver/sys/hid.c:2627) sets RAMPoolSize=200,
            // MaxSimultaneousEffects=10, MemoryManagement=0 unconditionally
            // at device-add. dinput8 reads Pool defaults during enumeration
            // (before any consumer has called PublishPidPool); zero
            // RAMPoolSize / MaxSim could lead dinput into degenerate
            // branches (divide-by-zero, no-effects path).
            //
            // PidEnabled stays 0 — the consumer still has to PublishPidPool
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
    /// last-write-wins per HID PID 1.0 §5.5 — call from the
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
    /// behavior. Outside the seqlock — written exactly once at
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

    /// <summary>v1.1.37 — seqlocked read of the Block Load fields the
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
        // Fall through after retries — return whatever the last sample was.
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

    /// <summary>Atomic seqlock write of a new input frame. Single-writer
    /// (the SDK consumer's input loop) → many-readers (driver + companion)
    /// pattern is safe lock-free — readers retry on SeqNo mismatch. After
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
    /// case — the write still completes normally.</para></summary>
    public static void WriteInputFrame(IntPtr view, IntPtr eventHandle, ref uint seqNo,
                                       byte[] data, int dataLen, byte[]? gipData,
                                       int dataOffset = 0,
                                       byte[]? extendedData = null, int extendedLen = 0)
    {
        // 1. Mark write in progress (odd seqNo)
        uint pending = seqNo + 1;
        Marshal.WriteInt32(view, 0, (int)pending);
        Thread.MemoryBarrier();

        // 2. Write payload (DataSize + Data + GipData + ExtendedReport*).
        // v1.3.0 — bulk Marshal.Copy replaces the prior per-byte
        // Marshal.WriteByte loops, which were 256 + 14 = 270 P/Invoke
        // calls per frame. At 250 Hz × 6 controllers that was ~400 000
        // P/Invokes/sec; a single bulk copy per region is two
        // P/Invokes/frame total. We don't zero the unused data tail past
        // dataLen — driver/consumer reads DataSize and uses only
        // data[0..DataSize-1]; the tail is irrelevant. T26-2 — gipData
        // is null for non-Xbox profiles (no XUSB companion bound), so we
        // can skip the 14-byte copy entirely. Section is zero-initialized
        // at create time, so the GIP slice stays zeros.
        //
        // v1.3.5 — when extendedData != null AND extendedLen > 0, copy
        // the full RID-included extended report into ExtendedReportData
        // and set ExtendedReportSize. Driver branches on
        // ExtendedReportSize > 0 and emits ExtendedReportData verbatim
        // (no FirstInputReportId prepend). When extendedData is null we
        // explicitly clear ExtendedReportSize so a previously-armed
        // controller's stale extended bytes don't leak through after
        // a legacy-mode frame (e.g. if armOn fires were ever rolled back
        // — currently they aren't, but cheap insurance).
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
    }

    /// <summary>v1.1.40 ring read. Reads the next output packet at
    /// SeqNo <paramref name="lastSeq"/>+1 if available. Returns true with
    /// the packet bytes if a fresh slot is published; false if nothing
    /// new since the last call. On success <paramref name="lastSeq"/>
    /// advances by 1 and the slot's data is copied into <paramref name="dataBuf"/>.
    ///
    /// Caller pumps this in a loop on each poll iteration so all slots
    /// between the previous LastSeen and current Head get drained — see
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
        // and continue from there. This is a real lossy edge case — if
        // a consumer is processing significantly slower than the driver
        // is producing, the tail of the burst wins over the head.
        if (head > nextSeq + (uint)OUTPUT_RING_SLOTS - 1)
            nextSeq = head - (uint)OUTPUT_RING_SLOTS + 1;

        int slotIdx = (int)((nextSeq - 1) % (uint)OUTPUT_RING_SLOTS);
        int slotBase = OUTPUT_HEADER_SIZE + slotIdx * OUTPUT_SLOT_SIZE;

        // Per-slot seqlock: read SeqNo, fields, SeqNo again — retry on
        // mismatch (writer mid-update on the same slot, very rare given
        // 64 slots and a single-producer driver).
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
            // v1.3.0 — bulk Marshal.Copy replaces the prior per-byte
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

    /// <summary>Releases the input and output sections for a single controller.
    /// Called when an HMController is disposed. Idempotent.</summary>
    public static void DestroyController(int controllerIndex)
    {
        lock (s_inputViews)
        {
            if (s_inputViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
                UnmapViewOfFile(v);
            s_inputViews.Remove(controllerIndex);

            if (s_inputHandles.TryGetValue(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
                CloseHandle(h);
            s_inputHandles.Remove(controllerIndex);

            if (s_inputEvents.TryGetValue(controllerIndex, out IntPtr ev) && ev != IntPtr.Zero)
                CloseHandle(ev);
            s_inputEvents.Remove(controllerIndex);
        }
        lock (s_outputViews)
        {
            if (s_outputViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
                UnmapViewOfFile(v);
            s_outputViews.Remove(controllerIndex);

            if (s_outputHandles.TryGetValue(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
                CloseHandle(h);
            s_outputHandles.Remove(controllerIndex);
        }
        lock (s_pidStateViews)
        {
            if (s_pidStateViews.TryGetValue(controllerIndex, out IntPtr v) && v != IntPtr.Zero)
                UnmapViewOfFile(v);
            s_pidStateViews.Remove(controllerIndex);

            if (s_pidStateHandles.TryGetValue(controllerIndex, out IntPtr h) && h != IntPtr.Zero)
                CloseHandle(h);
            s_pidStateHandles.Remove(controllerIndex);
        }
    }

    /// <summary>Releases all input and output sections owned by the process.
    /// Safe to call multiple times. Called on process exit and on
    /// HMContext.Dispose.</summary>
    public static void Cleanup()
    {
        lock (s_inputViews)
        {
            foreach (var v in s_inputViews.Values)
                if (v != IntPtr.Zero) UnmapViewOfFile(v);
            s_inputViews.Clear();
            foreach (var h in s_inputHandles.Values)
                if (h != IntPtr.Zero) CloseHandle(h);
            s_inputHandles.Clear();
            foreach (var ev in s_inputEvents.Values)
                if (ev != IntPtr.Zero) CloseHandle(ev);
            s_inputEvents.Clear();
        }
        lock (s_outputViews)
        {
            foreach (var v in s_outputViews.Values)
                if (v != IntPtr.Zero) UnmapViewOfFile(v);
            s_outputViews.Clear();
            foreach (var h in s_outputHandles.Values)
                if (h != IntPtr.Zero) CloseHandle(h);
            s_outputHandles.Clear();
        }
        lock (s_pidStateViews)
        {
            foreach (var v in s_pidStateViews.Values)
                if (v != IntPtr.Zero) UnmapViewOfFile(v);
            s_pidStateViews.Clear();
            foreach (var h in s_pidStateHandles.Values)
                if (h != IntPtr.Zero) CloseHandle(h);
            s_pidStateHandles.Clear();
        }
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
        try
        {
            // dwFlags = 0 → auto-reset, not initially set.
            ev = CreateEventExW(saPtr, name, 0, EVENT_MODIFY_STATE | SYNCHRONIZE);
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            LocalFree(sd);
        }

        if (ev == IntPtr.Zero)
            throw new Win32Exception();
        return ev;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes,
        uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor, uint StringSDRevision,
        out IntPtr SecurityDescriptor, IntPtr SecurityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventExW(IntPtr lpEventAttributes, string lpName,
        uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);
}
