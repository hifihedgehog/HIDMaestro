using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// Consumer-side owner of the Global\HIDMaestroVR section: the IPC channel
/// between <see cref="HMVRController"/> (elevated consumer process) and the
/// HIDMaestro OpenVR driver running inside SteamVR's vrserver.exe.
///
/// <para>Layout mirrors driver\openvr\src\vr_shared_layout.h byte-for-byte
/// (ABI v1). The input direction reuses the seqlock-writer discipline of
/// <see cref="SharedMemoryIO.WriteInputFrame"/>; the haptic direction is
/// the v1.1.40 output ring of <see cref="SharedMemoryIO.TryReadOutputFrame"/>
/// with the producer and consumer roles reversed (the VR driver produces,
/// this class consumes).</para>
///
/// <para><b>SDDL note:</b> vrserver.exe runs as the interactive user, not
/// LocalService, and it must both wait on the input event and write the
/// haptic ring + DriverPid. GENERIC_READ on an event excludes SYNCHRONIZE,
/// so the HID sections' SDDL is insufficient here: the VR section and both
/// events carry an additional Interactive-Users full-access ACE. Any
/// interactive process can therefore write this section; that is the same
/// trust boundary as the World-readable HID sections and is acceptable for
/// input-device state.</para>
/// </summary>
internal static class VrSharedMemoryIO
{
    // ── Layout constants. Match driver/openvr/src/vr_shared_layout.h ──
    public const uint MAGIC = 0x52564D48;  // 'HMVR'
    public const uint ABI_VERSION = 1;
    public const int SECTION_SIZE = 4096;

    public const int HAND_COUNT = 2;
    public const int SCALAR_COUNT = 16;
    public const int HAPTIC_RING_SLOTS = 64;

    private const int OFF_MAGIC = 0;
    private const int OFF_ABI = 4;
    private const int OFF_CONSUMER_PID = 8;
    private const int OFF_DRIVER_PID = 16;
    private const int OFF_CONFIG_SEQ = 24;
    private const int OFF_HANDS_MASK = 32;
    private const int OFF_PROFILE = 36;    // uint[2]
    private const int OFF_PRIORITY = 44;   // int[2]
    private const int OFF_INPUT_SEQ = 52;
    private const int OFF_TIMESTAMP = 56;
    private const int OFF_CTRL = 64;       // HMVR_CONTROLLER_STATE[2]
    private const int CTRL_SIZE = 68;      // uint ButtonBits + float[16]
    private const int OFF_HAPTIC_HEAD = 200;
    private const int OFF_HAPTIC_SLOTS = 208;
    private const int HAPTIC_SLOT_SIZE = 40;

    private const string SectionName = @"Global\HIDMaestroVR";
    private const string InputEventName = @"Global\HIDMaestroVR_InputEvent";
    private const string HapticEventName = @"Global\HIDMaestroVR_HapticEvent";

    // Base HID SDDL (SY/BA/LS full, World read) plus Interactive Users
    // full access: vrserver must SYNCHRONIZE-wait the input event and
    // map-write the haptic ring. 0x1F0003 (EVENT_ALL_ACCESS) would cover
    // the events alone, but GA keeps section and events uniform.
    private const string Sddl =
        "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GA;;;IU)(A;;GR;;;WD)";

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ = 0x02;
    private const uint FILE_MAP_WRITE = 0x04;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    // ── channel state (one per process, mirrors SharedMemoryIO's static
    //    single-instance rationale) ───────────────────────────────────────
    private static readonly object s_lock = new();
    private static IntPtr s_handle;
    private static IntPtr s_view;
    private static IntPtr s_inputEvent;
    private static IntPtr s_hapticEvent;
    private static uint s_inputSeqNo;

    private static Thread? s_hapticThread;
    private static IntPtr s_hapticStopEvent;
    private static ulong s_hapticCursor;
    private static readonly float[] s_zeroScalars = new float[SCALAR_COUNT];

    /// <summary>Registered per-hand haptic sinks. Index = HMVR hand.</summary>
    private static readonly Action<float, float, float, long>?[] s_hapticSinks
        = new Action<float, float, float, long>?[HAND_COUNT];

