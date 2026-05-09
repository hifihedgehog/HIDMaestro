using System;
using System.Collections.Generic;
using System.Threading;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// A live virtual controller. Created by <see cref="HMContext.CreateController"/>;
/// dispose to remove the device. The controller exposes two channels:
///
/// <para><b>Input</b> (host → game): the consumer pushes <see cref="HMGamepadState"/>
/// frames via <see cref="SubmitState"/> at whatever rate they want — typically the
/// rate of their real input source. The SDK translates the abstract state into the
/// profile's native HID descriptor format and writes it to a shared section that
/// the kernel-side driver reads at ~250 Hz. There is no internal pumping thread;
/// the consumer drives the cadence.</para>
///
/// <para><b>Output</b> (game → host): the SDK runs a background polling thread that
/// captures rumble / haptics / FFB / LED commands from any host application and
/// raises <see cref="OutputReceived"/>. Handlers run on the polling thread, not
/// the consumer's UI thread — implement your handler accordingly.</para>
/// </summary>
public sealed class HMController : IDisposable
{
    private readonly HMContext _context;
    internal int Index { get; }
    internal string? InstanceId { get; }
    public HMProfile Profile { get; }

    // Encoder built once from the profile descriptor at construction time;
    // SubmitState reuses it for every frame.
    private readonly HidReportBuilder _reportBuilder;
    private readonly IntPtr _inputView;
    // Named auto-reset event signaled by WriteInputFrame so the driver's
    // worker thread can wake immediately instead of busy-polling. Cached at
    // construction time alongside the view pointer.
    private readonly IntPtr _inputEvent;
    private uint _inputSeqNo;

    // Output passthrough reader (rumble/haptics/FFB) — background thread
    // poll-reads the per-controller output section and raises OutputReceived.
    private readonly IntPtr _outputView;
    private readonly Thread? _outputThread;
    private readonly CancellationTokenSource _outputCts = new();

    // 14-byte GIP-format buffer reused per frame to avoid per-call alloc.
    // The XUSB companion (HMXInput.dll, used for non-xinputhid Xbox
    // profiles like Xbox 360 wired) reads ONLY this slice from shared
    // memory when servicing IOCTL_XUSB_GET_STATE — it does not read the
    // HID native bytes. For Xbox-VID profiles SubmitState packs LX/LY/RX
    // /RY/LT/RT/buttons into this buffer in the layout the companion
    // expects. For non-Xbox profiles the buffer stays zeroed (companion
    // is not bound, so the bytes are unused).
    //
    // Layout (matches the proven pre-SDK test app):
    //   [0..1]  LX  16-bit unsigned (0..65535)
    //   [2..3]  LY  16-bit unsigned
    //   [4..5]  RX  16-bit unsigned
    //   [6..7]  RY  16-bit unsigned
    //   [8..9]  LT  10-bit unsigned in the low bits
    //   [10..11] RT 10-bit unsigned in the low bits
    //   [12]    btnLow  (A=0x01 B=0x02 X=0x04 Y=0x08 LB=0x10 RB=0x20 LS=0x40 RS=0x80)
    //   [13]    btnHigh (Back=0x01 Start=0x02 …)
    private readonly byte[] _gipBuf = new byte[14];

    // v1.3.0 — per-controller reusable HID input report buffer. SubmitState
    // calls BuildReportInto(_reportBuffer, ...) instead of BuildReport which
    // allocates a fresh byte[] each frame. At 250 Hz × N controllers the
    // alloc churn was real GC pressure; reusing avoids it entirely.
    // Sized at HidReportBuilder.InputReportByteSize, computed in the ctor.
    private readonly byte[] _reportBuffer;

    // v1.3.0 — per-controller reusable raw report buffer. SubmitRawReport
    // (DualSense / vendor-protocol path) used to do report.ToArray() per
    // call; this 64-byte buffer absorbs the copy without the alloc churn.
    private readonly byte[] _rawReportBuffer = new byte[64];

    /// <summary>Raised on the SDK's output-polling thread whenever a host
    /// application sends a rumble, haptic, FFB, feature, or LED command to
    /// this virtual controller. Subscribers must be thread-safe.
    ///
    /// <para><b>Cadence and ordering (v1.1.40+):</b> the SDK polls the
    /// driver's output ring every ~8 ms. On each poll the consumer drains
    /// every slot the driver has written since the last poll, in
    /// monotonic SeqNo order. Multiple <c>OutputReceived</c> invocations
    /// per poll iteration are normal — DirectInput PID FFB writes 3
    /// packets in 1-3 ms (Set Effect → Set Constant Force → Effect
    /// Operation Start) and all three surface here.</para>
    ///
    /// <para><b>Ring depth:</b> 64 slots × 256-byte payload. If the
    /// consumer's handler stalls for &gt; 512 ms while the driver is
    /// writing at burst rate, the oldest packets get overwritten —
    /// keep the handler cheap (no synchronous I/O, no long locks).
    /// Pre-1.1.40 was a single-slot channel that silently coalesced
    /// back-to-back writes; that drop pattern is fixed.</para></summary>
    public event Action<HMController, HMOutputPacket>? OutputReceived;

