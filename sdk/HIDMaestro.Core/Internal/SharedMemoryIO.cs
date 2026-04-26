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
    // HIDMAESTRO_SHARED_INPUT (278 bytes):
    //   ULONG  SeqNo                offset 0
    //   ULONG  DataSize              offset 4
    //   UCHAR  Data[256]             offset 8
    //   UCHAR  GipData[14]           offset 264
    //
    // HIDMAESTRO_SHARED_OUTPUT (264 bytes):
    //   ULONG  SeqNo                 offset 0
    //   UCHAR  Source                offset 4
    //   UCHAR  ReportId              offset 5
    //   USHORT DataSize              offset 6
    //   UCHAR  Data[256]             offset 8
    //
    // Data[] widened from 64→256 bytes 2026-04-23: DualSense BT report 0x31
    // is 78 bytes, Switch Pro standard input report can run to ~64, and
    // gyro/accelerometer values live LATE in those reports. The prior 64-byte
    // pipe silently truncated exactly the motion fields Dolphin / Cemu /
    // yuzu / Citron / RetroArch read via HIDAPI. GipData offset follows
    // directly after, so SHARED_INPUT_SIZE = 4 + 4 + 256 + 14 = 278.

    public const int DATA_OFFSET        = 8;
    public const int DATA_CAPACITY      = 256;
    public const int GIP_DATA_OFFSET    = DATA_OFFSET + DATA_CAPACITY;   // 264
    public const int GIP_DATA_LENGTH    = 14;
    public const int SHARED_INPUT_SIZE  = GIP_DATA_OFFSET + GIP_DATA_LENGTH; // 278
    public const int SHARED_OUTPUT_SIZE = 4 + 1 + 1 + 2 + 256;

    public const byte OUT_SOURCE_HID_OUTPUT  = 0;
    public const byte OUT_SOURCE_HID_FEATURE = 1;
    public const byte OUT_SOURCE_XINPUT      = 2;

    // HIDMAESTRO_SHARED_PID_STATE (24 bytes) — match driver/driver.h:
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
    //   total: 20 bytes (rounded to 24 for clean alignment via _pad1)
    public const int PID_STATE_SIZE                = 24;
    public const int PID_OFFSET_SEQNO              = 0;
    public const int PID_OFFSET_ENABLED            = 4;
    public const int PID_OFFSET_BL_EBI             = 8;
    public const int PID_OFFSET_BL_LOADSTATUS      = 9;
    public const int PID_OFFSET_BL_RAMAVAIL        = 10;
    public const int PID_OFFSET_POOL_RAMSIZE       = 12;
    public const int PID_OFFSET_POOL_MAXSIM        = 14;
    public const int PID_OFFSET_POOL_MEMMGMT       = 15;
    public const int PID_OFFSET_STATE_EBI          = 16;
    public const int PID_OFFSET_STATE_FLAGS        = 17;

    // SDDL granting Local System, Builtin Admins, and LocalService full
    // access plus World read. LocalService is what WUDFHost runs as for
    // UMDF2, so the driver/companion need full access to read input and
    // write output. World read is for non-elevated diagnostic tools.
    private const string Sddl =
        "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GR;;;WD)";

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ  = 0x02;
    private const uint FILE_MAP_WRITE = 0x04;

    // CreateEventW flags
    private const uint CREATE_EVENT_MANUAL_RESET = 0x00000001;
    private const uint CREATE_EVENT_INITIAL_SET  = 0x00000002;
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
            for (int i = 0; i < SHARED_INPUT_SIZE; i++)
                Marshal.WriteByte(view, i, 0);

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
            for (int i = 0; i < SHARED_OUTPUT_SIZE; i++)
                Marshal.WriteByte(view, i, 0);

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

            // Zero-init: PidEnabled=0 means the driver returns
            // STATUS_NO_SUCH_DEVICE for Pool / NOT_SUPPORTED for others
            // until the consumer's first PublishPidPool flips the gate.
            for (int i = 0; i < PID_STATE_SIZE; i++)
                Marshal.WriteByte(view, i, 0);

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

    /// <summary>Publish PID State Report fields.</summary>
    public static void WritePidState(IntPtr view, ref uint seqNo,
                                     byte effectBlockIndex, byte stateFlags)
    {
        PidStateBeginWrite(view, ref seqNo);
        Marshal.WriteByte(view, PID_OFFSET_STATE_EBI,   effectBlockIndex);
        Marshal.WriteByte(view, PID_OFFSET_STATE_FLAGS, stateFlags);
        PidStateEndWrite(view, ref seqNo);
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
                                       byte[] data, int dataLen, byte[] gipData,
                                       int dataOffset = 0)
    {
        // 1. Mark write in progress (odd seqNo)
        uint pending = seqNo + 1;
        Marshal.WriteInt32(view, 0, (int)pending);
        Thread.MemoryBarrier();

        // 2. Write payload (DataSize + Data + GipData)
        Marshal.WriteInt32(view, 4, dataLen);
        for (int i = 0; i < DATA_CAPACITY; i++)
            Marshal.WriteByte(view, DATA_OFFSET + i, i < dataLen ? data[dataOffset + i] : (byte)0);
        for (int i = 0; i < GIP_DATA_LENGTH; i++)
            Marshal.WriteByte(view, GIP_DATA_OFFSET + i, gipData[i]);

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

    /// <summary>Polled seqlock read of the latest output packet. Returns
    /// true if a new packet is available (SeqNo advanced past <paramref name="lastSeq"/>);
    /// false if nothing has changed since the previous call. On success
    /// <paramref name="lastSeq"/> is updated to the new SeqNo.</summary>
    public static bool TryReadOutputFrame(
        IntPtr view, ref uint lastSeq,
        out byte source, out byte reportId, out int dataSize, byte[] dataBuf)
    {
        source = 0; reportId = 0; dataSize = 0;

        uint seq1 = (uint)Marshal.ReadInt32(view, 0);
        if (seq1 == lastSeq) return false;

        // Seqlock read: sample, copy, sample, retry on mismatch.
        int retries = 4;
        uint seq2;
        do
        {
            source = Marshal.ReadByte(view, 4);
            reportId = Marshal.ReadByte(view, 5);
            ushort sz = (ushort)Marshal.ReadInt16(view, 6);
            if (sz > dataBuf.Length) sz = (ushort)dataBuf.Length;
            dataSize = sz;
            for (int i = 0; i < sz; i++)
                dataBuf[i] = Marshal.ReadByte(view, 8 + i);
            Thread.MemoryBarrier();
            seq2 = (uint)Marshal.ReadInt32(view, 0);
            if (seq1 == seq2) break;
            seq1 = seq2;
        } while (--retries > 0);

        lastSeq = seq2;
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
