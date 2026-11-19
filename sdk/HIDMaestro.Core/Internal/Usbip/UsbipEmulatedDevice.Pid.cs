using System;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>PID force feedback on a USB/IP device: the UMDF2 driver's PID
/// block, played by the device over HID class requests. DirectInput reaches
/// it through HidUsb, whose HidD_GetFeature, HidD_SetFeature and
/// HidD_SetOutputReport arrive here as GET_REPORT and SET_REPORT on the
/// control pipe, where the UMDF2 driver gets them as IOCTL_UMDF_HID_GET_FEATURE,
/// IOCTL_UMDF_HID_SET_FEATURE and IOCTL_UMDF_HID_SET_OUTPUT_REPORT. The
/// state is the same section the driver reads and writes,
/// Global\HIDMaestroPidState&lt;N&gt;, which the controller's PublishPid*
/// calls make and fill, with the same report IDs, the same effect block
/// allocator and the same answers. A report that fails on the driver with
/// STATUS_NOT_SUPPORTED, STATUS_NO_SUCH_DEVICE or STATUS_BUFFER_TOO_SMALL
/// stalls here, the one failure a USB device can give.
///
/// <para>The descriptor decides, never the profile. A derived device, the
/// USB/IP form of a profile the UMDF2 driver presents, has the block by the
/// driver's own rule, which serves the canonical PID report IDs on any
/// descriptor and the IDs of the descriptor's PID reports besides
/// (<see cref="PidCandidatesFor"/>), so every profile built with
/// <see cref="HidDescriptorBuilder.AddPidFfbBlock"/>, every vendor PID layout
/// and every descriptor that only reuses a canonical ID answers as it does on
/// UMDF2. An authored device, a captured persona the driver never presents,
/// has it only when its descriptor declares the PID usage page (0x0F), so a
/// captured device's own feature reports keep their captured answers.</para>
///
/// <para>An interrupt OUT transfer is the driver's IOCTL_HID_WRITE_REPORT,
/// which the driver publishes and does not hand to the PID block (driver.c,
/// the IOCTL_HID_WRITE_REPORT case), so it takes no part here either.</para></summary>
internal sealed partial class UsbipEmulatedDevice
{
    // The report IDs a PID report can arrive on, by ID (driver.c
    // BuildPidCandidates). Null for a device without the block, which
    // answers no PID report.
    private readonly bool[]? _pidCandidate;

    // GET_REPORT(Feature) on the Pool report the device stalled because no
    // published Pool could be read: none published, or no snapshot of the
    // section held still in four tries. The driver fails both with
    // STATUS_NO_SUCH_DEVICE. A check reads it to see whether DirectInput
    // asks again.
    private int _pidPoolRefusals;

    /// <summary>Pool reads the device stalled for want of a published Pool
    /// (<see cref="HMController.PublishPidPool"/>) it could read.</summary>
    internal int PidPoolRefusals => Volatile.Read(ref _pidPoolRefusals);

    /// <summary>True when the device serves the PID block.</summary>
    internal bool ServesPid => _pidCandidate != null;

    // driver.h HIDMAESTRO_PID_*_REPORT_ID: the canonical IDs, used where the
    // section holds no override.
    private const byte PidCreateNewEffectId = 0x11;
    private const byte PidBlockLoadId = 0x12;
    private const byte PidPoolId = 0x13;
    private const byte PidStateId = 0x14;
    private const byte PidBlockFreeId = 0x1B;
    private const byte PidDeviceControlId = 0x1C;