    /// <summary>v1.3.5 — raised when an inbound output report matches the
    /// profile's <see cref="HMProfile.HasExtendedOutput"/> spec. The SDK
    /// decodes the bytes per the profile's <c>extendedOutputReport</c> field
    /// list and surfaces parsed values (rumble amplitudes, lightbar RGB,
    /// adaptive-trigger blocks, etc.) keyed by semantic name.
    ///
    /// <para>Consumers that want raw bytes still get them via
    /// <see cref="OutputReceived"/> — both events fire for matching reports.
    /// Subscribers must be thread-safe (raised on the polling thread).</para></summary>
    public event EventHandler<HMOutputDecodedEventArgs>? OutputDecoded;

    // v1.3.5 — vendor-blob input encoder state. Built lazily when the profile
    // declares extendedReport. Holds rolling counters (Sony's framingTag /
    // reportCounter increment monotonically across SubmitState calls).
    private VendorBlobCodec.EncoderState? _extEncoderState;

    // v1.3.5 — vendor-blob output encoder state. Allocated lazily on the
    // first EncodeOutput call so consumers that never call it (input-only
    // virtuals, output-via-OnOutputReceived consumers) skip the dictionary
    // alloc. Holds rolling counters for output direction — Sony BT effect
    // output's btTag increments stride-16 per write or real firmware drops
    // the packet.
    private VendorBlobCodec.EncoderState? _outputEncoderState;
    private readonly object _outputEncoderStateLock = new();

    // v1.3.5 — buffer sized to ExtendedReport.Size, allocated once. NULL
    // when the profile has no extendedReport.
    private byte[]? _extendedReportBuffer;

    // v1.3.5 — host-side arm flag. False until a host write matches one of
    // ExtendedReport.armOn triggers; true thereafter for the lifetime of
    // this controller. Until armed, SubmitState falls through to the
    // descriptor-driven BuildReportInto path so consumers that never issue
    // the handshake still see legacy Report 1 emission.
    private volatile bool _extendedModeArmed;

    /// <summary>Optional diagnostic: invoked at the end of every successful
    /// <see cref="SubmitState"/> with the elapsed microseconds. Wire this
    /// when investigating per-frame submit latency (e.g. issue #21 USB
    /// stalls). Called inline on the caller's thread; keep the handler
    /// short — log to a ring buffer or counter, don't block.</summary>
    public Action<long>? OnSubmitLatencyMicros { get; set; }

    // PID FFB state section. Lazy: created on the first PublishPid* call so
    // a non-FFB consumer never allocates the section. Once created, the
    // driver's IOCTL_UMDF_HID_GET_FEATURE handler reads from it on every
    // HidD_GetFeature for the canonical PID Report IDs (0x12, 0x13, 0x14).
    private IntPtr _pidStateView;
    private uint _pidStateSeqNo;
    private readonly object _pidStateLock = new();

    private IntPtr EnsurePidStateViewLocked()
    {
        if (_pidStateView == IntPtr.Zero)
        {
            _pidStateView = SharedMemoryIO.EnsurePidStateMapping(Index);
            // v1.3.7 — write the profile descriptor's PID Report ID
            // layout to shared state immediately after section creation,
            // before any IOCTL handler reads. Builder-emitted descriptors
            // (every existing profile that uses HidDescriptorBuilder.
            // AddPidFfbBlock) come back with canonical IDs and the driver's
            // `if (sec->XxxReportId)` guards skip the override; vendor-PID
            // descriptors (Microsoft SideWinder Force Feedback 2 with
            // Pool=0x03, BlockLoad=0x02, Set Effect=0x01) return non-zero
            // overrides and the driver dispatches IOCTLs to the right RIDs.
            // See PidReportIdExtractor for the LC-usage match table.
            var rids = PidReportIdExtractor.Extract(Profile.Inner.GetDescriptorBytes());
            SharedMemoryIO.WritePidReportIds(_pidStateView,
                rids.PoolReportId, rids.StateReportId, rids.BlockLoadReportId,
                rids.CreateNewEffectReportId, rids.BlockFreeReportId, rids.DeviceControlReportId);
        }
        return _pidStateView;
    }