    /// <summary>Ensure the section + events exist and this process owns the
    /// consumer slot. Throws if another live process already owns it.</summary>
    public static void EnsureChannel()
    {
        lock (s_lock)
        {
            if (s_view != IntPtr.Zero)
            {
                EnsureOwnershipLocked();
                return;
            }

            (s_handle, s_view) = CreateSection(SectionName, SECTION_SIZE);
            s_inputEvent = CreateNamedEvent(InputEventName);
            s_hapticEvent = CreateNamedEvent(HapticEventName);

            // ABI gate first (only meaningful when a prior consumer
            // already initialized the header), then CLAIM OWNERSHIP,
            // then initialize. Claiming before writing the header keeps
            // two processes racing the very first creation from both
            // "winning": the loser's CAS fails before it touches a
            // single header byte, so it can never zero the winner's
            // HandsMask or ConsumerPid. Pid fields are never
            // plain-written at all; a fresh pagefile section is
            // zero-initialized, and the CAS in EnsureOwnershipLocked is
            // the only writer the layout contract permits
            // (vr_shared_layout.h "ConsumerPid is CAS'd 0 -> pid").
            uint magic = (uint)Marshal.ReadInt32(s_view, OFF_MAGIC);
            if (magic == MAGIC)
            {
                uint abi = (uint)Marshal.ReadInt32(s_view, OFF_ABI);
                if (abi != ABI_VERSION)
                    throw new InvalidOperationException(
                        $"HIDMaestroVR section has ABI v{abi}; this SDK speaks v{ABI_VERSION}. " +
                        "Close the other HIDMaestro consumer (or reboot) and retry.");
            }

            EnsureOwnershipLocked();

            if (magic != MAGIC)
            {
                Marshal.WriteInt64(s_view, OFF_CONFIG_SEQ, 0);
                Marshal.WriteInt32(s_view, OFF_HANDS_MASK, 0);
                Marshal.WriteInt32(s_view, OFF_ABI, (int)ABI_VERSION);
                Thread.MemoryBarrier();
                Marshal.WriteInt32(s_view, OFF_MAGIC, unchecked((int)MAGIC));
            }

            StartHapticListenerLocked();
        }
    }

    private static void EnsureOwnershipLocked()
    {
        long myPid = Environment.ProcessId;
        unsafe
        {
            long* pidField = (long*)((byte*)s_view + OFF_CONSUMER_PID);
            long owner = Interlocked.CompareExchange(ref *pidField, myPid, 0);
            if (owner != 0 && owner != myPid)
            {
                // A stale owner (crashed consumer) is reclaimable; a live
                // one is not. Same liveness test the VR driver applies.
                if (IsProcessAlive((ulong)owner))
                    throw new InvalidOperationException(
                        $"Another process (PID {owner}) already owns the HIDMaestro VR channel. " +
                        "Only one consumer process can drive VR controllers at a time.");
                Interlocked.CompareExchange(ref *pidField, myPid, owner);
                if (Interlocked.Read(ref *pidField) != myPid)
                    throw new InvalidOperationException(
                        "Lost the race to reclaim the HIDMaestro VR channel; retry.");
                // Stale owner's config is void.
                Marshal.WriteInt32(s_view, OFF_HANDS_MASK, 0);
                BumpConfigSeqLocked();
            }
        }
    }

    private static void BumpConfigSeqLocked()
    {
        long seq = Marshal.ReadInt64(s_view, OFF_CONFIG_SEQ);
        Thread.MemoryBarrier();
        Marshal.WriteInt64(s_view, OFF_CONFIG_SEQ, seq + 1);
        SetEvent(s_inputEvent); // config rides the input event (driver re-checks)
    }

    /// <summary>Publish a hand's profile + priority and set its active
    /// bit. Throws if the hand is already active (one controller per hand).</summary>
    public static void RegisterHand(int hand, uint profile, int priority)
    {
        lock (s_lock)
        {
            ThrowIfNoChannel();
            uint mask = (uint)Marshal.ReadInt32(s_view, OFF_HANDS_MASK);
            if ((mask & (1u << hand)) != 0)
                throw new InvalidOperationException(
                    $"A virtual VR controller for the {(hand == 0 ? "left" : "right")} hand is already active.");

            Marshal.WriteInt32(s_view, OFF_PROFILE + 4 * hand, (int)profile);
            Marshal.WriteInt32(s_view, OFF_PRIORITY + 4 * hand, priority);
            Marshal.WriteInt32(s_view, OFF_HANDS_MASK, (int)(mask | (1u << hand)));
            BumpConfigSeqLocked();
        }
    }

    /// <summary>Clear a hand's active bit. When the last hand drops, the
    /// consumer slot is released so another process can take the channel.</summary>
    public static void UnregisterHand(int hand)
    {
        lock (s_lock)
        {
            if (s_view == IntPtr.Zero) return;
            uint mask = (uint)Marshal.ReadInt32(s_view, OFF_HANDS_MASK);
            mask &= ~(1u << hand);
            Marshal.WriteInt32(s_view, OFF_HANDS_MASK, (int)mask);
            BumpConfigSeqLocked();
            s_hapticSinks[hand] = null;

            if (mask == 0)
            {
                unsafe
                {
                    long* pidField = (long*)((byte*)s_view + OFF_CONSUMER_PID);
                    Interlocked.CompareExchange(ref *pidField, 0, Environment.ProcessId);
                }
            }
        }
    }