    /// <summary>The candidates of driver.c BuildPidCandidates, built from
    /// the same descriptor, line for line: the canonical six, then the report
    /// ID of every main item inside a Logical Collection whose usage is Set
    /// Effect, Create New Effect, Block Free, Device Control, Pool, Block
    /// Load or State, the innermost such collection, with the report ID read
    /// both with Push and Pop and without. A descriptor the walk cannot
    /// follow to its end makes every ID a candidate. The walk also notes the
    /// PID usage page, as a Usage Page item or a four-byte usage: with
    /// <paramref name="requirePidPage"/> a descriptor without it gets
    /// null.</summary>
    internal static bool[]? PidCandidatesFor(byte[]? d, bool requirePidPage)
    {
        if (d == null || d.Length == 0) return null;
        var cand = new bool[256];
        Span<byte> collType = stackalloc byte[32];
        Span<byte> collUsage = stackalloc byte[32];
        Span<byte> idStack = stackalloc byte[8];
        int depth = 0, idDepth = 0;
        byte ridScoped = 0, ridFlat = 0;
        bool haveUsage = false, complete = true, pidPage = false;
        byte firstUsage = 0;
        int n = d.Length, i = 0;
        foreach (byte id in stackalloc byte[] { PidCreateNewEffectId, PidBlockLoadId, PidPoolId, PidStateId, PidBlockFreeId, PidDeviceControlId })
            cand[id] = true;
        while (i < n)
        {
            byte prefix = d[i];
            if (prefix == 0xFE)
            {
                // A long item: size, tag, data.
                if (i + 2 >= n) { complete = false; break; }
                i += 3 + d[i + 1];
                continue;
            }
            int sz = prefix & 0x03;
            if (sz == 3) sz = 4;
            if (i + 1 + sz > n) { complete = false; break; }
            uint val = 0;
            for (int k = 0; k < sz; k++) val |= (uint)d[i + 1 + k] << (8 * k);
            int type = (prefix >> 2) & 0x03, tag = prefix >> 4;
            if (type == 0)
            {
                if (tag == 10)
                {
                    // Collection.
                    if (depth == collType.Length) { complete = false; break; }
                    collType[depth] = (byte)val;
                    collUsage[depth] = haveUsage ? firstUsage : (byte)0;
                    depth++;
                }
                else if (tag == 12)
                {
                    // End Collection.
                    if (depth > 0) depth--;
                }
                else if (tag == 8 || tag == 9 || tag == 11)
                {
                    // Input, Output, Feature.
                    for (int k = depth - 1; k >= 0; k--)
                    {
                        byte u = collUsage[k];
                        if (collType[k] != 0x02) continue;
                        if (u == 0x21 || u == 0x88 || u == 0x90 || u == 0x96 || u == 0x7F || u == 0x89 || u == 0x92)
                        {
                            if (ridScoped != 0) cand[ridScoped] = true;
                            if (ridFlat != 0) cand[ridFlat] = true;
                            break;
                        }
                    }
                }
                // Locals end at every main item.
                haveUsage = false;
            }
            else if (type == 1)
            {
                if (tag == 0 && val == 0x0F) pidPage = true;          // Usage Page (PID)
                else if (tag == 8) { ridScoped = (byte)val; ridFlat = (byte)val; }
                else if (tag == 10)
                {
                    // Push.
                    if (idDepth == idStack.Length) { complete = false; break; }
                    idStack[idDepth++] = ridScoped;
                }
                else if (tag == 11 && idDepth > 0)
                {
                    // Pop.
                    ridScoped = idStack[--idDepth];
                }
            }
            else if (type == 2 && tag == 0)
            {
                if (sz == 4 && (val >> 16) == 0x0F) pidPage = true;   // a usage that names the PID page
                if (!haveUsage)
                {
                    firstUsage = (byte)val;
                    haveUsage = true;
                }
            }
            i += 1 + sz;
        }
        if (requirePidPage && !pidPage) return null;
        if (!complete) Array.Fill(cand, true);
        return cand;
    }

    /// <summary>The PID report IDs in force: the section's overrides, which
    /// the controller writes from PidReportIdExtractor when it makes the
    /// section, else the canonical IDs (driver.c, the
    /// <c>if (sec-&gt;XxxReportId)</c> lines of each PID case). Caller holds
    /// <see cref="SharedMemoryIO.PidStateLock"/>.</summary>
    private static byte PidIdLocked(IntPtr view, int offset, byte canonical)
    {
        if (view == IntPtr.Zero) return canonical;
        byte v = System.Runtime.InteropServices.Marshal.ReadByte(view, offset);
        return v != 0 ? v : canonical;
    }