    /// <summary>Publish the current PID Pool Report state (HID PID 1.0 §5.7).
    /// First call enables FFB on this controller — until called at least once,
    /// HidD_GetFeature on the Pool Report ID returns STATUS_NO_SUCH_DEVICE
    /// (matching vJoy's "FFB not enabled" convention), so DInput cleanly
    /// concludes "device exists but no FFB" rather than retrying.
    /// Subsequent calls update the pool state.
    ///
    /// <para><b>Descriptor requirements:</b> the controller's HID descriptor
    /// must declare the PID FFB report block. Use
    /// <see cref="HidDescriptorBuilder.AddPidFfbBlock"/> — that method emits
    /// the canonical "minimum viable" block (Set Effect 0x11, Set Constant
    /// Force 0x15, Effect Operation 0x1A, Device Control 0x1C, etc., plus
    /// the single Feature report Create New Effect 0x11). Do NOT add
    /// additional Feature reports (0x12 Block Load, 0x13 PID Pool, 0x14
    /// PID State) inside the same Application Collection — the four-feature
    /// variant from vJoy's reference descriptor causes pid.dll to AV inside
    /// PID_EffectOperation+0x52 the first time the consumer calls
    /// CreateEffect (DirectX 8-era pid.dll FFB enumeration bug, not
    /// OS-build-gated; verified empirically on Windows 11 26100 — issue #16).
    /// Pool, Block Load, and PID State are served by the driver from a
    /// separate shared-section path that doesn't touch pid.dll's preparsed-data
    /// parser — that's what <see cref="PublishPidPool"/>, <see cref="PublishPidBlockLoad"/>,
    /// and <see cref="PublishPidState"/> publish to.</para></summary>
    /// <param name="ramPoolSize">Total RAM pool size in bytes.</param>
    /// <param name="simultaneousEffectsMax">Max effects the device can play simultaneously.</param>
    /// <param name="deviceManagedPool">True if the device manages effect block allocation.</param>
    /// <param name="sharedParameterBlocks">True if effect parameter blocks can be shared.</param>
    public void PublishPidPool(ushort ramPoolSize, byte simultaneousEffectsMax,
                               bool deviceManagedPool, bool sharedParameterBlocks)
    {
        ThrowIfDisposed();
        lock (_pidStateLock)
        {
            IntPtr view = EnsurePidStateViewLocked();
            if (view == IntPtr.Zero) return;
            SharedMemoryIO.WritePidPool(view, ref _pidStateSeqNo,
                ramPoolSize, simultaneousEffectsMax,
                deviceManagedPool, sharedParameterBlocks);
        }
    }

    /// <summary>v1.1.37 — Read the Block Load Report state the driver
    /// populated synchronously inside its SetFeature(0x11 Create New Effect)
    /// IOCTL handler. The driver picks the EBI from a free-list bitmap and
    /// updates BL fields atomically before completing the IOCTL, so by the
    /// time this consumer's <c>OutputReceived</c> handler fires for the
    /// SetFeature notification (8 ms-ish later via the SDK's poll loop),
    /// the BL state is already canonical. Read it here and wire the EBI
    /// to your effect-tracking dictionary.
    ///
    /// Returns a default-zero <see cref="HMPidBlockLoad"/> if the consumer
    /// hasn't called <see cref="PublishPidPool"/> yet (FFB not enabled —
    /// the shared section doesn't exist).</summary>
    public HMPidBlockLoad GetCurrentPidBlockLoad()
    {
        ThrowIfDisposed();
        lock (_pidStateLock)
        {
            IntPtr view = EnsurePidStateViewLocked();
            if (view == IntPtr.Zero) return default;
            var (ebi, stat, ram) = SharedMemoryIO.ReadPidBlockLoad(view);
            return new HMPidBlockLoad(ebi, stat, ram);
        }
    }

    /// <summary>Legacy / override — manually publish the Block Load
    /// Report state. <b>v1.1.37 made this optional.</b> The driver now
    /// allocates EBIs and writes BL fields synchronously inside its
    /// SetFeature(0x11) IOCTL handler (mirroring vJoy's
    /// <c>Ffb_GetNextFreeEffect</c>), so the canonical pattern is for the
    /// consumer to <i>read</i> the assigned EBI via
    /// <see cref="GetCurrentPidBlockLoad"/> rather than write its own.
    ///
    /// Calling this method overwrites the driver's allocation. Useful only
    /// if the consumer has a reason to mint EBIs itself (specific
    /// reservation policy, mapping back to physical-side handles). Single
    /// slot — most recent publish overwrites.
    ///
    /// <para><b>Note on threading:</b> <c>OutputReceived</c> is delivered on
    /// the SDK's poll thread (~8 ms latency). It is <i>not</i> synchronous
    /// with the kernel SetFeature IOCTL. The pre-1.1.37 doc here was wrong
    /// to suggest otherwise — calling Publish from the handler runs after
    /// dinput8 has already issued its follow-up GetFeature(BlockLoad), so
    /// the publish lands too late to influence that read. The driver-side
    /// allocation in v1.1.37 is what makes the handshake work.</para></summary>
    public void PublishPidBlockLoad(byte effectBlockIndex, PidLoadStatus loadStatus,
                                    ushort ramPoolAvailable)
    {
        ThrowIfDisposed();
        lock (_pidStateLock)
        {
            IntPtr view = EnsurePidStateViewLocked();
            if (view == IntPtr.Zero) return;
            SharedMemoryIO.WritePidBlockLoad(view, ref _pidStateSeqNo,
                effectBlockIndex, (byte)loadStatus, ramPoolAvailable);
        }
    }

    /// <summary>Publish the current PID State Report (HID PID 1.0 §5.8).
    /// Reflects current device state for the most-recently-referenced
    /// effect. Update whenever Effect Operation Start/Stop, Device Reset,
    /// Device Pause, or Actuators Enable/Disable changes the state.</summary>
    /// <param name="effectBlockIndex">Currently active EBI (0 if none).</param>
    /// <param name="flags">Bitfield of <see cref="PidStateFlags"/> reflecting current state.</param>
    public void PublishPidState(byte effectBlockIndex, PidStateFlags flags)
    {
        ThrowIfDisposed();
        lock (_pidStateLock)
        {
            IntPtr view = EnsurePidStateViewLocked();
            if (view == IntPtr.Zero) return;
            SharedMemoryIO.WritePidState(view, ref _pidStateSeqNo,
                effectBlockIndex, (byte)flags);
        }
    }

