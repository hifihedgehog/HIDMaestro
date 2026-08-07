using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>Consumer-side accessor for the <c>Global\HIDMaestroVR</c> IPC
/// section (issue #32). This is the byte-for-byte C# mirror of
/// <c>driver/openvr/src/vr_protocol.h</c>; the two are kept in lockstep by
/// <c>test/probes/vr_controller_smoke</c>, which plays the driver's role
/// against this producer and fails on any offset drift.
///
/// <para>Roles: the consumer (this class) CREATES the section and both
/// events, CASes <see cref="ConsumerPidOffset"/> to claim it, writes the
/// config and the seqlock input frame, and reads the driver-produced
/// haptic ring and status block. The OpenVR driver inside vrserver.exe
/// opens everything by name and does the reverse. Section and event
/// creation mirror <see cref="SharedMemoryIO"/>'s CreateSection pattern
/// with one SDDL change: vrserver runs as the interactive user, so the
/// Interactive group needs full access on the section (it writes the
/// haptic ring) and on the events (a waiter needs SYNCHRONIZE, which
/// GENERIC_READ does not include).</para></summary>
internal static class VrSharedMemory
{
    public const string SectionName = @"Global\HIDMaestroVR";
    public const string InputEventName = @"Global\HIDMaestroVR_InputEvent";
    public const string HapticEventName = @"Global\HIDMaestroVR_HapticEvent";

    public const uint Magic = 0x52564D48;  // 'HMVR'
    public const uint AbiVersion = 1;

    public const int HandCount = 2;
    public const int ScalarCount = 16;
    public const int HapticSlots = 64;

    // ── Offsets, mirroring the packed HMVR_Section layout ──────────────
    //
    //  HMVR_ControllerState = 4 + 16*4 + 4 + 3*4 + 4*4 = 100 bytes
    //  HMVR_InputFrame      = 8 + 2*100               = 208 bytes
    //  HMVR_Config          = 4 + 7*4                 = 32 bytes
    //  HMVR_HapticReply     = 8 + 4 + 3*4 + 8         = 32 bytes
    //  HMVR_Status          = 4*4 + 3*4 + 4*4 + 8     = 52 bytes
    public const int ControllerStateSize = 100;
    public const int InputFrameSize = 8 + HandCount * ControllerStateSize;   // 208

    public const int MagicOffset = 0;
    public const int AbiVersionOffset = 4;
    public const int ConsumerPidOffset = 8;
    public const int ConfigSeqNoOffset = 16;
    public const int ConfigOffset = 24;
    public const int ConfigSize = 32;
    public const int InputSeqNoOffset = ConfigOffset + ConfigSize;            // 56
    public const int InputFrameOffset = InputSeqNoOffset + 4;                 // 60
    // + 4 alignment pad: hapticHead and each ring slot's seqNo are read
    // and written outside any seqlock, so they must sit on 8-byte
    // boundaries for x64 single-copy atomicity. Pinned by the C++
    // static_asserts in vr_protocol.h.
    public const int HapticHeadOffset = InputFrameOffset + InputFrameSize + 4; // 272
    public const int HapticRingOffset = HapticHeadOffset + 8;                  // 280
    public const int HapticReplySize = 32;
    public const int StatusOffset = HapticRingOffset + HapticSlots * HapticReplySize; // 2328
    public const int StatusSize = 52;
    public const int SectionSize = StatusOffset + StatusSize;                  // 2380

    // Controller-state field offsets within one HMVR_ControllerState.
    public const int StateButtonBitsOffset = 0;
    public const int StateScalarsOffset = 4;
    public const int StatePoseValidOffset = StateScalarsOffset + ScalarCount * 4;  // 68
    public const int StatePositionOffset = StatePoseValidOffset + 4;               // 72
    public const int StateOrientationOffset = StatePositionOffset + 12;            // 84

    // ── Win32 ──────────────────────────────────────────────────────────

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    private const uint EVENT_MODIFY_STATE = 0x0002;