    /// <summary>Seqlock-write one hand's state. The other hand's bytes are
    /// left untouched (its own controller owns them). Same odd/even +
    /// barrier discipline as <see cref="SharedMemoryIO.WriteInputFrame"/>;
    /// the s_lock serialization makes the two hands a single logical
    /// writer, which the driver-side 4-retry reader assumes.</summary>
    public static void WriteFrame(int hand, uint buttonBits, float[]? scalars)
    {
        lock (s_lock)
        {
            ThrowIfNoChannel();

            uint pending = s_inputSeqNo + 1;
            Marshal.WriteInt32(s_view, OFF_INPUT_SEQ, (int)pending);
            Thread.MemoryBarrier();

            Marshal.WriteInt64(s_view, OFF_TIMESTAMP,
                System.Diagnostics.Stopwatch.GetTimestamp());
            int ctrlBase = OFF_CTRL + CTRL_SIZE * hand;
            Marshal.WriteInt32(s_view, ctrlBase, (int)buttonBits);
            if (scalars != null)
            {
                int n = Math.Min(scalars.Length, SCALAR_COUNT);
                Marshal.Copy(scalars, 0, s_view + ctrlBase + 4, n);
            }
            else
            {
                // null = all zeros per the HMVRState contract. Skipping
                // the copy would leak the PREVIOUS frame's scalars into
                // this frame (a trigger pulled once would stick forever
                // for a button-only submitter). Same stale-bytes
                // discipline as WriteInputFrame's mandatory per-frame
                // ExtendedReportSize clear (SharedMemoryIO.cs:457-466).
                Marshal.Copy(s_zeroScalars, 0, s_view + ctrlBase + 4, SCALAR_COUNT);
            }

            Thread.MemoryBarrier();
            s_inputSeqNo = pending + 1;
            Marshal.WriteInt32(s_view, OFF_INPUT_SEQ, (int)s_inputSeqNo);

            SetEvent(s_inputEvent);
        }
    }

    /// <summary>Route haptic replies for a hand to a sink. Fired on the
    /// listener thread; keep handlers cheap (same contract as
    /// HMController.OutputReceived).</summary>
    public static void SetHapticSink(int hand, Action<float, float, float, long>? sink)
        => s_hapticSinks[hand] = sink;

    /// <summary>True while the OpenVR driver has the section attached
    /// (DriverPid written by the driver, process still alive).</summary>
    public static bool IsDriverConnected()
    {
        lock (s_lock)
        {
            if (s_view == IntPtr.Zero) return false;
            ulong pid = (ulong)Marshal.ReadInt64(s_view, OFF_DRIVER_PID);
            return pid != 0 && IsProcessAlive(pid);
        }
    }

    // ── haptic ring consumer ────────────────────────────────────────────

    private static void StartHapticListenerLocked()
    {
        if (s_hapticThread != null) return;

        s_hapticStopEvent = CreateEventExW(IntPtr.Zero, null!, 1 /*manual*/,
                                           EVENT_MODIFY_STATE | SYNCHRONIZE);
        if (s_hapticStopEvent == IntPtr.Zero)
            throw new Win32Exception();
        // Start the cursor at the current head so a fresh consumer doesn't
        // replay stale haptics from a previous session.
        s_hapticCursor = (ulong)Marshal.ReadInt64(s_view, OFF_HAPTIC_HEAD);

        s_hapticThread = new Thread(HapticListenerProc)
        {
            IsBackground = true,
            Name = "HIDMaestro.VrHapticListener",
        };
        s_hapticThread.Start();
    }

    private static void HapticListenerProc()
    {
        IntPtr[] waits = { s_hapticStopEvent, s_hapticEvent };
        while (true)
        {
            uint rc = WaitForMultipleObjects(2, waits, false, 500);
            if (rc == 0) return;          // stop event
            if (rc != 1 && rc != 0x102)
            {
                // WAIT_FAILED / anything unexpected means a dead handle.
                // Exit rather than re-issuing the failing wait in a hot
                // loop (the driver.c:833-840 rule: every non-signal path
                // must make progress, never spin).
                return;
            }

            while (TryReadHapticSlot(out int hand, out float amp,
                                     out float dur, out float freq, out long ts))
            {
                if ((uint)hand < HAND_COUNT)
                    s_hapticSinks[hand]?.Invoke(amp, dur, freq, ts);
            }
        }
    }