    // T26-2 — set once at ctor, read every frame in SubmitState. Only Xbox-
    // VID profiles have an XUSB companion (HMXInput.dll) that reads the
    // GIP-format byte slice; for every other profile the bytes are
    // unconditionally unused, so we can skip the per-frame packing AND the
    // 14-byte Marshal.Copy entirely. ~60-80 instructions saved per frame.
    private readonly bool _packsGipBuffer;

    internal HMController(HMContext context, int index, HMProfile profile, string? instanceId)
    {
        _context = context;
        Index = index;
        InstanceId = instanceId;
        Profile = profile;

        // v1.3.0 T10 — cached per-profile builder; same descriptor + same
        // maps produce identical output, so each CreateController for a
        // given profile reuses the same configured builder instead of
        // re-parsing the descriptor on every ctor.
        _reportBuilder = profile.Inner.GetOrBuildReportBuilder();
        _reportBuffer = new byte[_reportBuilder.InputReportByteSize];

        // Only profiles with an XUSB companion (HMXInput.dll) read the
        // GIP-format buffer slice on IOCTL_XUSB_GET_STATE. xinputhid-bound
        // Xbox profiles publish XInput through the upper filter, not the
        // companion, so the GIP slice is unused. Microsoft-VID non-Xbox
        // profiles (SideWinder etc.) don't speak XInput at all. Skip the
        // 14-byte packing for both — gated on the same predicate that
        // controls XUSB companion creation in DeviceOrchestrator.
        _packsGipBuffer = profile.Inner.RequiresXusbCompanion;
        _inputView = SharedMemoryIO.EnsureInputMapping(index);
        _inputEvent = SharedMemoryIO.GetInputEvent(index);

        // v1.3.5 — pre-allocate vendor-blob buffer + encoder state ONLY when
        // the profile actually arms (Sony BT post-handshake). Profiles with
        // extendedReport metadata but no armOn list (every USB Sony profile,
        // every generic profile) never run the codec, so the buffer alloc
        // would be dead memory and the SubmitState hot path would carry an
        // unused extended-write branch. Issue #21 USB jerkiness: keeping
        // this allocation off entirely on USB profiles is the difference
        // between v1.3.4-equivalent hot-path codegen and the regressed path.
        bool armOnDeclared = profile.ExtendedReport?.ArmOn != null
                          && profile.ExtendedReport.ArmOn.Count > 0;
        if (profile.ExtendedReport != null && armOnDeclared)
        {
            _extendedReportBuffer = new byte[profile.ExtendedReport.Size];
            _extEncoderState = new VendorBlobCodec.EncoderState();
        }

        // Output passthrough is best-effort. If the section can't be created
        // (rare — only LocalService permission issues) we just don't raise
        // OutputReceived events.
        try
        {
            _outputView = SharedMemoryIO.EnsureOutputMapping(index);
            _outputThread = new Thread(OutputPollLoop)
            {
                IsBackground = true,
                Name = $"HMOutputReader_{index}",
            };
            _outputThread.Start();
        }
        catch
        {
            _outputView = IntPtr.Zero;
        }
    }