    /// <summary>A SET_REPORT the PID block acts on before the device
    /// publishes it, as the driver acts on it before PublishOutput: Create
    /// New Effect allocates an effect block (a feature report only), Block
    /// Free frees one, and Device Control with Control 4 (DISFFC_RESET)
    /// resets the block (driver.c, the IOCTL_UMDF_HID_SET_FEATURE and
    /// IOCTL_UMDF_HID_SET_OUTPUT_REPORT cases). <paramref name="reportId"/>
    /// is the data stage's first byte and <paramref name="data"/> the rest,
    /// as the driver splits its buffer.</summary>
    private void PidOnSetReport(byte reportType, byte reportId, ReadOnlySpan<byte> data)
    {
        if (reportType != 0x02 && reportType != 0x03) return;
        // Only an ID a PID report can arrive on opens the section.
        if (!_pidCandidate![reportId]) return;
        lock (SharedMemoryIO.PidStateLock)
        {
            IntPtr view = SharedMemoryIO.PidStateViewLocked(_index, _ackGeneration);
            if (view == IntPtr.Zero) return;
            byte createNewEffect = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_NEW_EFFECT_RID, PidCreateNewEffectId);
            byte blockFree = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_BLOCK_FREE_RID, PidBlockFreeId);
            byte deviceControl = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_DEVICE_CONTROL_RID, PidDeviceControlId);
            // Create New Effect is a feature report. On the output path the
            // same ID is Set Effect, which selects a block and allocates
            // none (driver.c, the IOCTL_UMDF_HID_SET_OUTPUT_REPORT comment).
            if (reportType == 0x03 && reportId == createNewEffect)
                AllocateEbiInBlockLoad(view);
            else if (reportId == blockFree && data.Length >= 1)
                FreeEbi(view, data[0]);
            else if (reportId == deviceControl && data.Length >= 1 && data[0] == 4)
                ResetPidState(view);
        }
    }

    /// <summary>GET_REPORT(Feature) on a report the answer table does not
    /// hold, the driver's PID part of IOCTL_UMDF_HID_GET_FEATURE: Create New
    /// Effect succeeds with nothing written, Block Load, Pool and State are
    /// answered from the section once the program has published a Pool, and
    /// everything else fails.</summary>
    private void AnswerPidFeature(uint seqnum, byte reportId, ushort wLength)
    {
        // A request for no bytes fails first, as the driver's outSize < 1
        // does (STATUS_BUFFER_TOO_SMALL).
        if (wLength < 1 || !_pidCandidate![reportId]) { SendError(seqnum, -UsbipProtocol.EPipe); return; }
        Span<byte> reply = stackalloc byte[5];
        int n = -1;
        bool poolRefused = false;
        lock (SharedMemoryIO.PidStateLock)
        {
            IntPtr view = SharedMemoryIO.PidStateViewLocked(_index, _ackGeneration);
            byte createNewEffect = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_NEW_EFFECT_RID, PidCreateNewEffectId);
            byte pool = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_POOL_RID, PidPoolId);
            byte state = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_STATE_RID, PidStateId);
            byte blockLoad = PidIdLocked(view, SharedMemoryIO.PID_OFFSET_BL_RID, PidBlockLoadId);
            if (reportId == createNewEffect)
            {
                // Silent success, the buffer untouched (vJoy's
                // vJoyGetFeature has no case for it).
                n = 0;
            }
            else if (!ReadPidState(view, out var pid) || !pid.Enabled)
            {
                // No Pool published: the driver fails the Pool read with
                // STATUS_NO_SUCH_DEVICE and the others with
                // STATUS_NOT_SUPPORTED.
                poolRefused = reportId == pool;
            }
            else if (reportId == blockLoad)
            {
                if (wLength >= 5)
                {
                    reply[0] = reportId;
                    reply[1] = pid.BlockLoadEbi;
                    reply[2] = pid.BlockLoadStatus is >= 1 and <= 3 ? pid.BlockLoadStatus : (byte)3;
                    reply[3] = (byte)(pid.BlockLoadRamAvailable & 0xFF);
                    reply[4] = (byte)(pid.BlockLoadRamAvailable >> 8);
                    n = 5;
                }
            }
            else if (reportId == pool)
            {
                if (wLength >= 5)
                {
                    reply[0] = reportId;
                    reply[1] = (byte)(pid.PoolRamSize & 0xFF);
                    reply[2] = (byte)(pid.PoolRamSize >> 8);
                    reply[3] = pid.PoolMaxSimultaneous;
                    reply[4] = pid.PoolMemoryManagement;
                    n = 5;
                }
            }
            else if (reportId == state)
            {
                if (wLength >= 3)
                {
                    reply[0] = reportId;
                    reply[1] = pid.StateEbi;
                    reply[2] = pid.StateFlags;
                    n = 3;
                }
            }
        }
        if (poolRefused) Interlocked.Increment(ref _pidPoolRefusals);
        if (n < 0) SendError(seqnum, -UsbipProtocol.EPipe);
        else SendRetSubmitSpan(seqnum, 0, n, reply[..n]);
    }

    /// <summary>GET_REPORT(Input) on the PID State report, the driver's PID
    /// part of IOCTL_UMDF_HID_GET_INPUT_REPORT: three bytes from the
    /// section, zeros past the ID while it holds no state. False for any
    /// other report, which the input reports answer.</summary>
    private bool TryAnswerPidState(uint seqnum, byte reportId, ushort wLength)
    {
        if (!_pidCandidate![reportId]) return false;
        Span<byte> reply = stackalloc byte[3];
        lock (SharedMemoryIO.PidStateLock)
        {
            IntPtr view = SharedMemoryIO.PidStateViewLocked(_index, _ackGeneration);
            if (reportId != PidIdLocked(view, SharedMemoryIO.PID_OFFSET_STATE_RID, PidStateId)) return false;
            bool have = ReadPidState(view, out var pid);
            reply[0] = reportId;
            reply[1] = have ? pid.StateEbi : (byte)0;
            reply[2] = have ? pid.StateFlags : (byte)0;
        }
        if (wLength < 3) SendError(seqnum, -UsbipProtocol.EPipe);
        else SendRetSubmitSpan(seqnum, 0, 3, reply);
        return true;
    }

    private struct PidSnapshot
    {
        public bool Enabled;
        public byte BlockLoadEbi, BlockLoadStatus;
        public ushort BlockLoadRamAvailable;
        public ushort PoolRamSize;
        public byte PoolMaxSimultaneous, PoolMemoryManagement;
        public byte StateEbi, StateFlags;
    }

    /// <summary>driver.c ReadPidState: a seqlocked snapshot, false when the
    /// section is not there or no read held still in four tries.</summary>
    private static unsafe bool ReadPidState(IntPtr view, out PidSnapshot s)
    {
        s = default;
        if (view == IntPtr.Zero) return false;
        byte* p = (byte*)view;
        ref int seqNo = ref *(int*)(p + SharedMemoryIO.PID_OFFSET_SEQNO);
        uint seq1, seq2;
        int retries = 4;
        do
        {
            seq1 = (uint)Volatile.Read(ref seqNo);
            if ((seq1 & 1) != 0) seq1 = (uint)Volatile.Read(ref seqNo);   // a publisher mid-write: one more look
            Interlocked.MemoryBarrier();
            s.Enabled = p[SharedMemoryIO.PID_OFFSET_ENABLED] != 0;
            s.BlockLoadEbi = p[SharedMemoryIO.PID_OFFSET_BL_EBI];
            s.BlockLoadStatus = p[SharedMemoryIO.PID_OFFSET_BL_LOADSTATUS];
            s.BlockLoadRamAvailable = *(ushort*)(p + SharedMemoryIO.PID_OFFSET_BL_RAMAVAIL);
            s.PoolRamSize = *(ushort*)(p + SharedMemoryIO.PID_OFFSET_POOL_RAMSIZE);
            s.PoolMaxSimultaneous = p[SharedMemoryIO.PID_OFFSET_POOL_MAXSIM];
            s.PoolMemoryManagement = p[SharedMemoryIO.PID_OFFSET_POOL_MEMMGMT];
            s.StateEbi = p[SharedMemoryIO.PID_OFFSET_STATE_EBI];
            s.StateFlags = p[SharedMemoryIO.PID_OFFSET_STATE_FLAGS];
            Interlocked.MemoryBarrier();
            seq2 = (uint)Volatile.Read(ref seqNo);
            if (seq1 == seq2 && (seq1 & 1) == 0) break;
        } while (--retries > 0);
        return retries > 0;
    }

    /// <summary>driver.c AllocateEbiInBlockLoad: the first free effect
    /// block below the published simultaneous-effect count (32 when it is 0
    /// or more than the bitmap holds) becomes the Block Load's index, with
    /// Success, or Full and index 0 when none is free, and the RAM still
    /// available as (pool size / cap) * (cap - allocated).</summary>
    private static unsafe void AllocateEbiInBlockLoad(IntPtr view)
    {
        byte* p = (byte*)view;
        int cap = p[SharedMemoryIO.PID_OFFSET_POOL_MAXSIM];
        if (cap == 0 || cap > 32) cap = 32;
        ref int bitmap = ref *(int*)(p + SharedMemoryIO.PID_OFFSET_EBI_BITMAP);
        ref int count = ref *(int*)(p + SharedMemoryIO.PID_OFFSET_EBI_COUNT);
        byte allocated = 0;
        byte loadStatus = 2;   // Full (PID 1.0, 5.5)
        uint remaining = 0;
        for (int ebi = 1; ebi <= cap; ebi++)
        {
            int bit = 1 << (ebi - 1);
            int prev = Interlocked.Or(ref bitmap, bit);
            if ((prev & bit) == 0)
            {
                // This call turned the bit on: the block is its own.
                allocated = (byte)ebi;
                loadStatus = 1;   // Success
                Interlocked.Increment(ref count);
                break;
            }
        }
        ushort poolSize = *(ushort*)(p + SharedMemoryIO.PID_OFFSET_POOL_RAMSIZE);
        uint allocCount = (uint)Volatile.Read(ref count);
        if (allocCount <= (uint)cap) remaining = (uint)(poolSize / cap) * ((uint)cap - allocCount);
        // The section's SeqNo, as the driver counts on it and the program's
        // writers do (SharedMemoryIO.PidStateBeginWrite).
        uint seq = 0;
        SharedMemoryIO.PidStateBeginWrite(view, ref seq);
        p[SharedMemoryIO.PID_OFFSET_BL_EBI] = allocated;
        p[SharedMemoryIO.PID_OFFSET_BL_LOADSTATUS] = loadStatus;
        *(ushort*)(p + SharedMemoryIO.PID_OFFSET_BL_RAMAVAIL) = (ushort)remaining;
        SharedMemoryIO.PidStateEndWrite(view, ref seq);
    }

    /// <summary>driver.c ResetPidState: every effect block freed, the Block
    /// Load and State cleared, the RAM available back to the pool size. The
    /// Pool stays: it is the program's.</summary>
    private static unsafe void ResetPidState(IntPtr view)
    {
        byte* p = (byte*)view;
        Interlocked.Exchange(ref *(int*)(p + SharedMemoryIO.PID_OFFSET_EBI_BITMAP), 0);
        Interlocked.Exchange(ref *(int*)(p + SharedMemoryIO.PID_OFFSET_EBI_COUNT), 0);
        uint seq = 0;
        SharedMemoryIO.PidStateBeginWrite(view, ref seq);
        p[SharedMemoryIO.PID_OFFSET_BL_EBI] = 0;
        p[SharedMemoryIO.PID_OFFSET_BL_LOADSTATUS] = 0;
        *(ushort*)(p + SharedMemoryIO.PID_OFFSET_BL_RAMAVAIL) = *(ushort*)(p + SharedMemoryIO.PID_OFFSET_POOL_RAMSIZE);
        p[SharedMemoryIO.PID_OFFSET_STATE_EBI] = 0;
        p[SharedMemoryIO.PID_OFFSET_STATE_FLAGS] = 0;
        SharedMemoryIO.PidStateEndWrite(view, ref seq);
    }

    /// <summary>driver.c FreeEbi: the block's bit cleared, and the count
    /// lowered only when this call cleared it.</summary>
    private static unsafe void FreeEbi(IntPtr view, byte ebi)
    {
        if (ebi < 1 || ebi > 32) return;
        byte* p = (byte*)view;
        int bit = 1 << (ebi - 1);
        int prev = Interlocked.And(ref *(int*)(p + SharedMemoryIO.PID_OFFSET_EBI_BITMAP), ~bit);
        if ((prev & bit) != 0) Interlocked.Decrement(ref *(int*)(p + SharedMemoryIO.PID_OFFSET_EBI_COUNT));
    }
}