    // SharedMemoryIO's SDDL grants SY/BA full, LS full, World read. VR
    // needs the Interactive group instead of LocalService: vrserver runs
    // in the logged-on user's session and both maps the section for
    // haptic writes and waits on events. 0x1F0003 = EVENT_ALL_ACCESS.
    private const string SectionSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;IU)";
    private const string EventSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x1F0003;;;IU)";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES attrs,
        uint protect, uint maxHigh, uint maxLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES attrs, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    public sealed class Channel : IDisposable
    {
        public IntPtr View;
        public IntPtr Mapping;
        public IntPtr InputEvent;
        public IntPtr HapticEvent;
        private bool _ownsPid;

        public void Dispose()
        {
            if (_ownsPid && View != IntPtr.Zero)
            {
                // Release the claim so a successor consumer can attach and
                // the driver's liveness check sees a clean disconnect.
                Interlocked.Exchange(ref MarshalRef<long>(View, ConsumerPidOffset), 0);
            }
            if (View != IntPtr.Zero) { UnmapViewOfFile(View); View = IntPtr.Zero; }
            if (Mapping != IntPtr.Zero) { CloseHandle(Mapping); Mapping = IntPtr.Zero; }
            if (InputEvent != IntPtr.Zero) { CloseHandle(InputEvent); InputEvent = IntPtr.Zero; }
            if (HapticEvent != IntPtr.Zero) { CloseHandle(HapticEvent); HapticEvent = IntPtr.Zero; }
        }

        internal void MarkOwner() => _ownsPid = true;
    }

    private static ref T MarshalRef<T>(IntPtr basePtr, int offset) where T : unmanaged
    {
        unsafe { return ref *(T*)(basePtr + offset); }
    }

    /// <summary>Create (or open) the section + events and claim the
    /// consumer slot via a CAS on consumerPid. Throws on ABI mismatch or
    /// when another consumer holds the claim and is still alive.</summary>
    public static Channel Create()
    {
        var ch = new Channel();
        try
        {
            ch.Mapping = CreateNamed(SectionName, SectionSddl,
                (sa) => CreateFileMappingW(new IntPtr(-1), ref sa, PAGE_READWRITE, 0, SectionSize, SectionName));
            ch.View = MapViewOfFile(ch.Mapping, FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)SectionSize);
            if (ch.View == IntPtr.Zero)
                throw new InvalidOperationException($"MapViewOfFile({SectionName}) failed: {Marshal.GetLastWin32Error()}");

            ch.InputEvent = CreateNamed(InputEventName, EventSddl,
                (sa) => CreateEventW(ref sa, manualReset: false, initialState: false, InputEventName));
            ch.HapticEvent = CreateNamed(HapticEventName, EventSddl,
                (sa) => CreateEventW(ref sa, manualReset: false, initialState: false, HapticEventName));

            ref long pidSlot = ref MarshalRef<long>(ch.View, ConsumerPidOffset);
            long existing = Interlocked.Read(ref pidSlot);
            long myPid = Environment.ProcessId;

            uint magic = MarshalRef<uint>(ch.View, MagicOffset);
            if (magic != 0 && magic != Magic)
                throw new InvalidOperationException($"HIDMaestroVR section carries foreign magic 0x{magic:X8}");
            uint abi = MarshalRef<uint>(ch.View, AbiVersionOffset);
            if (magic == Magic && abi != AbiVersion)
                throw new InvalidOperationException($"HIDMaestroVR ABI mismatch: section {abi}, this build {AbiVersion}");

            if (existing != 0 && existing != myPid && ProcessAlive((int)existing))
                throw new InvalidOperationException($"HIDMaestroVR section is claimed by live pid {existing}");
            // Stale or unclaimed: CAS against the exact observed value so
            // two racing consumers cannot both win; the loser sees the
            // winner's pid and throws.
            long won = Interlocked.CompareExchange(ref pidSlot, myPid, existing);
            if (won != existing && won != myPid)
                throw new InvalidOperationException($"HIDMaestroVR section was claimed concurrently by pid {won}");
            ch.MarkOwner();

            // Stamp identity AFTER the claim so a parallel loser never
            // half-initializes over the winner.
            MarshalRef<uint>(ch.View, AbiVersionOffset) = AbiVersion;
            Thread.MemoryBarrier();
            MarshalRef<uint>(ch.View, MagicOffset) = Magic;
            return ch;
        }
        catch
        {
            ch.Dispose();
            throw;
        }
    }

    private static IntPtr CreateNamed(string name, string sddl, Func<SECURITY_ATTRIBUTES, IntPtr> creator)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr sd, out _))
            throw new InvalidOperationException($"SDDL parse failed for {name}: {Marshal.GetLastWin32Error()}");
        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                Length = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                SecurityDescriptor = sd,
                InheritHandle = false,
            };
            IntPtr h = creator(sa);
            if (h == IntPtr.Zero)
                throw new InvalidOperationException($"Create({name}) failed: {Marshal.GetLastWin32Error()}");
            return h;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Publish config (active hands) and bump configSeqNo.</summary>
    public static void WriteConfig(Channel ch, uint handsActive)
    {
        MarshalRef<uint>(ch.View, ConfigOffset) = handsActive;
        Thread.MemoryBarrier();
        MarshalRef<ulong>(ch.View, ConfigSeqNoOffset)++;
        SetEvent(ch.InputEvent);
    }

    /// <summary>Seqlock-write one input frame and signal the driver. The
    /// odd/even protocol is SharedMemoryIO.WriteInputFrame's exactly:
    /// odd seqno, barrier, payload, barrier, even seqno, SetEvent.</summary>
    public static void WriteInputFrame(Channel ch, ReadOnlySpan<byte> frame208)
    {
        if (frame208.Length != InputFrameSize)
            throw new ArgumentException($"frame must be {InputFrameSize} bytes", nameof(frame208));

        ref uint seq = ref MarshalRef<uint>(ch.View, InputSeqNoOffset);
        uint odd = (seq + 1) | 1u;
        seq = odd;
        Thread.MemoryBarrier();
        unsafe
        {
            fixed (byte* src = frame208)
            {
                Buffer.MemoryCopy(src, (void*)(ch.View + InputFrameOffset), InputFrameSize, InputFrameSize);
            }
        }
        Thread.MemoryBarrier();
        seq = odd + 1;
        SetEvent(ch.InputEvent);
    }

    /// <summary>Read the next haptic reply after <paramref name="cursor"/>,
    /// advancing it. The reader protocol is SharedMemoryIO.TryReadOutputFrame's:
    /// the cursor is PRIVATE to this consumer, each slot is validated by its
    /// seqNo with a re-check after the copy, and a reader more than
    /// <see cref="HapticSlots"/> behind skips to the oldest readable slot.</summary>
    public static bool TryReadHaptic(Channel ch, ref ulong cursor,
        out uint hand, out float duration, out float frequency, out float amplitude, out ulong timestampQpc)
    {
        hand = 0; duration = 0; frequency = 0; amplitude = 0; timestampQpc = 0;

        ulong head = MarshalRef<ulong>(ch.View, HapticHeadOffset);
        if (head == 0 || cursor >= head)
            return false;
        if (head - cursor > HapticSlots)
            cursor = head - HapticSlots;   // tail-wins skip

        ulong want = cursor + 1;
        int slotOffset = HapticRingOffset + (int)((want - 1) % HapticSlots) * HapticReplySize;

        ulong seq1 = MarshalRef<ulong>(ch.View, slotOffset);
        if (seq1 != want)
            return false;   // producer mid-write; retry on the next signal
        hand = MarshalRef<uint>(ch.View, slotOffset + 8);
        duration = MarshalRef<float>(ch.View, slotOffset + 12);
        frequency = MarshalRef<float>(ch.View, slotOffset + 16);
        amplitude = MarshalRef<float>(ch.View, slotOffset + 20);
        timestampQpc = MarshalRef<ulong>(ch.View, slotOffset + 24);
        Thread.MemoryBarrier();
        ulong seq2 = MarshalRef<ulong>(ch.View, slotOffset);
        if (seq2 != want)
            return false;   // torn: slot was recycled mid-copy

        cursor = want;
        return true;
    }

    /// <summary>Seqlock-read the driver status block. False on a torn or
    /// in-progress read after 4 retries (driver.c ReadSharedInput's retry
    /// posture).</summary>
    public static bool TryReadStatus(Channel ch, out uint driverActive, out uint controllersLive,
        out uint hmdPoseValid, float[] hmdPosition3, float[] hmdOrientation4, out ulong heartbeatQpc)
    {
        driverActive = 0; controllersLive = 0; hmdPoseValid = 0; heartbeatQpc = 0;

        for (int retry = 0; retry < 4; retry++)
        {
            uint s1 = MarshalRef<uint>(ch.View, StatusOffset);
            if ((s1 & 1) != 0) continue;
            Thread.MemoryBarrier();
            uint active = MarshalRef<uint>(ch.View, StatusOffset + 4);
            uint live = MarshalRef<uint>(ch.View, StatusOffset + 8);
            uint poseValid = MarshalRef<uint>(ch.View, StatusOffset + 12);
            for (int i = 0; i < 3; i++) hmdPosition3[i] = MarshalRef<float>(ch.View, StatusOffset + 16 + i * 4);
            for (int i = 0; i < 4; i++) hmdOrientation4[i] = MarshalRef<float>(ch.View, StatusOffset + 28 + i * 4);
            ulong hb = MarshalRef<ulong>(ch.View, StatusOffset + 44);
            Thread.MemoryBarrier();
            uint s2 = MarshalRef<uint>(ch.View, StatusOffset);
            if (s1 != s2) continue;

            driverActive = active;
            controllersLive = live;
            hmdPoseValid = poseValid;
            heartbeatQpc = hb;
            return true;
        }
        return false;
    }
}