    /// <summary>Push the next input frame to the virtual controller.
    /// The SDK encodes <paramref name="state"/> into the active profile's
    /// HID report layout and publishes it via shared memory.</summary>
    public void SubmitState(in HMGamepadState state)
    {
        ThrowIfDisposed();

        long startTicks = OnSubmitLatencyMicros != null
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // v1.3.9 — single unified state.Axes dict drives every analog input.
        // Resolve the 6 "simple-slot" values (left stick X/Y, right stick
        // X/Y, LT, RT) by looking up each profile's declared sticks/triggers
        // in the axes dict. Auto-default: 0.5 (centered) for sticks, 0.0
        // (released) for triggers.
        var axes = state.Axes;
        double GetAxis(HMAxis ax, double def) =>
            axes != null && axes.TryGetValue(ax, out var v) ? Math.Clamp(v, 0f, 1f) : def;

        var sticks = Profile.Sticks;
        var triggers = Profile.Triggers;
        double mlx = sticks.Count > 0 ? GetAxis(sticks[0].XAxis, 0.5) : 0.5;
        double mly = sticks.Count > 0 ? GetAxis(sticks[0].YAxis, 0.5) : 0.5;
        double mrx = sticks.Count > 1 ? GetAxis(sticks[1].XAxis, 0.5) : 0.5;
        double mry = sticks.Count > 1 ? GetAxis(sticks[1].YAxis, 0.5) : 0.5;
        double mlt = triggers.Count > 0 ? GetAxis(triggers[0].Axis, 0.0) : 0.0;
        double mrt = triggers.Count > 1 ? GetAxis(triggers[1].Axis, 0.0) : 0.0;

        byte[] report;
        // v1.3.5 — vendor-blob path is gated on the host-side arm flag.
        // Sony BT controllers default to legacy short Report 0x01; the host
        // (Steam Input, Chrome's Gamepad API, dualsense-tester, ds.daidr.me)
        // issues a Get_Feature on 0x05 / 0x09 / 0x20 to switch real firmware
        // into vendor-blob mode (Report 0x31 / 0x11). The arm-watcher in
        // OutputPollLoop flips _extendedModeArmed when any of those reads
        // arrives via HidFeatureRead. Until then, fall through to the
        // descriptor-driven BuildReportInto path so joy.cpl, RawInput, and
        // generic HID consumers see structured X/Y/Rx/Ry through Report 0x01.
        // ExtendedReport with a missing armOn list flips this back to "always
        // extended" — used by output-only profiles or test fixtures where
        // arming-on-demand is the wrong default.
        // The codec runs only after the host-side arm-handshake has fired.
        // Profiles without an armOn list (every USB Sony profile, every
        // generic profile) never arm, so they always take the legacy
        // BuildReportInto path — same code path v1.3.4 used. This avoids
        // the per-frame codec cost (field-list walk, CRC compute, byte
        // re-encode) on the 250 Hz SubmitState hot path for profiles that
        // don't need vendor-blob input emission. Bug #21: pre-v1.3.5 USB
        // profiles had no extendedReport at all and this gate didn't apply;
        // 0cec81d added extendedReport metadata to USB Sony profiles for
        // PadForge's bidirectional decode, which silently flipped USB onto
        // the codec path even though USB doesn't need vendor-blob input
        // (its descriptor already declares structured X/Y/Rx/Ry usages
        // that joy.cpl, dinput, and the test app's parsers all decode
        // correctly). Restoring the v1.3.4 path for USB removes the
        // regression.
        bool useExtended = Profile.ExtendedReport != null
                        && _extendedReportBuffer != null
                        && _extEncoderState != null
                        && _extendedModeArmed;

        if (useExtended)
        {
            // Profile.ExtendedReport's field list drives byte placement.
            // Sticks / triggers / buttons / hat encode through
            // VendorBlobCodec; CRC32 (if declared) is computed last. For
            // Sony BT, the buffer is full 78 bytes including byte[0] = RID
            // (0x31 / 0x11), so the driver must NOT prepend its own RID.
            // The driver-side WriteToInputReport recognizes the extended
            // path via the SHARED_INPUT.ExtendedReportSize > 0 hint set
            // alongside the legacy bytes below.
            VendorBlobCodec.EncodeInput(Profile.ExtendedReport!, in state,
                (float)mlx, (float)mly, (float)mrx, (float)mry, (float)mlt, (float)mrt,
                _extendedReportBuffer!, _extEncoderState!);
            report = _extendedReportBuffer!;
        }
        else
        {
            // v1.3.9 — unified axes dict drives every declared analog input.
            // Hat priority chain (HatDegrees > HatHundredths > HatRaw > Hat)
            // picks the first non-null and ignores the rest.
            _reportBuilder.BuildReportInto(_reportBuffer,
                axes: state.Axes,
                hatValue: (int)state.Hat,
                buttonMask: (uint)state.Buttons,
                hatDegrees: state.HatDegrees,
                hatHundredths: state.HatHundredths,
                hatRaw: state.HatRaw);

            // v1.3.5 — overlay profile-declared fixed bytes (e.g. DS5 Edge
            // USB activeProfile = 0x80 at byte 49 so dualsense-tester's
            // useInNormalMode check `byte && (byte & 3) === 0` succeeds —
            // see profiles/sony/dualsense-edge.json inputDefaults). Codec
            // path doesn't need this: it walks ExtendedReport.fields which
            // already lists these as uint8 entries with `initial` values,
            // so the constants participate in CRC32 computation. Legacy
            // path has no CRC, so a post-encode overlay is fine.
            var inputDefaults = Profile.Inner.InputDefaults;
            if (inputDefaults != null)
            {
                int len = _reportBuffer.Length;
                foreach (var p in inputDefaults)
                {
                    if ((uint)p.Byte < (uint)len)
                        _reportBuffer[p.Byte] = (byte)p.Value;
                }
            }

            report = _reportBuffer;
        }

        // T26-2 — pack the GIP-format buffer ONLY for Xbox-VID profiles.
        // The XUSB companion (HMXInput.dll) reads this slice on
        // IOCTL_XUSB_GET_STATE; non-Xbox profiles have no XUSB companion
        // bound, so the bytes are unused — skip the per-frame packing
        // entirely (~60-80 instructions saved). The downstream Marshal.Copy
        // is also skipped via the gipData=null path in WriteInputFrame.
        if (_packsGipBuffer)
        {
            ushort gipLx = (ushort)(mlx * 65535);
            ushort gipLy = (ushort)(mly * 65535);
            ushort gipRx = (ushort)(mrx * 65535);
            ushort gipRy = (ushort)(mry * 65535);
            ushort gipLt = (ushort)(mlt * 1023);
            ushort gipRt = (ushort)(mrt * 1023);
            _gipBuf[0]  = (byte)(gipLx & 0xFF); _gipBuf[1]  = (byte)(gipLx >> 8);
            _gipBuf[2]  = (byte)(gipLy & 0xFF); _gipBuf[3]  = (byte)(gipLy >> 8);
            _gipBuf[4]  = (byte)(gipRx & 0xFF); _gipBuf[5]  = (byte)(gipRx >> 8);
            _gipBuf[6]  = (byte)(gipRy & 0xFF); _gipBuf[7]  = (byte)(gipRy >> 8);
            _gipBuf[8]  = (byte)(gipLt & 0xFF); _gipBuf[9]  = (byte)(gipLt >> 8);
            _gipBuf[10] = (byte)(gipRt & 0xFF); _gipBuf[11] = (byte)(gipRt >> 8);
            // Button low byte: A,B,X,Y,LB,RB,LS,RS (XInput XUSB convention)
            uint b = (uint)state.Buttons;
            byte btnLow = 0;
            if ((b & (uint)HMButton.A)           != 0) btnLow |= 0x01;
            if ((b & (uint)HMButton.B)           != 0) btnLow |= 0x02;
            if ((b & (uint)HMButton.X)           != 0) btnLow |= 0x04;
            if ((b & (uint)HMButton.Y)           != 0) btnLow |= 0x08;
            if ((b & (uint)HMButton.LeftBumper)  != 0) btnLow |= 0x10;
            if ((b & (uint)HMButton.RightBumper) != 0) btnLow |= 0x20;
            if ((b & (uint)HMButton.LeftStick)   != 0) btnLow |= 0x40;
            if ((b & (uint)HMButton.RightStick)  != 0) btnLow |= 0x80;
            _gipBuf[12] = btnLow;
            // Button high byte. Bits 0..1 are Back/Start, bits 2..5 carry the
            // 4-bit hat — companion.c does (btnHigh >> 2) & 0x0F and switches
            // the result into wButtons.DPAD_* (companion.c:421-426). Guide
            // sits above the hat at bit 6 (0x40); HMXInput.dll's
            // IOCTL_XUSB_GET_STATE handler translates 0x40 to the undocumented
            // XINPUT_GAMEPAD_GUIDE bit (0x0400) returned by XInputGetStateEx.
            // Pre-v1.3.3 the hat bits were never written, so XInput consumers
            // hitting xusb22 directly (SDL3 XInput backend, sample-quality
            // XInput apps) saw no d-pad on Xbox 360 wired (#19). HID-derived
            // consumers (joy.cpl/DI, SDL3-HID, browsers via WGI) were
            // unaffected because BuildReportInto correctly populates the
            // descriptor's Hat Switch usage. Mask against 0x0F so a future
            // HMHat extension can't smear into Back/Start bits below.
            byte btnHigh = 0;
            if ((b & (uint)HMButton.Back)  != 0) btnHigh |= 0x01;
            if ((b & (uint)HMButton.Start) != 0) btnHigh |= 0x02;
            btnHigh |= (byte)(((byte)state.Hat & 0x0F) << 2);
            if ((b & (uint)HMButton.Guide) != 0) btnHigh |= 0x40;
            _gipBuf[13] = btnHigh;
        }

        // v1.3.5 — two write paths, mutually exclusive per frame:
        //
        //  • Legacy (default, _extendedModeArmed=false or no ExtendedReport):
        //    SDK strips the Report ID byte at position 0 and the driver
        //    re-prepends ctx->FirstInputReportId. Result: the descriptor's
        //    first declared input report ID arrives at the kernel HID stack
        //    (e.g. Report 0x01 for Sony BT). joy.cpl, RawInput, and generic
        //    HID consumers see structured X/Y/Rx/Ry per the legacy descriptor.
        //
        //  • Extended (post-arm, useExtended=true): SDK passes the full
        //    RID-included buffer (e.g. 78-byte Sony BT Report 0x31 with
        //    CRC32 trailer) via WriteInputFrame's extendedData parameter.
        //    Driver emits ExtendedReportData verbatim (no RID prepend).
        //    Steam Input, dualsense-tester, ds.daidr.me, and Chrome's
        //    Gamepad API decode the vendor-blob format. joy.cpl loses
        //    sticks in this state — same as real Sony hardware behavior
        //    once Steam runs and switches the controller to extended mode.
        //
        // dataLen capped at SharedMemoryIO.DATA_CAPACITY (256 bytes; widened
        // from 64 in 2026-04-23). T26-2 — pass null for gipData on non-Xbox
        // profiles so WriteInputFrame skips the 14-byte Marshal.Copy.
        if (useExtended)
        {
            int extLen = Profile.ExtendedReport!.Size;
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo,
                Array.Empty<byte>(), 0,
                _packsGipBuffer ? _gipBuf : null,
                dataOffset: 0,
                extendedData: report, extendedLen: extLen);
        }
        else
        {
            int dataStart = _reportBuilder.InputReportId != 0 ? 1 : 0;
            int dataLen = Math.Min(report.Length - dataStart, SharedMemoryIO.DATA_CAPACITY);
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo, report, dataLen,
                _packsGipBuffer ? _gipBuf : null, dataStart);
        }

        if (OnSubmitLatencyMicros != null)
        {
            long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
            long micros = elapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            OnSubmitLatencyMicros(micros);
        }
    }

    /// <summary>Push a raw HID input report for features that
    /// <see cref="HMGamepadState"/> doesn't model — touchpad coordinates,
    /// gyroscope, sensor packets, vendor extensions.
    ///
    /// <para>Pass <b>data bytes only</b> — do NOT include a Report ID prefix.
    /// The driver prepends the Report ID automatically (same as
    /// <see cref="SubmitState"/>). For a DualSense with Report ID 0x01 and
    /// 64-byte InputReportByteLength, pass 63 bytes of data.</para>
    ///
    /// <para>For profiles with no Report ID (e.g. Xbox Series BT), pass the
    /// full report as-is.</para>
    ///
    /// <para>Tip: use <see cref="HMProfile.InputReportSize"/> and
    /// <see cref="HMProfile.GetDescriptorBytes"/> to determine the expected
    /// data layout. The test app's <c>info</c> command shows every field's
    /// bit offset.</para>
    /// </summary>
    public void SubmitRawReport(ReadOnlySpan<byte> report)
    {
        ThrowIfDisposed();
        if (report.Length == 0) throw new ArgumentException("Report cannot be empty.", nameof(report));
        if (report.Length > SharedMemoryIO.DATA_CAPACITY)
            throw new ArgumentException(
                $"Report length {report.Length} exceeds the {SharedMemoryIO.DATA_CAPACITY}-byte shared section payload.",
                nameof(report));

        // v1.3.0 — copy into the per-controller reusable buffer instead of
        // report.ToArray()'ing per call. Vendor-protocol consumers (PadForge
        // DualSense path, etc.) hit this path at the same rate as
        // SubmitState; the alloc-per-call cost was visible.
        report.CopyTo(_rawReportBuffer.AsSpan());

        // v1.3.5 — overlay profile-declared fixed bytes. Note that
        // SubmitRawReport's `report` arg is DATA-ONLY (no report ID byte
        // prepended); inputDefaults entries are JSON-keyed by ON-WIRE byte
        // (where byte 0 is the report ID), so we subtract 1 to land in
        // the data-buffer coordinate system. PadForge's USB DS5 raw packers
        // build the standard Sony layout but don't know about Edge-specific
        // status bytes (activeProfile at struct[48]); without overlaying
        // here, SubmitRawReport clobbers whatever SubmitState wrote a few
        // microseconds earlier.
        var rawDefaults = Profile.Inner.InputDefaults;
        if (rawDefaults != null)
        {
            int len = Math.Min(report.Length, _rawReportBuffer.Length);
            byte rid = (byte)(_reportBuilder.InputReportId);
            int dataShift = rid != 0 ? 1 : 0;
            foreach (var p in rawDefaults)
            {
                int idx = p.Byte - dataShift;
                if ((uint)idx < (uint)len)
                    _rawReportBuffer[idx] = (byte)p.Value;
            }
        }
        // Raw mode reuses the GIP buffer at whatever state SubmitState last
        // left it in (or zero if SubmitState was never called) — raw consumers
        // are expected to also call SubmitState if they need GIP/XInput.
        // T30-2 — pass null for gipData on non-Xbox profiles, same logic as
        // SubmitState's Xbox-only GIP packing. Saves the 14-byte Marshal.Copy
        // per raw frame on DualSense / Switch Pro / generic gamepad paths.
        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo, _rawReportBuffer, report.Length,
            _packsGipBuffer ? _gipBuf : null);
    }

    /// <summary>v1.3.5 — instance-level <see cref="HMOutputEncoder.Encode"/>
    /// that threads per-controller rolling-counter state through the codec.
    ///
    /// <para>Required for DS5 BT effect output: the spec's <c>btTag</c> field
    /// is a stride-16 rolling counter, and real Sony firmware drops the
    /// effect packet if consecutive writes don't carry the next tag value.
    /// The static <see cref="HMOutputEncoder.Encode"/> overload is stateless
    /// and falls back to <c>initial</c>; use this method instead so the
    /// SDK owns the increment.</para>
    ///
    /// <para>Per-controller — multiple virtuals never share counter state.
    /// The internal lock makes this safe to call from any thread.</para>
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> if the profile
    /// has no <c>extendedOutputReport</c> spec.</para></summary>
    public byte[] EncodeOutput(IReadOnlyDictionary<string, object> fields)
    {
        ThrowIfDisposed();
        if (fields == null) throw new ArgumentNullException(nameof(fields));

        var spec = Profile.ExtendedOutputReport;
        if (spec == null)
            throw new InvalidOperationException(
                $"Profile '{Profile.Id}' has no extendedOutputReport spec — nothing to encode against.");

        lock (_outputEncoderStateLock)
        {
            _outputEncoderState ??= new VendorBlobCodec.EncoderState();
            return VendorBlobCodec.EncodeOutput(spec, fields, _outputEncoderState);
        }
    }

    /// <summary>Background polling loop that reads from the per-controller
    /// output shared section and raises <see cref="OutputReceived"/> for
    /// each new packet. Sleeps 8 ms between polls (≈125 Hz) which is
    /// comfortably above the rate at which any host app sends output
    /// packets and well below the cost threshold for an idle thread.</summary>
    private void OutputPollLoop()
    {
        if (_outputView == IntPtr.Zero) return;
        // Initialize lastSeq to the current Head so any pre-existing ring
        // contents (stale or legitimate) never fire a spurious
        // OutputReceived for the prior session's data.
        uint lastSeq = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(_outputView, 0);
        byte[] buf = new byte[256];
        var ct = _outputCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // v1.1.40 — drain the ring on every poll. pid.dll writes
                // Set Effect → Set Constant Force → Effect Operation Start
                // within 1-3 ms; the pre-1.1.40 single-slot channel was
                // coalescing those bursts vs the 8 ms poll cadence and
                // losing the middle (magnitude) packet.
                while (SharedMemoryIO.TryReadOutputFrame(_outputView, ref lastSeq,
                        out byte source, out byte reportId, out int dataSize, buf))
                {
                    var data = new ReadOnlyMemory<byte>(buf, 0, dataSize);
                    var pkt = new HMOutputPacket((HMOutputSource)source, reportId, data, lastSeq);
                    OutputReceived?.Invoke(this, pkt);

                    // v1.3.5 — vendor-blob output decode. When the profile
                    // declares an extendedOutputReport with a matching
                    // reportId, decode the bytes into a parsed-field
                    // dictionary and surface as OutputDecoded. Consumers
                    // get named values (rumble amplitudes, lightbar RGB,
                    // adaptive-trigger blocks) instead of raw bytes.
                    var extOut = Profile.ExtendedOutputReport;
                    if (extOut != null && reportId == extOut.ReportIdByte
                        && OutputDecoded != null)
                    {
                        try
                        {
                            // Reconstruct the full report (RID + data) for
                            // the codec — VendorBlobCodec expects the RID
                            // at offset 0. The shared output ring stores
                            // the RID separately so we synthesize it here.
                            var full = new byte[dataSize + 1];
                            full[0] = reportId;
                            Buffer.BlockCopy(buf, 0, full, 1, dataSize);

                            var (fields, crcValid) = VendorBlobCodec.Decode(extOut, full);
                            OutputDecoded.Invoke(this, new HMOutputDecodedEventArgs
                            {
                                ReportId = reportId,
                                Fields = fields,
                                RawBytes = full,
                                CrcValid = crcValid,
                            });
                        }
                        catch
                        {
                            // Swallow decode errors so a malformed packet
                            // doesn't kill the polling thread. OutputReceived
                            // already fired with the raw bytes; consumers
                            // that need them have them.
                        }
                    }

                    // v1.3.5 — arm-handshake watcher. When the profile
                    // declares armOn triggers and a matching host action
                    // arrives, flip the armed flag — SubmitState then
                    // switches from legacy Report 0x01 emission to
                    // vendor-blob Report 0x31 / 0x11 emission via the
                    // extended shared-memory path (see SubmitState's
                    // useExtended branch). Sony BT profiles arm on
                    // Get_Feature 0x05 / 0x09 / 0x20 reads — the same
                    // handshake real Sony firmware uses to switch from
                    // basic to extended mode (ref: Linux hid-playstation
                    // dualsense_create init flow). featureWrite and
                    // outputWrite trigger types stay supported for
                    // future profiles that arm on writes (e.g. Switch
                    // Pro init handshake).
                    var extIn = Profile.ExtendedReport;
                    if (extIn?.ArmOn != null && !_extendedModeArmed)
                    {
                        bool isFeature     = source == (byte)HMOutputSource.HidFeature;
                        bool isOutput      = source == (byte)HMOutputSource.HidOutput;
                        bool isFeatureRead = source == (byte)HMOutputSource.HidFeatureRead;
                        foreach (var trig in extIn.ArmOn)
                        {
                            if ((trig.Type == "featureWrite" && isFeature     && trig.ReportIdByte == reportId)
                             || (trig.Type == "outputWrite"  && isOutput      && trig.ReportIdByte == reportId)
                             || (trig.Type == "featureRead"  && isFeatureRead && trig.ReportIdByte == reportId))
                            {
                                _extendedModeArmed = true;
                                break;
                            }
                        }
                    }

                    if (ct.IsCancellationRequested) break;
                }
            }
            catch
            {
                // Swallow polling errors so a transient kernel-side failure
                // doesn't kill the reader thread.
            }
            // T10 — wait on the CTS WaitHandle with an 8 ms timeout instead
            // of Thread.Sleep(8). Cancel returns nearly immediately (kernel
            // SetEvent on the cancel handle) instead of waiting up to 8 ms
            // for the next sleep slice to expire. Net Dispose latency drops
            // from up-to-8 ms to under 1 ms per controller — small but real
            // for callers that batch-dispose many controllers in series.
            try { ct.WaitHandle.WaitOne(8); } catch { break; }
        }
    }

    private bool _disposed;
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMController));
    }

    /// <summary>Removes the virtual device from PnP and frees the per-controller
    /// shared memory section. Idempotent — safe to call multiple times. Called
    /// automatically when the owning <see cref="HMContext"/> is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _outputCts.Cancel(); } catch { }
        try { _outputThread?.Join(Internal.TimeoutScale.Apply(500)); } catch { }
        try { _outputCts.Dispose(); } catch { }
        _context.OnControllerDisposing(this);
    }
}