    /// <summary>Tear the channel down: stop the listener, release the
    /// consumer slot, close every handle. Called from HMContext.Dispose
    /// (mirroring SharedMemoryIO.Cleanup's role for the HID sections);
    /// standalone HMVRController users who never touch HMContext keep the
    /// process-exit lifetime. Safe to call multiple times; a later
    /// EnsureChannel re-creates everything.</summary>
    public static void Cleanup()
    {
        Thread? listener;
        lock (s_lock)
        {
            if (s_view == IntPtr.Zero) return;

            listener = s_hapticThread;
            if (s_hapticStopEvent != IntPtr.Zero)
                SetEvent(s_hapticStopEvent);
        }

        // Join outside the lock: the listener takes s_lock inside
        // TryReadHapticSlot.
        listener?.Join(1500);

        lock (s_lock)
        {
            if (s_view == IntPtr.Zero) return;

            // Release any hands a consumer forgot to disconnect, then the
            // ownership slot itself.
            Marshal.WriteInt32(s_view, OFF_HANDS_MASK, 0);
            long seq = Marshal.ReadInt64(s_view, OFF_CONFIG_SEQ);
            Thread.MemoryBarrier();
            Marshal.WriteInt64(s_view, OFF_CONFIG_SEQ, seq + 1);
            if (s_inputEvent != IntPtr.Zero) SetEvent(s_inputEvent);
            unsafe
            {
                long* pidField = (long*)((byte*)s_view + OFF_CONSUMER_PID);
                Interlocked.CompareExchange(ref *pidField, 0, Environment.ProcessId);
            }

            for (int h = 0; h < HAND_COUNT; h++) s_hapticSinks[h] = null;

            UnmapViewOfFile(s_view);
            s_view = IntPtr.Zero;
            if (s_handle != IntPtr.Zero) { CloseHandle(s_handle); s_handle = IntPtr.Zero; }
            if (s_inputEvent != IntPtr.Zero) { CloseHandle(s_inputEvent); s_inputEvent = IntPtr.Zero; }
            if (s_hapticEvent != IntPtr.Zero) { CloseHandle(s_hapticEvent); s_hapticEvent = IntPtr.Zero; }
            if (s_hapticStopEvent != IntPtr.Zero) { CloseHandle(s_hapticStopEvent); s_hapticStopEvent = IntPtr.Zero; }
            s_hapticThread = null;
            s_inputSeqNo = 0;
            s_hapticCursor = 0;
        }
    }

    /// <summary>Ring read, the <see cref="SharedMemoryIO.TryReadOutputFrame"/>
    /// protocol against the 40-byte VR haptic slots: private cursor,
    /// skip-ahead when overrun, per-slot seqno validate + re-check.</summary>
    private static bool TryReadHapticSlot(out int hand, out float amplitude,
                                          out float duration, out float frequency,
                                          out long timestamp)
    {
        hand = 0; amplitude = 0; duration = 0; frequency = 0; timestamp = 0;

        IntPtr view;
        lock (s_lock)
        {
            view = s_view;
            if (view == IntPtr.Zero) return false;
        }

        ulong head = (ulong)Marshal.ReadInt64(view, OFF_HAPTIC_HEAD);
        if (head == s_hapticCursor) return false;

        ulong nextSeq = s_hapticCursor + 1;
        if (head > nextSeq + HAPTIC_RING_SLOTS - 1)
            nextSeq = head - HAPTIC_RING_SLOTS + 1;

        int slotBase = OFF_HAPTIC_SLOTS
            + (int)((nextSeq - 1) % HAPTIC_RING_SLOTS) * HAPTIC_SLOT_SIZE;

        int retries = 4;
        ulong seqAfter = 0;
        do
        {
            ulong seqBefore = (ulong)Marshal.ReadInt64(view, slotBase);
            if (seqBefore != nextSeq)
                return false; // slot not published yet for our seq

            hand = Marshal.ReadInt32(view, slotBase + 8);
            amplitude = ReadFloat(view, slotBase + 16);
            duration = ReadFloat(view, slotBase + 20);
            frequency = ReadFloat(view, slotBase + 24);
            timestamp = Marshal.ReadInt64(view, slotBase + 32);
            Thread.MemoryBarrier();
            seqAfter = (ulong)Marshal.ReadInt64(view, slotBase);
            if (seqAfter == seqBefore) break;
        } while (--retries > 0);

        if (seqAfter == 0) return false;
        s_hapticCursor = nextSeq;
        return true;
    }

    private static unsafe float ReadFloat(IntPtr view, int offset)
    {
        int raw = Marshal.ReadInt32(view, offset);
        return *(float*)&raw;
    }

    private static void ThrowIfNoChannel()
    {
        if (s_view == IntPtr.Zero)
            throw new InvalidOperationException(
                "VR channel not connected. Call HMVRController.Connect() first.");
    }

    private static bool IsProcessAlive(ulong pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE;
        }
        finally { CloseHandle(h); }
    }

    // ── section/event creation (SharedMemoryIO.CreateSection /
    //    CreateNamedEvent shape with the VR SDDL) ────────────────────────

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint sddlRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventExW(IntPtr lpEventAttributes, string lpName,
        uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles,
        bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
}
