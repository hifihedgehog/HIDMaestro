using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// A live virtual controller. Created by <see cref="HMContext.CreateController"/>;
/// dispose to remove the device. The controller exposes two channels:
///
/// <para><b>Input</b> (host → game): the consumer pushes <see cref="HMGamepadState"/>
/// frames via <see cref="SubmitState"/> at whatever rate they want: typically the
/// rate of their real input source. The SDK translates the abstract state into the
/// profile's native HID descriptor format and writes it to a shared section that
/// the kernel-side driver reads at ~250 Hz. There is no internal pumping thread;
/// the consumer drives the cadence.</para>
///
/// <para><b>Output</b> (game → host): the SDK runs a background reader thread that
/// captures rumble / haptics / FFB / LED commands from any host application and
/// raises <see cref="OutputReceived"/>. Since issue #34 the thread blocks on a
/// driver-signaled event (falling back to an 8 ms poll against older drivers).
/// Handlers run on the reader thread, not the consumer's UI thread. Implement
/// your handler accordingly.</para>
/// </summary>
public sealed partial class HMController : IDisposable
{
    private readonly HMContext _context;
    /// <summary>The machine-wide transport index: every section, event,
    /// pipe and registry key of this controller is named with it, and the
    /// driver reads it. Internal code says Index for it.</summary>
    internal int Index { get; }
    /// <summary>The slot within the owning context, which
    /// <see cref="HMContext.CreateControllerAt(int, HMProfile)"/> names.</summary>
    internal int Slot { get; }
    internal string? InstanceId { get; }
    internal Internal.ControllerLease? Lease { get; }

    /// <summary>The machine-wide index this controller's shared sections,
    /// events, owner pipe and registry key are named with, and the
    /// <c>ControllerIndex</c> the driver reads. A program running alone gets
    /// the slot number here, as every release before 1.13.0 did. When another
    /// program on the machine already uses that index, the controller gets the
    /// lowest free one instead, so the value can differ between runs. Native
    /// writers that open the sections themselves read the names from
    /// <see cref="SectionNames"/>.</summary>
    public int TransportIndex => Index;

    /// <summary>The kernel object names this controller's shared memory
    /// contract uses, in the order the input path creates them:
    /// <c>Global\HIDMaestroInput&lt;N&gt;</c>, <c>Global\HIDMaestroInputEvent&lt;N&gt;</c>,
    /// <c>Global\HIDMaestroCompanionInputEvent&lt;N&gt;</c>,
    /// <c>Global\HIDMaestroInputAck&lt;N&gt;</c>, <c>Global\HIDMaestroInputAckEvent&lt;N&gt;</c>,
    /// <c>Global\HIDMaestroOutput&lt;N&gt;</c>, <c>Global\HIDMaestroOutputEvent&lt;N&gt;</c>,
    /// <c>Global\HIDMaestroPidState&lt;N&gt;</c>, <c>Global\HIDMaestroVersion&lt;N&gt;</c>
    /// and the owner pipe <c>\\.\pipe\HIDMaestroOwner&lt;N&gt;</c>, with N the
    /// <see cref="TransportIndex"/>.</summary>
    public IReadOnlyList<string> SectionNames => Internal.SharedMemoryIO.ObjectNames(Index);

    /// <summary>The version of the HIDMaestro driver this controller runs
    /// on, as the driver reports it in <c>Global\HIDMaestroVersion&lt;N&gt;</c>.
    /// Null until the driver has started the device, on a driver older than
    /// 1.13.0, which does not report one, and on a composite persona, which
    /// runs on the USB/IP transport instead of the HIDMaestro driver.</summary>
    public Version? DriverVersion => Internal.SharedMemoryIO.DriverVersion(Index)?.Version;

    /// <summary>The identity key this controller was created with. Every
    /// devnode instance id, the container id and, for USB/IP personas, the
    /// USB serial derive from it, so creating a controller with the same
    /// key again brings back the same device paths (issue #60). A caller
    /// that passed no key sees <c>index:&lt;N&gt;</c> here, and passing that
    /// string back in recreates the same default identity.</summary>
    public string IdentityKey { get; }
    public HMProfile Profile { get; }

    // Issue #39: non-null when this controller runs on the USB/IP
    // backend instead of the UMDF2 driver. The backend's device emulator
    // speaks the SAME shared-memory contract the driver does (it reads the
    // input section this class writes and publishes to the output ring
    // this class's poll loop reads), which is why nothing else in this
    // class changes per backend. Dispose routing in HMContext keys on it.
    internal Internal.Usbip.UsbipBackendHandle? UsbipHandle { get; }

    /// <summary>Issue #39: the audio surfaces of a composite USB persona
    /// (speaker/haptic PCM out, microphone in, volume/mute control
    /// events). Null on every UMDF2 profile; non-null exactly when
    /// <see cref="HMProfile.RequiresUsbipBackend"/> and the controller
    /// was created through the USB/IP backend.</summary>
    public HMUsbAudio? UsbAudio { get; }

    // Encoder built once from the profile descriptor at construction time;
    // SubmitState reuses it for every frame.
    private readonly HidReportBuilder _reportBuilder;
    private readonly IntPtr _inputView;
    // Named auto-reset event signaled by WriteInputFrame so the driver's
    // worker thread can wake immediately instead of busy-polling. Cached at
    // construction time alongside the view pointer.
    private readonly IntPtr _inputEvent;
    private uint _inputSeqNo;

    // Output passthrough reader (rumble/haptics/FFB): background thread
    // poll-reads the per-controller output section and raises OutputReceived.
    private readonly IntPtr _outputView;
    private readonly Thread? _outputThread;
    private readonly CancellationTokenSource _outputCts = new();

    // 14-byte GIP-format buffer reused per frame to avoid per-call alloc.
    // The XUSB companion (HMXInput.dll, used for non-xinputhid Xbox
    // profiles like Xbox 360 wired) reads ONLY this slice from shared
    // memory when servicing IOCTL_XUSB_GET_STATE: it does not read the
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

    // v1.3.0: per-controller reusable HID input report buffer. SubmitState
    // calls BuildReportInto(_reportBuffer, ...) instead of BuildReport which
    // allocates a fresh byte[] each frame. At 250 Hz × N controllers the
    // alloc churn was real GC pressure; reusing avoids it entirely.
    // Sized at HidReportBuilder.InputReportByteSize, computed in the ctor.
    private readonly byte[] _reportBuffer;

    // v1.3.0: per-controller reusable raw report buffer. SubmitRawReport
    // (DualSense / vendor-protocol path) used to do report.ToArray() per
    // call. This buffer absorbs the copy without the alloc churn. Sized to
    // the shared section's payload, the most either raw path accepts: at 64
    // bytes a 78-byte Sony Bluetooth frame threw inside the copy.
    private readonly byte[] _rawReportBuffer = new byte[SharedMemoryIO.DATA_CAPACITY];

    // Issue #67: keyboards and mice. The packer writes SubmitMouse /
    // SubmitKeyboard / SubmitConsumer into the profile's own reports.
    // _deliverEvery holds each frame until the driver has handed the
    // previous one to HID class (SharedMemoryIO.WaitInputDelivered), so a
    // click's press, a key tap and every relative delta arrive. Gamepads
    // keep latest-wins, which is right for state. Guarded by _submitLock.
    private KeyboardMousePacker? _kbm;
    private readonly bool _deliverEvery;
    private uint _deliverySeq;
    private bool _deliveryPending;
    private int _deliveryMisses;
    private uint _lastExtraButtons;
    private bool _extraButtonsSent;
    private const int DeliveryTimeoutMs = 200;
    // One report per millisecond at most, the fastest a full-speed USB
    // mouse or keyboard reports (bInterval 1). The acknowledgement says a
    // report reached HID class. HID class keeps a short queue per reader,
    // and a burst faster than mouhid and kbdhid drain it overflows that
    // queue no matter what the driver does.
    private long _lastFrameTicks;
    private static readonly long s_minFrameTicks = System.Diagnostics.Stopwatch.Frequency / 1000;

    /// <summary>Raised on the SDK's output-polling thread whenever a host
    /// application sends a rumble, haptic, FFB, feature, or LED command to
    /// this virtual controller. Subscribers must be thread-safe.
    ///
    /// <para><b>Cadence and ordering:</b> the reader wakes on the driver's
    /// per-packet event signal (issue #34; 8 ms poll fallback against
    /// older drivers) and drains every slot written since the last wake,
    /// in monotonic SeqNo order. Multiple <c>OutputReceived</c> invocations
    /// per wake are normal. DirectInput PID FFB writes 3 packets in
    /// 1-3 ms (Set Effect → Set Constant Force → Effect Operation Start)
    /// and all three surface here.</para>
    ///
    /// <para><b>Ring depth:</b> 64 slots × 256-byte payload. If the
    /// consumer's handler stalls for &gt; 512 ms while the driver is
    /// writing at burst rate, the oldest packets get overwritten
    /// keep the handler cheap (no synchronous I/O, no long locks).
    /// Pre-1.1.40 was a single-slot channel that silently coalesced
    /// back-to-back writes; that drop pattern is fixed.</para></summary>
    public event Action<HMController, HMOutputPacket>? OutputReceived;

    /// <summary>v1.3.5: raised when an inbound output report matches the
    /// profile's <see cref="HMProfile.HasExtendedOutput"/> spec. The SDK
    /// decodes the bytes per the profile's <c>extendedOutputReport</c> field
    /// list and surfaces parsed values (rumble amplitudes, lightbar RGB,
    /// adaptive-trigger blocks, etc.) keyed by semantic name.
    ///
    /// <para>Consumers that want raw bytes still get them via
    /// <see cref="OutputReceived"/>: both events fire for matching reports.
    /// Subscribers must be thread-safe (raised on the polling thread).</para></summary>
    public event EventHandler<HMOutputDecodedEventArgs>? OutputDecoded;

    // v1.3.5: vendor-blob input encoder state. Built lazily when the profile
    // declares extendedReport. Holds rolling counters (Sony's framingTag /
    // reportCounter increment monotonically across SubmitState calls).
    private VendorBlobCodec.EncoderState? _extEncoderState;

    // v1.3.5: vendor-blob output encoder state. Allocated lazily on the
    // first EncodeOutput call so consumers that never call it (input-only
    // virtuals, output-via-OnOutputReceived consumers) skip the dictionary
    // alloc. Holds rolling counters for output direction: Sony BT effect
    // output's btTag increments stride-16 per write or real firmware drops
    // the packet.
    private VendorBlobCodec.EncoderState? _outputEncoderState;
    private readonly object _outputEncoderStateLock = new();

    // v1.3.5: buffer sized to ExtendedReport.Size, allocated once. NULL
    // when the profile has no extendedReport.
    private byte[]? _extendedReportBuffer;

    // v1.3.5: host-side arm flag. False until a host write matches one of
    // ExtendedReport.armOn triggers; true thereafter for the lifetime of
    // this controller. Until armed, SubmitState falls through to the
    // descriptor-driven BuildReportInto path so consumers that never issue
    // the handshake still see legacy Report 1 emission.
    private volatile bool _extendedModeArmed;

    // Issue #58. Resolved once: whether SubmitRawReport must emit verbatim
    // rather than letting a report id be prepended by position, and which id
    // a data-only frame should carry.
    private readonly bool _rawEmitsVerbatim;
    private readonly byte _rawVerbatimReportId;

    // Idle streaming (issue #56). A device that only writes on change is
    // invisible to any consumer that probes it before touching it, and
    // Valve's drivers do exactly that. These hold the consumer's last frame
    // so the pump can re-publish it at the profile's declared cadence.
    //
    // Every submit path records its frame under _submitLock, and the pump
    // repeats under the same lock. A raw consumer's frame is repeated as
    // its own bytes, never as a default state, and a repeat can never
    // interleave its shared-section write with a consumer's.
    // _lastSubmitTimestamp is Stopwatch ticks: Environment.TickCount64
    // moves in 15.6 ms steps, too coarse for a 4 ms cadence.
    private readonly object _submitLock = new();
    private HMGamepadState _lastSubmitted;
    private long _lastSubmitTimestamp;
    private bool _lastSubmitWasRaw;
    private bool _lastRawWasExtended;
    private byte[]? _lastRawFrame;
    private int _lastRawLength;
    private Thread? _idleThread;
    private readonly CancellationTokenSource _idleCts = new();

    // Switch Pro protocol (issue #33): wire-format packer state. See
    // SwitchProPacker + the driver-side responder in driver.c.
    private bool _switchProtocol;
    private byte[]? _switchBodyBuffer;

    // Switch 2 Pro protocol (issue #66): the composite persona, 057E:2069
    // on the USB/IP backend. SubmitState packs a state body and the
    // backend's Switch2ProDevice builds every report from it on its own
    // 4 ms clock. See Switch2ProPacker.
    private readonly bool _switch2Protocol;
    private readonly byte[]? _switch2BodyBuffer;

    /// <summary>v1.13.0: the controller ports of a device that carries
    /// several controllers in one report, as the profile's
    /// <c>extendedReport.ports</c> declares them. Empty for a profile with
    /// one controller. On a device with ports, <see cref="SubmitState"/>
    /// submits to port 1.</summary>
    public HMPortSet Ports { get; }

    /// <summary>v1.13.0: the protocol of a USB/IP device whose profile
    /// declares <c>usbConfiguration.protocol</c>, else null.</summary>
    public HMDeviceProtocol? DeviceProtocol { get; }

    // Cached layout projections (audit 1n): Profile.Sticks / Profile.Triggers
    // allocate on every access, so snapshot them once in the ctor and read
    // the cached lists on the SubmitState hot path. The profile's layout is
    // immutable for the controller's lifetime.
    private readonly System.Collections.Generic.IReadOnlyList<HMSimpleStick> _cachedSticks;
    private readonly System.Collections.Generic.IReadOnlyList<HMSimpleTrigger> _cachedTriggers;

    // Companion input doorbell (perf audit 2026-07-21): signaled per
    // GIP-carrying frame so the XUSB companion can complete a parked
    // WAIT_FOR_INPUT at frame arrival instead of its 8 ms timer tick.
    // IntPtr.Zero for non-Xbox profiles.
    private readonly IntPtr _companionInputEvent;

    // Canonical trigger positions, resolved once (issue #34). The axisMap
    // walk (case-insensitive role compare + hex key parse) ran inside
    // every SubmitState, twice, for values that are constant per profile.
    private readonly HMAxis _canonicalLt;
    private readonly HMAxis _canonicalRt;

    /// <summary>Optional diagnostic: invoked at the end of every successful
    /// <see cref="SubmitState"/> with the elapsed microseconds. Wire this
    /// when investigating per-frame submit latency (e.g. issue #21 USB
    /// stalls). Called inline on the caller's thread; keep the handler
    /// short: log to a ring buffer or counter, don't block.</summary>
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
            // v1.3.7: write the profile descriptor's PID Report ID
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
    /// First call enables FFB on this controller: until called at least once,
    /// HidD_GetFeature on the Pool Report ID returns STATUS_NO_SUCH_DEVICE
    /// (matching vJoy's "FFB not enabled" convention), so DInput cleanly
    /// concludes "device exists but no FFB" rather than retrying.
    /// Subsequent calls update the pool state.
    ///
    /// <para><b>Descriptor requirements:</b> the controller's HID descriptor
    /// must declare the PID FFB report block. Use
    /// <see cref="HidDescriptorBuilder.AddPidFfbBlock"/>: that method emits
    /// the canonical "minimum viable" block (Set Effect 0x11, Set Constant
    /// Force 0x15, Effect Operation 0x1A, Device Control 0x1C, etc., plus
    /// the single Feature report Create New Effect 0x11). Do NOT add
    /// additional Feature reports (0x12 Block Load, 0x13 PID Pool, 0x14
    /// PID State) inside the same Application Collection: the four-feature
    /// variant from vJoy's reference descriptor causes pid.dll to AV inside
    /// PID_EffectOperation+0x52 the first time the consumer calls
    /// CreateEffect (DirectX 8-era pid.dll FFB enumeration bug, not
    /// OS-build-gated; verified empirically on Windows 11 26100: issue #16).
    /// Pool, Block Load, and PID State are served by the driver from a
    /// separate shared-section path that doesn't touch pid.dll's preparsed-data
    /// parser: that's what <see cref="PublishPidPool"/>, <see cref="PublishPidBlockLoad"/>,
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

    /// <summary>v1.1.37: Read the Block Load Report state the driver
    /// populated synchronously inside its SetFeature(0x11 Create New Effect)
    /// IOCTL handler. The driver picks the EBI from a free-list bitmap and
    /// updates BL fields atomically before completing the IOCTL, so by the
    /// time this consumer's <c>OutputReceived</c> handler fires for the
    /// SetFeature notification (8 ms-ish later via the SDK's poll loop),
    /// the BL state is already canonical. Read it here and wire the EBI
    /// to your effect-tracking dictionary.
    ///
    /// Returns a default-zero <see cref="HMPidBlockLoad"/> if the consumer
    /// hasn't called <see cref="PublishPidPool"/> yet (FFB not enabled
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

    /// <summary>Legacy / override: manually publish the Block Load
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
    /// slot: most recent publish overwrites.
    ///
    /// <para><b>Note on threading:</b> <c>OutputReceived</c> is delivered on
    /// the SDK's poll thread (~8 ms latency). It is <i>not</i> synchronous
    /// with the kernel SetFeature IOCTL. The pre-1.1.37 doc here was wrong
    /// to suggest otherwise: calling Publish from the handler runs after
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

    // T26-2: set once at ctor, read every frame in SubmitState. Only Xbox-
    // VID profiles have an XUSB companion (HMXInput.dll) that reads the
    // GIP-format byte slice; for every other profile the bytes are
    // unconditionally unused, so we can skip the per-frame packing AND the
    // 14-byte Marshal.Copy entirely. ~60-80 instructions saved per frame.
    private readonly bool _packsGipBuffer;

    internal HMController(HMContext context, int slot, Internal.ControllerLease lease, HMProfile profile, string? instanceId,
                          Internal.Usbip.UsbipBackendHandle? usbipHandle = null,
                          string? identityKey = null)
        : this(context, lease.TransportIndex, profile, instanceId, usbipHandle, identityKey)
    {
        Slot = slot;
        Lease = lease;
    }

    private HMController(HMContext context, int index, HMProfile profile, string? instanceId,
                         Internal.Usbip.UsbipBackendHandle? usbipHandle,
                         string? identityKey)
    {
        _context = context;
        Index = index;
        InstanceId = instanceId;
        IdentityKey = identityKey ?? Internal.DeviceIdentity.DefaultKey(index);
        Profile = profile;
        UsbipHandle = usbipHandle;
        if (usbipHandle != null)
            UsbAudio = new HMUsbAudio(profile.Inner, usbipHandle.Device);

        // v1.3.0 T10: cached per-profile builder; same descriptor + same
        // maps produce identical output, so each CreateController for a
        // given profile reuses the same configured builder instead of
        // re-parsing the descriptor on every ctor.
        _reportBuilder = profile.Inner.GetOrBuildReportBuilder();
        _reportBuffer = new byte[_reportBuilder.InputReportByteSize];

        // Snapshot the layout projections once (audit 1n): see the field
        // declarations. Profile.Sticks/Triggers allocate per access.
        _cachedSticks = profile.Sticks;
        _cachedTriggers = profile.Triggers;

        // Resolve the canonical trigger axes once (issue #34): axisMap-
        // declared override wins, else the Z/Rz defaults PadForge writes.
        _canonicalLt = ResolveCanonicalAxis(profile.Inner.AxisMap, "lefttrigger", HMAxis.Z);
        _canonicalRt = ResolveCanonicalAxis(profile.Inner.AxisMap, "righttrigger", HMAxis.Rz);

        // Only profiles with an XUSB companion (HMXInput.dll) read the
        // GIP-format buffer slice on IOCTL_XUSB_GET_STATE. xinputhid-bound
        // Xbox profiles publish XInput through the upper filter, not the
        // companion, so the GIP slice is unused. Microsoft-VID non-Xbox
        // profiles (SideWinder etc.) don't speak XInput at all. Skip the
        // 14-byte packing for both: gated on the same predicate that
        // controls XUSB companion creation in DeviceOrchestrator.
        _packsGipBuffer = profile.Inner.RequiresXusbCompanion;
        _inputView = SharedMemoryIO.EnsureInputMapping(index);
        _inputEvent = SharedMemoryIO.GetInputEvent(index);
        _companionInputEvent = _packsGipBuffer
            ? SharedMemoryIO.GetCompanionInputEvent(index) : IntPtr.Zero;

        // Issue #67. A keyboard or mouse profile gets every frame delivered,
        // and so does an "other" profile whose descriptor is a mouse or a
        // keyboard, the shape a consumer built before the types existed.
        if (usbipHandle == null && profile.Inner.HasDescriptor)
        {
            var kbm = profile.Inner.GetOrBuildKeyboardMousePacker();
            if (kbm.HasMouse || kbm.HasKeyboard || kbm.HasConsumer) _kbm = kbm;
            _deliverEvery = profile.Inner.IsKeyboardOrMouse
                || (string.Equals(profile.Type, "other", StringComparison.OrdinalIgnoreCase)
                    && (kbm.HasMouse || kbm.HasKeyboard));
        }

        // v1.3.5: pre-allocate vendor-blob buffer + encoder state ONLY when
        // the profile actually arms (Sony BT post-handshake). Profiles with
        // extendedReport metadata but no armOn list (every USB Sony profile,
        // every generic profile) never run the codec, so the buffer alloc
        // would be dead memory and the SubmitState hot path would carry an
        // unused extended-write branch. Issue #21 USB jerkiness: keeping
        // this allocation off entirely on USB profiles is the difference
        // between v1.3.4-equivalent hot-path codegen and the regressed path.
        // alwaysArmed profiles (Switch 2 Pro) skip the handshake entirely:
        // their real firmware streams the vendor report from power-on, and
        // their descriptor's FIRST declared input report is an opaque
        // vendor blob, so the legacy fallback path has no buttons or axes
        // to fill and every consumer would read a live device that never
        // moves.
        bool alwaysArmed = profile.ExtendedReport?.AlwaysArmed == true;
        _rawVerbatimReportId = profile.ExtendedReport?.ReportIdByte ?? 0;
        _rawEmitsVerbatim = alwaysArmed && _rawVerbatimReportId != 0;
        bool armOnDeclared = profile.ExtendedReport?.ArmOn != null
                          && profile.ExtendedReport.ArmOn.Count > 0;
        if (profile.ExtendedReport != null && (armOnDeclared || alwaysArmed))
        {
            _extendedReportBuffer = new byte[profile.ExtendedReport.Size];
            _extEncoderState = new VendorBlobCodec.EncoderState();
            _extendedModeArmed = alwaysArmed;
        }

        // Switch Pro protocol (issue #33): keyed on the Nintendo VID/PID
        // family, mirroring the driver-side responder's gate. Pre-allocate
        // the body buffer only for this family, same rationale as the
        // extended-report buffer above.
        _switchProtocol = SwitchProPacker.IsSwitchPro(profile.VendorId, profile.ProductId);
        if (_switchProtocol)
            _switchBodyBuffer = new byte[SwitchProPacker.BodySize];

        // Switch 2 Pro protocol (issue #66): the same hardcoding, for the
        // persona on the USB/IP backend alone. The UMDF2
        // switch2-pro-controller profile shares the VID/PID and keeps its
        // codec-built report 0x09.
        _switch2Protocol = usbipHandle != null
            && Switch2ProPacker.IsSwitch2Pro(profile.VendorId, profile.ProductId);
        if (_switch2Protocol)
            _switch2BodyBuffer = new byte[Switch2ProPacker.BodySize];

        // v1.13.0: the profile's controller ports, whose report goes out
        // once now so the device starts from it, and its USB/IP protocol.
        Ports = new HMPortSet(this, profile.Inner);
        lock (_submitLock) Ports.PublishInitialLocked();
        if (usbipHandle?.Device.Protocol is { } engine)
            DeviceProtocol = new HMDeviceProtocol(engine);

        // Output passthrough is best-effort. If the section can't be created
        // (rare: only LocalService permission issues) we just don't raise
        // OutputReceived events.
        int idleMs = profile.ExtendedReport?.IdleFrameIntervalMs ?? 0;
        if (idleMs > 0)
        {
            // Publish one frame immediately so the device is already
            // streaming when a driver opens it, then keep the cadence up
            // whenever the consumer goes quiet.
            _lastSubmitted = new HMGamepadState();
            _idleThread = new Thread(() => IdleFrameLoop(idleMs))
            {
                IsBackground = true,
                Name = $"HMIdleFrames_{index}",
                // The same priority as the USB/IP input pump that carries
                // these frames to the wire.
                Priority = ThreadPriority.AboveNormal,
            };
            _idleThread.Start();
        }

        try
        {
            _outputView = SharedMemoryIO.EnsureOutputMapping(index);
            _outputThread = new Thread(OutputPollLoop)
            {
                IsBackground = true,
                Name = $"HMOutputReader_{index}",
            };
            _outputThread.Start();
            // Publish the thread so any unmap stops it first. Without this a
            // sweep that runs while this controller is still alive frees the
            // view underneath the loop below (issue #45).
            SharedMemoryIO.RegisterOutputPump(index, _outputCts, _outputThread);
        }
        catch
        {
            _outputView = IntPtr.Zero;
        }
    }

    // Resolve a canonical trigger axis from a profile's axisMap: the
    // axisMap-declared role position wins, else the given default. Runs
    // ONCE per controller at construction (issue #34). The walk's
    // case-insensitive compares and hex parse used to run inside every
    // SubmitState, twice, for a per-profile constant.
    internal static HMAxis ResolveCanonicalAxis(
        Dictionary<string, string>? axisMap,
        string roleName,
        HMAxis canonicalDefault)
    {
        if (axisMap == null) return canonicalDefault;
        // LAST match wins on duplicate roles (audit of #34): ApplyAxisMap
        // assigns semantic slots last-wins, so canonical resolution must
        // agree or a malformed duplicate-role map would read one axis and
        // write another. Shipped maps declare each role once.
        HMAxis resolved = canonicalDefault;
        foreach (var kvp in axisMap)
        {
            if (kvp.Value == null) continue;
            if (!string.Equals(kvp.Value, roleName, StringComparison.OrdinalIgnoreCase))
                continue;
            ushort usage;
            try { usage = Convert.ToUInt16(kvp.Key, 16); }
            catch { continue; }
            if (usage <= 0xFF) usage |= 0x0100;
            resolved = (HMAxis)usage;
        }
        return resolved;
    }

    // Canonical-first, field-key-fallback trigger resolution. PadForge writes
    // axes[Z]/axes[Rz] via ResolveAxisByRole canonical defaults; the
    // HIDMaestroTest / StandardAxes path writes axes[layout.triggers[N].Axis]
    // (Vx/Vy for the unified xbox-360-* profiles). Both must feed the GIP
    // buffer that drives the XUSB companion's XInput / WGI dispatch. Same
    // resolution rule that HidReportBuilder's combined-Z synthesis uses.
    // The canonical axis arrives pre-resolved (ctor-cached, issue #34).
    internal static double ResolveTrigger(
        Dictionary<HMAxis, float>? axes,
        IReadOnlyList<HMSimpleTrigger> triggers,
        int slot,
        HMAxis canonical)
    {
        if (axes != null)
        {
            if (axes.TryGetValue(canonical, out var vCanon))
                return Math.Clamp(vCanon, 0f, 1f);
            if (slot < triggers.Count && axes.TryGetValue(triggers[slot].Axis, out var vField))
                return Math.Clamp(vField, 0f, 1f);
            // A descriptor with no declared triggers (every Valve state
            // packet, both Switch 2 Pro profiles) has no field to fall back
            // to. The canonical axis above is the whole answer, and it is
            // what StandardAxes writes. Reading Z and Rz here regardless of
            // the axisMap took the Switch 2 Pro's right stick Y, which is
            // Rz, for its right trigger (issue #66).
        }
        return 0.0;
    }

    /// <summary>Push the next input frame to the virtual controller.
    /// The SDK encodes <paramref name="state"/> into the active profile's
    /// HID report layout and publishes it via shared memory.</summary>
    public void SubmitState(in HMGamepadState state)
    {
        ThrowIfDisposed();

        // Every submit path writes the shared section under _submitLock.
        // The section is a single-writer seqlock: two threads writing one
        // controller's frames at once could publish a frame mixed from both.
        lock (_submitLock)
        {
            AwaitPreviousDelivery();
            SubmitStateCore(in state);
            NoteFrameWritten();
            if (_idleThread == null) return;
            // Remembered for the idle pump, and the timestamp is what
            // keeps the pump from competing with a live consumer.
            _lastSubmitted = state;
            _lastSubmitWasRaw = false;
            Volatile.Write(ref _lastSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    private void SubmitStateCore(in HMGamepadState state, bool consumerCall = true)
    {
        // The latency hook times the caller's own submits, so an idle repeat
        // leaves it alone and never runs consumer code under _submitLock.
        var latency = consumerCall ? OnSubmitLatencyMicros : null;
        long startTicks = latency != null
            ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        ResolveSimpleSlots(in state, out double mlx, out double mly, out double mrx,
                           out double mry, out double mlt, out double mrt);

        // A device with controller ports: SubmitState is port 1.
        if (Ports.Count > 0)
        {
            Ports.SubmitLocked(0, in state, direct: false,
                (float)mlx, (float)mly, (float)mrx, (float)mry, (float)mlt, (float)mrt);
            if (latency != null)
            {
                long portElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
                latency(portElapsed * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
            }
            return;
        }

        // Switch Pro protocol path (issue #33): pack the wire-format 0x30
        // body instead of the descriptor-driven build. The descriptor's
        // report 0x30 is a vendor-opaque byte array, so BuildReportInto's
        // semantic packing cannot produce the layout SDL casts the bytes
        // into; SwitchProPacker owns it (buttons/12-bit sticks/IMU). The
        // driver's 60 Hz streamer serves this body with its own timer +
        // battery overlay, and no GIP buffer applies (Nintendo VID has no
        // XUSB companion).
        if (_switchProtocol)
        {
            SwitchProPacker.BuildBody(in state, mlx, mly, mrx, mry, _switchBodyBuffer!);
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo,
                _switchBodyBuffer!, SwitchProPacker.BodySize, null);

            if (latency != null)
            {
                long swElapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
                latency(swElapsed * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
            }
            return;
        }

        // Switch 2 Pro protocol path (issue #66): pack the state body. The
        // device side picks report 0x09 or 0x05, stamps the counters and
        // sends one report every 4 ms whatever rate this is called at, so a
        // submit only replaces what the next report is built from. The body
        // travels in the section's extended lane, which the backend reads
        // back byte for byte.
        if (_switch2Protocol)
        {
            Switch2ProPacker.BuildBody(in state, mlx, mly, mrx, mry, mlt, mrt, _switch2BodyBuffer!);
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo,
                Array.Empty<byte>(), 0, null,
                dataOffset: 0,
                extendedData: _switch2BodyBuffer!, extendedLen: Switch2ProPacker.BodySize);

            if (latency != null)
            {
                long s2Elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
                latency(s2Elapsed * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
            }
            return;
        }

        byte[] report;
        // v1.3.5: vendor-blob path is gated on the host-side arm flag.
        // Sony BT controllers default to legacy short Report 0x01; the host
        // (Steam Input, Chrome's Gamepad API, dualsense-tester, ds.daidr.me)
        // issues a Get_Feature on 0x05 / 0x09 / 0x20 to switch real firmware
        // into vendor-blob mode (Report 0x31 / 0x11). The arm-watcher in
        // OutputPollLoop flips _extendedModeArmed when any of those reads
        // arrives via HidFeatureRead. Until then, fall through to the
        // descriptor-driven BuildReportInto path so joy.cpl, RawInput, and
        // generic HID consumers see structured X/Y/Rx/Ry through Report 0x01.
        // A profile whose extendedReport sets alwaysArmed starts armed and
        // never takes the legacy path, for controllers that stream the
        // vendor report from power-on rather than switching into it.
        // The codec runs only after the host-side arm-handshake has fired.
        // Profiles without an armOn list (every USB Sony profile, every
        // generic profile) never arm, so they always take the legacy
        // BuildReportInto path: same code path v1.3.4 used. This avoids
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
            // v1.3.9: unified axes dict drives every declared analog input.
            // Hat priority chain (HatDegrees > HatHundredths > HatRaw > Hat)
            // picks the first non-null and ignores the rest.
            _reportBuilder.BuildReportInto(_reportBuffer,
                axes: state.Axes,
                hatValue: (int)state.Hat,
                buttonMask: (uint)state.Buttons,
                hatDegrees: state.HatDegrees,
                hatHundredths: state.HatHundredths,
                hatRaw: state.HatRaw);

            // v1.3.5: overlay profile-declared fixed bytes (e.g. DS5 Edge
            // USB activeProfile = 0x80 at byte 49 so dualsense-tester's
            // useInNormalMode check `byte && (byte & 3) === 0` succeeds
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

        // T26-2: pack the GIP-format buffer ONLY for Xbox-VID profiles.
        // The XUSB companion (HMXInput.dll) reads this slice on
        // IOCTL_XUSB_GET_STATE; non-Xbox profiles have no XUSB companion
        // bound, so the bytes are unused: skip the per-frame packing
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
            // 4-bit hat: companion.c does (btnHigh >> 2) & 0x0F and switches
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

        // v1.3.5: two write paths, mutually exclusive per frame:
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
        //    sticks in this state: same as real Sony hardware behavior
        //    once Steam runs and switches the controller to extended mode.
        //
        // dataLen capped at SharedMemoryIO.DATA_CAPACITY (256 bytes; widened
        // from 64 in 2026-04-23). T26-2: pass null for gipData on non-Xbox
        // profiles so WriteInputFrame skips the 14-byte Marshal.Copy.
        if (useExtended)
        {
            int extLen = Profile.ExtendedReport!.Size;
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo,
                Array.Empty<byte>(), 0,
                _packsGipBuffer ? _gipBuf : null,
                companionEvent: _companionInputEvent,
                dataOffset: 0,
                extendedData: report, extendedLen: extLen);
        }
        else
        {
            int dataStart = _reportBuilder.InputReportId != 0 ? 1 : 0;
            int dataLen = Math.Min(report.Length - dataStart, SharedMemoryIO.DATA_CAPACITY);
            SharedMemoryIO.WriteInputFrame(
                _inputView, _inputEvent, ref _inputSeqNo, report, dataLen,
                _packsGipBuffer ? _gipBuf : null, dataStart,
                companionEvent: _companionInputEvent);
        }

        if (latency != null)
        {
            long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
            long micros = elapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            latency(micros);
        }
    }

    /// <summary>The six simple-slot values (left stick X/Y, right stick X/Y,
    /// LT, RT) every encoder starts from, resolved from
    /// <see cref="HMGamepadState.Axes"/> through the profile's declared
    /// sticks and triggers. v1.3.9: a single unified Axes dict drives every
    /// analog input. Auto-default: 0.5 (centered) for sticks, 0.0
    /// (released) for triggers.</summary>
    private void ResolveSimpleSlots(in HMGamepadState state, out double mlx, out double mly,
                                    out double mrx, out double mry, out double mlt, out double mrt)
    {
        var axes = state.Axes;
        double GetAxis(HMAxis ax, double def) =>
            axes != null && axes.TryGetValue(ax, out var v) ? Math.Clamp(v, 0f, 1f) : def;

        // Cached at construction: Profile.Sticks / Profile.Triggers are
        // uncached computed properties that each allocate a List plus
        // record elements on every access. Reading them per SubmitState
        // frame (~250 Hz × N controllers) is ~6 heap objects/frame of
        // avoidable GC pressure on the hot path (audit 1n). The layout is
        // immutable for the controller's lifetime, so resolve once.
        var sticks = _cachedSticks;
        var triggers = _cachedTriggers;
        // Sticks resolve canonical-first when the descriptor declares none,
        // mirroring ResolveTrigger below (issue #56). Profile.Sticks derives
        // from the descriptor-built report builder, so a profile whose
        // descriptor is an opaque vendor blob - every Valve state packet -
        // has no declared sticks at all. Hard-defaulting those to 0.5 put a
        // live device on the wire whose sticks never left center, while its
        // triggers worked, because only triggers had a fallback. The right
        // stick accepts either convention: Rx/Ry, or Sony's Z/Rz.
        mlx = sticks.Count > 0 ? GetAxis(sticks[0].XAxis, 0.5) : GetAxis(HMAxis.X, 0.5);
        mly = sticks.Count > 0 ? GetAxis(sticks[0].YAxis, 0.5) : GetAxis(HMAxis.Y, 0.5);
        mrx = sticks.Count > 1 ? GetAxis(sticks[1].XAxis, 0.5)
                   : axes != null && axes.ContainsKey(HMAxis.Rx) ? GetAxis(HMAxis.Rx, 0.5)
                                                                 : GetAxis(HMAxis.Z, 0.5);
        mry = sticks.Count > 1 ? GetAxis(sticks[1].YAxis, 0.5)
                   : axes != null && axes.ContainsKey(HMAxis.Ry) ? GetAxis(HMAxis.Ry, 0.5)
                                                                 : GetAxis(HMAxis.Rz, 0.5);
        // Triggers resolve canonical-first, then fall back to the descriptor's
        // declared trigger field. PadForge writes axes[Z]/axes[Rz] via
        // ResolveAxisByRole (canonical defaults when no axisMap). HIDMaestroTest
        // and StandardAxes write axes[triggers[N].Axis] directly. Both must feed
        // the GIP buffer that drives XInput, WGI and Raw Input. Otherwise the
        // unified xbox-360-* descriptors (layout.triggers = Vx/Vy) silently
        // drop PadForge's writes and triggers freeze in non-DirectInput APIs.
        mlt = ResolveTrigger(axes, triggers, 0, _canonicalLt);
        mrt = ResolveTrigger(axes, triggers, 1, _canonicalRt);
    }

    /// <summary>The six simple-slot values as the port encoders take them.</summary>
    internal void ResolveSlots(in HMGamepadState state, out float lx, out float ly, out float rx,
                               out float ry, out float lt, out float rt)
    {
        ResolveSimpleSlots(in state, out double mlx, out double mly, out double mrx,
                           out double mry, out double mlt, out double mrt);
        lx = (float)mlx; ly = (float)mly; rx = (float)mrx;
        ry = (float)mry; lt = (float)mlt; rt = (float)mrt;
    }

    /// <summary>The lock every submit path writes the input section under.</summary>
    internal object SubmitLock => _submitLock;

    internal void ThrowIfDisposedInternal() => ThrowIfDisposed();

    /// <summary>The ports' whole report through the extended lane, which a
    /// USB/IP device reads back byte for byte. <paramref name="direct"/> is
    /// true for a port call of the consumer's own, which this records as
    /// SubmitRawExtendedReport records a frame, and false inside
    /// SubmitState, which records its own. Caller holds
    /// <see cref="SubmitLock"/>.</summary>
    internal void WritePortsReportLocked(byte[] report, bool direct)
    {
        if (!direct)
        {
            SubmitRawExtendedReportCore(report);
            return;
        }
        AwaitPreviousDelivery();
        SubmitRawExtendedReportCore(report);
        NoteFrameWritten();
        if (_idleThread != null)
            RememberRawFrame(report, extended: true);
    }

    /// <summary>v1.13.0: bind WinUSB to this controller's virtual USB/IP
    /// device, so libusb hosts can open it, for a profile that declares
    /// <c>usbConfiguration.winUsb</c>. Nothing the device reports changes,
    /// and no other device is touched: a real device, now or later, keeps
    /// the driver Windows gives it. The binding is recorded and is undone
    /// when the controller is disposed or its program ends. Requires
    /// elevation, as creating the controller does.</summary>
    /// <returns><see cref="HMWinUsbBinding.Bound"/>, or
    /// <see cref="HMWinUsbBinding.AlreadyBound"/> when the device already
    /// carries a driver libusb opens, or <see cref="HMWinUsbBinding.OtherDriver"/>
    /// when it carries a driver from outside Windows, which this step leaves
    /// alone.</returns>
    /// <exception cref="InvalidOperationException">The profile declares no
    /// winUsb, or no driver Windows has ranks above the package, which would
    /// let Windows choose the package for real devices.</exception>
    public HMWinUsbBinding BindWinUsb()
    {
        ThrowIfDisposed();
        return Internal.WinUsbBinding.Bind(this);
    }

    /// <summary>Push a raw HID input report for features that
    /// <see cref="HMGamepadState"/> doesn't model: touchpad coordinates,
    /// gyroscope, sensor packets, vendor extensions.
    ///
    /// <para>Pass <b>data bytes only</b>: do NOT include a Report ID prefix.
    /// The driver prepends the Report ID automatically (same as
    /// <see cref="SubmitState"/>). For a DualSense with Report ID 0x01 and
    /// 64-byte InputReportByteLength, pass 63 bytes of data.</para>
    ///
    /// <para>A profile whose <c>extendedReport</c> is <c>alwaysArmed</c> is
    /// the exception: it is never in legacy mode, so the frame is emitted
    /// verbatim on the report that profile declares rather than on whichever
    /// report the descriptor happens to list first. Data-only still works and
    /// gets the declared id prepended; a frame already carrying its own id is
    /// passed through untouched. Both are accepted, discriminated by length.
    /// <see cref="SubmitRawExtendedReport"/> is the explicit form. This
    /// matters for the Steam Controller 2, whose descriptor puts a
    /// lizard-mode mouse ahead of its controller state (issue #58).</para>
    ///
    /// <para>For profiles with no Report ID (e.g. Xbox Series BT), pass the
    /// full report as-is.</para>
    ///
    /// <para>Tip: use <see cref="HMProfile.InputReportSize"/> and
    /// <see cref="HMProfile.GetDescriptorBytes"/> to determine the expected
    /// data layout. The test app's <c>info</c> command shows every field's
    /// bit offset.</para>
    ///
    /// <para>The Switch 2 Pro Controller composite persona takes no raw
    /// reports and throws <see cref="NotSupportedException"/>. Which report
    /// it sends, and when, belongs to the device, as it does on the real
    /// pad, so its input comes from <see cref="SubmitState"/> alone.</para>
    /// </summary>
    public void SubmitRawReport(ReadOnlySpan<byte> report)
    {
        ThrowIfDisposed();
        ThrowIfDeviceBuildsReports();
        if (report.Length == 0) throw new ArgumentException("Report cannot be empty.", nameof(report));
        if (report.Length > SharedMemoryIO.DATA_CAPACITY)
            throw new ArgumentException(
                $"Report length {report.Length} exceeds the {SharedMemoryIO.DATA_CAPACITY}-byte shared section payload.",
                nameof(report));

        lock (_submitLock)
        {
            AwaitPreviousDelivery();
            SubmitRawReportCore(report);
            NoteFrameWritten();
            if (_deliverEvery && _kbm != null)
            {
                // The driver sends this lane's data under the descriptor's
                // first input report ID.
                byte id = _kbm.FirstInputReportId;
                if (id == 0) NoteReleaseFrame(report);
                else
                {
                    var full = new byte[report.Length + 1];
                    full[0] = id;
                    report.CopyTo(full.AsSpan(1));
                    NoteReleaseFrame(full);
                }
            }
            if (_idleThread != null)
                RememberRawFrame(report, extended: false);
        }
    }

    private void SubmitRawReportCore(ReadOnlySpan<byte> report)
    {
        // v1.3.0: copy into the per-controller reusable buffer instead of
        // report.ToArray()'ing per call. Vendor-protocol consumers (PadForge
        // DualSense path, etc.) hit this path at the same rate as
        // SubmitState; the alloc-per-call cost was visible.
        report.CopyTo(_rawReportBuffer.AsSpan());

        // v1.3.5: overlay profile-declared fixed bytes. Note that
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
        // left it in (or zero if SubmitState was never called): raw consumers
        // are expected to also call SubmitState if they need GIP/XInput.
        // T30-2: pass null for gipData on non-Xbox profiles, same logic as
        // SubmitState's Xbox-only GIP packing. Saves the 14-byte Marshal.Copy
        // per raw frame on DualSense / Switch Pro / generic gamepad paths.
        // Issue #58. A profile that is always in extended mode has no legacy
        // report to fall back to: whatever the descriptor happens to declare
        // first is not where its input belongs. The Steam Controller 2 puts
        // its lizard-mode mouse (0x40) and keyboard (0x41) ahead of its
        // controller state (0x42) on one interface, exactly as the hardware
        // does, so a prepend by position re-heads every frame as a mouse
        // report and the rolling sequence number lands on relative X.
        //
        // For these profiles the frame goes out verbatim through the same
        // extended path SubmitState uses. Both caller conventions are
        // accepted, discriminated by length rather than by guessing at byte
        // 0: a frame the size of the declared report already carries its own
        // report id, and a data-only frame one byte shorter gets the
        // declared id prepended. Anything else is the caller's own layout
        // and is passed through untouched.
        if (_rawEmitsVerbatim)
        {
            EmitExtendedFrame(_rawReportBuffer, report.Length);
            return;
        }

        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo, _rawReportBuffer, report.Length,
            _packsGipBuffer ? _gipBuf : null,
            companionEvent: _companionInputEvent);
    }

    /// <summary>Submit a complete extended report, emitted verbatim with no
    /// report-id prepend, whatever the profile declares.
    ///
    /// <para>This is the explicit form of what <see cref="SubmitRawReport"/>
    /// does automatically for an <c>alwaysArmed</c> profile. Use it when the
    /// caller owns the whole frame including its report id and wants that
    /// guaranteed regardless of profile (issue #58).</para>
    ///
    /// <para>Throws <see cref="NotSupportedException"/> on the Switch 2 Pro
    /// Controller composite persona, as <see cref="SubmitRawReport"/>
    /// does.</para>
    /// </summary>
    /// <param name="report">The complete frame, report id included.</param>
    public void SubmitRawExtendedReport(ReadOnlySpan<byte> report)
    {
        ThrowIfDisposed();
        ThrowIfDeviceBuildsReports();
        if (report.Length == 0) throw new ArgumentException("Report cannot be empty.", nameof(report));
        // The extended lane carries the whole frame in an 80-byte field. The
        // writer used to cut a longer one there without a word.
        if (report.Length > SharedMemoryIO.EXTENDED_DATA_CAPACITY)
            throw new ArgumentException(
                $"Report length {report.Length} exceeds the {SharedMemoryIO.EXTENDED_DATA_CAPACITY}-byte extended report field.",
                nameof(report));

        lock (_submitLock)
        {
            AwaitPreviousDelivery();
            SubmitRawExtendedReportCore(report);
            NoteFrameWritten();
            if (_deliverEvery && _kbm != null) NoteReleaseFrame(report);
            if (_idleThread != null)
                RememberRawFrame(report, extended: true);
        }
    }

    // ── Keyboards and mice (issue #67) ───────────────────────────────────

    /// <summary>True when this controller sends every report to HID class
    /// in order instead of the latest one: a keyboard or mouse profile (or
    /// an "other" profile whose descriptor is a keyboard or mouse) on a
    /// driver that acknowledges delivery. Gamepads keep latest-wins, which
    /// suits state.</summary>
    public bool DeliversEveryReport =>
        _deliverEvery && SharedMemoryIO.InputAckSupported(Index);

    /// <summary>True when the driver lets go of every key and button this
    /// keyboard or mouse holds if the consuming process ends without
    /// disposing it, however it ends: keys and buttons go up, relative
    /// motion stops, and an absolute pointer stays where it was. Without
    /// it a held key repeats until the device is removed.
    /// <see cref="Dispose"/> sends the same release itself.</summary>
    public bool ReleasesOnConsumerExit =>
        _deliverEvery && SharedMemoryIO.ReleaseOnExitSupported(Index);

    /// <summary>Raised on the output thread when the host sets the
    /// keyboard's LEDs (Num Lock, Caps Lock, Scroll Lock, Compose, Kana).
    /// Windows sends a keyboard the current state when it arrives and again
    /// on every change. Only for profiles whose keyboard collection declares
    /// an LED output report.</summary>
    public event Action<HMController, HMKeyboardLeds>? KeyboardLedsChanged;

    /// <summary>The LED state the host last set on this keyboard, or null
    /// until it sets one. Windows sends the state at arrival while
    /// <see cref="HMContext.CreateController(HMProfile, string)"/> is still
    /// running, so it can land here before any
    /// <see cref="KeyboardLedsChanged"/> handler is attached. Attach the
    /// handler, then read this property, and one of the two has
    /// it.</summary>
    public HMKeyboardLeds? KeyboardLeds
    {
        get { int v = _keyboardLeds; return v < 0 ? null : (HMKeyboardLeds)v; }
    }
    private volatile int _keyboardLeds = -1;

    /// <summary>Send one mouse report, built from the profile's own mouse
    /// collection. Buttons past the ones that collection declares go to the
    /// profile's second, non-system collection when it has one (the
    /// <see cref="HidDescriptorBuilder.VendorButtons"/> shape), since Raw
    /// Input's RAWMOUSE carries five. A relative movement larger than the
    /// report can hold is sent as several reports. Thread-safe. On a
    /// keyboard or mouse profile it returns once the previous report has
    /// been delivered, which takes well under a millisecond while Windows
    /// is reading the device.</summary>
    /// <exception cref="InvalidOperationException">The profile's descriptor
    /// declares no mouse collection.</exception>
    public void SubmitMouse(in HMMouseState state)
    {
        ThrowIfDisposed();
        ThrowIfDeviceBuildsReports();
        var packer = KbmPacker();
        lock (_submitLock)
        {
            var frames = packer.PackMouse(in state, ref _lastExtraButtons, ref _extraButtonsSent);
            foreach (var frame in frames)
                WriteKbmFrame(frame);
        }
    }

    /// <summary>Send one keyboard report: the modifiers and up to as many
    /// keys as the profile's keyboard report holds, all held down. Send
    /// another report without a key to release it. More keys than the
    /// report holds report ErrorRollOver, as a real keyboard does.</summary>
    /// <exception cref="InvalidOperationException">The profile's descriptor
    /// declares no keyboard collection.</exception>
    public void SubmitKeyboard(HMKeyModifiers modifiers, ReadOnlySpan<HMKey> keys)
    {
        ThrowIfDisposed();
        ThrowIfDeviceBuildsReports();
        var packer = KbmPacker();
        lock (_submitLock)
            WriteKbmFrame(packer.PackKeyboard(modifiers, keys));
    }

    /// <summary>Send one consumer-control report with these usages held
    /// (HID Usage Tables, Consumer page: 0x00E9 Volume Increment, 0x00CD
    /// Play/Pause and so on). Send an empty span to release them.</summary>
    /// <exception cref="InvalidOperationException">The profile's descriptor
    /// declares no consumer control collection.</exception>
    public void SubmitConsumer(ReadOnlySpan<ushort> usages)
    {
        ThrowIfDisposed();
        ThrowIfDeviceBuildsReports();
        var packer = KbmPacker();
        lock (_submitLock)
            WriteKbmFrame(packer.PackConsumer(usages));
    }

    private KeyboardMousePacker KbmPacker() =>
        _kbm ??= Profile.Inner.GetOrBuildKeyboardMousePacker();

    /// <summary>A whole frame, report ID included, through the extended
    /// lane, which sends it at its own length. Caller holds _submitLock.</summary>
    private void WriteKbmFrame(byte[] frame)
    {
        AwaitPreviousDelivery();
        SubmitRawExtendedReportCore(frame);
        NoteFrameWritten();
        if (_deliverEvery) NoteReleaseFrame(frame);
    }

    // The report that releases each input report this controller has sent,
    // in the order the reports first appeared. Guarded by _submitLock.
    private readonly List<byte[]> _releaseFrames = new();

    /// <summary>Keep the release frame for the report just written, and hand
    /// the set to the driver when it changed, so a consumer that goes away
    /// with a key or button held does not leave it held. Caller holds
    /// _submitLock.</summary>
    private void NoteReleaseFrame(ReadOnlySpan<byte> frame)
    {
        if (_kbm?.ReleaseOf(frame) is not { } release) return;
        bool withIds = _kbm.Map.UsesReportIds;
        int slot = _releaseFrames.FindIndex(f => !withIds || f[0] == release[0]);
        if (slot >= 0)
        {
            if (_releaseFrames[slot].AsSpan().SequenceEqual(release)) return;
            _releaseFrames[slot] = release;
        }
        else
        {
            if (_releaseFrames.Count >= SharedMemoryIO.RELEASE_SLOTS) return;
            _releaseFrames.Add(release);
        }
        SharedMemoryIO.WriteReleaseFrames(Index, _releaseFrames);
    }

    /// <summary>Send every release frame, so a key or button the consumer
    /// still holds is let go before the device goes away.</summary>
    private void SendReleaseFrames()
    {
        if (!_deliverEvery) return;
        lock (_submitLock)
        {
            if (_releaseFrames.Count == 0) return;
            foreach (var f in _releaseFrames.ToArray())
            {
                AwaitPreviousDelivery();
                SubmitRawExtendedReportCore(f);
                NoteFrameWritten();
            }
            AwaitPreviousDelivery();
        }
    }

    /// <summary>Hold the next frame until the driver has delivered the last
    /// one. Gives up after DeliveryTimeoutMs (Windows not reading the
    /// device, say while it starts), and after three misses in a row stops
    /// waiting until an acknowledgement shows up again. Caller holds
    /// _submitLock.</summary>
    private void AwaitPreviousDelivery()
    {
        if (!_deliverEvery || !_deliveryPending) return;
        if (SharedMemoryIO.InputAckSupported(Index))
        {
            int timeout = _deliveryMisses >= 3 ? 0 : DeliveryTimeoutMs;
            if (SharedMemoryIO.WaitInputDelivered(Index, _deliverySeq, timeout))
                _deliveryMisses = 0;
            else
                _deliveryMisses++;
        }
        long due = _lastFrameTicks + s_minFrameTicks;
        while (System.Diagnostics.Stopwatch.GetTimestamp() < due)
            Thread.SpinWait(64);
    }

    private void NoteFrameWritten()
    {
        if (!_deliverEvery) return;
        _deliverySeq = _inputSeqNo;
        _deliveryPending = true;
        _lastFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Keep a raw consumer's frame for the idle pump, which repeats
    /// it through the same path, byte for byte. Called under
    /// <see cref="_submitLock"/> once the frame has gone out.</summary>
    private void RememberRawFrame(ReadOnlySpan<byte> report, bool extended)
    {
        _lastRawFrame ??= new byte[SharedMemoryIO.DATA_CAPACITY];
        report.CopyTo(_lastRawFrame);
        _lastRawLength = report.Length;
        _lastRawWasExtended = extended;
        _lastSubmitWasRaw = true;
        Volatile.Write(ref _lastSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private void SubmitRawExtendedReportCore(ReadOnlySpan<byte> report)
    {
        report.CopyTo(_rawReportBuffer.AsSpan());
        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo,
            Array.Empty<byte>(), 0,
            _packsGipBuffer ? _gipBuf : null,
            companionEvent: _companionInputEvent,
            dataOffset: 0,
            extendedData: _rawReportBuffer, extendedLen: report.Length);
    }

    /// <summary>Emit through the extended path, prepending the declared
    /// report id when the caller passed a data-only frame. Shared by the raw
    /// path so both conventions land on the same wire bytes.</summary>
    private void EmitExtendedFrame(byte[] buffer, int length)
    {
        int declaredSize = Profile.ExtendedReport!.Size;
        byte rid = _rawVerbatimReportId;
        int emitLen = length;

        if (length == declaredSize - 1 && length + 1 <= buffer.Length)
        {
            // Data-only, the contract SubmitRawReport documents. Shift up one
            // byte and head the frame with the id the profile declares.
            Array.Copy(buffer, 0, buffer, 1, length);
            buffer[0] = rid;
            emitLen = length + 1;
        }

        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo,
            Array.Empty<byte>(), 0,
            _packsGipBuffer ? _gipBuf : null,
            companionEvent: _companionInputEvent,
            dataOffset: 0,
            extendedData: buffer, extendedLen: emitLen);
    }

    /// <summary>v1.3.5: instance-level <see cref="HMOutputEncoder.Encode"/>
    /// that threads per-controller rolling-counter state through the codec.
    ///
    /// <para>Required for DS5 BT effect output: the spec's <c>btTag</c> field
    /// is a stride-16 rolling counter, and real Sony firmware drops the
    /// effect packet if consecutive writes don't carry the next tag value.
    /// The static <see cref="HMOutputEncoder.Encode"/> overload is stateless
    /// and falls back to <c>initial</c>; use this method instead so the
    /// SDK owns the increment.</para>
    ///
    /// <para>Per-controller: multiple virtuals never share counter state.
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
                $"Profile '{Profile.Id}' has no extendedOutputReport spec: nothing to encode against.");

        lock (_outputEncoderStateLock)
        {
            _outputEncoderState ??= new VendorBlobCodec.EncoderState();
            return VendorBlobCodec.EncodeOutput(spec, fields, _outputEncoderState);
        }
    }

    /// <summary>Re-publish the consumer's last frame at the profile's
    /// declared interval whenever the consumer has gone quiet for longer
    /// than that. A <see cref="SubmitState"/> frame is encoded again for
    /// each repeat rather than copied, so rolling counters advance the way
    /// a real device's do: SDL_hidapi_steam.c treats a repeated unPacketNum
    /// as no new data at all. A raw frame is the consumer's own bytes and is
    /// repeated as they are.
    ///
    /// <para>The cadence is a deadline in Stopwatch ticks, waited out on a
    /// high-resolution waitable timer, the way UsbAudioEngine paces audio.
    /// A plain wait rounds up to the 15.6 ms system tick, which held a 4 ms
    /// Steam Deck to about 62 frames a second. SDL's Deck driver reads once
    /// with a 16 ms timeout before it accepts the device, and at that rate
    /// the read could come back empty.</para></summary>
    private void IdleFrameLoop(int intervalMs)
    {
        var token = _idleCts.Token;
        long interval = Math.Max(1, intervalMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        var waits = new[] { timer, token.WaitHandle.SafeWaitHandle.DangerousGetHandle() };
        long due = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                // A consumer's own frame puts the next repeat one interval
                // after it, so a live consumer's cadence always wins.
                long afterConsumer = Volatile.Read(ref _lastSubmitTimestamp) + interval;
                if (afterConsumer > due) due = afterConsumer;

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now >= due)
                {
                    try { RepeatLastFrame(); }
                    catch
                    {
                        // Same containment contract as OutputPollLoop: a
                        // transient failure must not kill the pump, and
                        // disposal races here are ordinary rather than
                        // exceptional.
                    }
                    // Fixed rate, the way a device clock runs. A pump that
                    // fell a whole interval behind starts again from now
                    // rather than sending a burst.
                    due += interval;
                    if (due <= now) due = now + interval;
                    continue;
                }

                long wait = due - now;
                if (timer != IntPtr.Zero)
                {
                    long rel = -Math.Max(1, wait * 10_000_000L / System.Diagnostics.Stopwatch.Frequency); // relative, 100 ns units
                    if (SetWaitableTimer(timer, ref rel, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        uint woke = WaitForMultipleObjects(2, waits, false, (uint)(intervalMs * 4 + 16));
                        if (woke == 1) break;
                        if (woke != WAIT_FAILED) continue;
                    }
                }
                // No high-resolution timer (Windows 10 before 1803) or a
                // failed wait: the plain wait, coarser but still running.
                int ms = (int)Math.Max(1, wait * 1000 / System.Diagnostics.Stopwatch.Frequency);
                if (token.WaitHandle.WaitOne(ms)) break;
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    /// <summary>One idle repeat of the consumer's last frame, under the same
    /// lock every submit path takes.</summary>
    private void RepeatLastFrame()
    {
        lock (_submitLock)
        {
            if (_disposed) return;
            if (_lastSubmitWasRaw && _lastRawFrame != null)
            {
                var frame = new ReadOnlySpan<byte>(_lastRawFrame, 0, _lastRawLength);
                if (_lastRawWasExtended) SubmitRawExtendedReportCore(frame);
                else SubmitRawReportCore(frame);
            }
            else
            {
                var st = _lastSubmitted;
                SubmitStateCore(in st, consumerCall: false);
            }
        }
    }

    /// <summary>Background reader for the per-controller output shared
    /// section, raising <see cref="OutputReceived"/> for each new packet.
    ///
    /// Event-driven since issue #34: the driver creates
    /// <c>Global\HIDMaestroOutputEvent&lt;N&gt;</c> and signals it after every
    /// published packet, so this thread blocks on the event (500 ms safety
    /// timeout, ~2 wakes/s idle) instead of polling every 8 ms
    /// (125 wakes/s idle). Open success doubles as capability detection:
    /// against an older driver that never created the event, the loop
    /// keeps the historical 8 ms poll cadence and retries the open every
    /// 64 cycles (~0.5 s) in case the driver device finishes starting
    /// after this thread does. Output latency with the event is dispatch
    /// cost instead of up-to-8-ms poll quantization; the drain-to-Head
    /// loop below is unchanged, so burst coalescing behaves identically
    /// in both modes.</summary>
    private void OutputPollLoop()
    {
        if (_outputView == IntPtr.Zero) return;
        // Start where this controller's setup found the ring
        // (SharedMemoryIO.MarkOutputStart), so a packet a host sent while
        // the device was starting is read too: Windows sends a new keyboard
        // its LED state then (issue #67). Whatever an earlier controller at
        // this index left stays behind the mark. With no mark, start at the
        // current Head, so nothing from a prior session fires.
        uint lastSeq = SharedMemoryIO.TakeOutputStart(Index)
            ?? (uint)System.Runtime.InteropServices.Marshal.ReadInt32(_outputView, 0);
        byte[] buf = new byte[256];
        var ct = _outputCts.Token;

        IntPtr rawEvt = SharedMemoryIO.TryOpenOutputEvent(Index);
        AutoResetEvent? doorbell = null;
        WaitHandle[]? waitPair = null;
        int openRetryCountdown = 64;
        // Adaptive missed-signal healing (#34 audit): in event mode the
        // 500 ms timeout is supposed to be pure paranoia. If a TIMEOUT
        // wake (not an event wake) finds ring data, some producer
        // advanced Head without signaling (version-skewed companion,
        // event-create failure, a foreign waiter stealing wakes). Drop
        // to the historical 8 ms cadence so delivery and ring headroom
        // return to pre-#34 behavior. A drain aborted by a throwing
        // subscriber also shortens the next wait: the consumed signal
        // can cover a packet the aborted drain never reached.
        int eventTimeoutMs = 500;
        int missedSignalStrikes = 0;
        bool drainFaulted = false;
        bool lastWakeWasTimeout = false;
        if (rawEvt != IntPtr.Zero)
        {
            doorbell = new AutoResetEvent(false);
            doorbell.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(rawEvt, ownsHandle: true);
            waitPair = new WaitHandle[] { ct.WaitHandle, doorbell };
        }
        try
        {
        while (!ct.IsCancellationRequested)
        {
            bool drainedAny = false;
            try
            {
                // v1.1.40: drain the ring on every poll. pid.dll writes
                // Set Effect → Set Constant Force → Effect Operation Start
                // within 1-3 ms; the pre-1.1.40 single-slot channel was
                // coalescing those bursts vs the 8 ms poll cadence and
                // losing the middle (magnitude) packet.
                while (SharedMemoryIO.TryReadOutputFrame(_outputView, ref lastSeq,
                        out byte source, out byte reportId, out int dataSize, buf))
                {
                    drainedAny = true;
                    // A protocol device's return to its initial state. It
                    // came from the device, not a host, so it is not output.
                    if (source == Internal.Usbip.UsbipEmulatedDevice.SourceDeviceReset)
                    {
                        Ports.OnDeviceReset();
                        if (ct.IsCancellationRequested) break;
                        continue;
                    }
                    var data = new ReadOnlyMemory<byte>(buf, 0, dataSize);
                    var pkt = new HMOutputPacket((HMOutputSource)source, reportId, data, lastSeq);
                    OutputReceived?.Invoke(this, pkt);

                    // Issue #67: the keyboard's LED report, decoded by its
                    // own descriptor.
                    if (_kbm is { HasKeyboardLeds: true } kbm
                        && (HMOutputSource)source == HMOutputSource.HidOutput
                        && kbm.DecodeLeds(reportId, data.Span) is HMKeyboardLeds leds)
                    {
                        _keyboardLeds = (int)leds;
                        KeyboardLedsChanged?.Invoke(this, leds);
                    }

                    // v1.3.5: vendor-blob output decode. When the profile
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
                            // the codec: VendorBlobCodec expects the RID
                            // at offset 0. The shared output ring stores
                            // the RID separately so we synthesize it here.
                            var full = new byte[dataSize + 1];
                            full[0] = reportId;
                            Buffer.BlockCopy(buf, 0, full, 1, dataSize);

                            var (fields, crcValid) = VendorBlobCodec.Decode(extOut, full);
                            Ports.AddDecoded(fields, reportId, new ReadOnlySpan<byte>(buf, 0, dataSize));
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

                    // Switch Pro rumble decode (issue #33): output 0x01
                    // (rumble + subcommand) and 0x10 (rumble only) carry
                    // one 4-byte HD-rumble block per side at payload
                    // [1..4] / [5..8]. Surface coarse amplitudes through
                    // the same OutputDecoded lane the Sony profiles use,
                    // as leftMotor / rightMotor bytes, so PadForge's
                    // feedback handler works unchanged (spec point 4).
                    else if (_switchProtocol
                        && (reportId == 0x01 || reportId == 0x10)
                        && dataSize >= 9 && OutputDecoded != null)
                    {
                        try
                        {
                            byte leftAmp = SwitchProPacker.DecodeRumbleAmplitude(
                                new ReadOnlySpan<byte>(buf, 1, 4));
                            byte rightAmp = SwitchProPacker.DecodeRumbleAmplitude(
                                new ReadOnlySpan<byte>(buf, 5, 4));
                            var full = new byte[dataSize + 1];
                            full[0] = reportId;
                            Buffer.BlockCopy(buf, 0, full, 1, dataSize);
                            OutputDecoded.Invoke(this, new HMOutputDecodedEventArgs
                            {
                                ReportId = reportId,
                                Fields = new Dictionary<string, object>
                                {
                                    ["leftMotor"] = leftAmp,
                                    ["rightMotor"] = rightAmp,
                                },
                                RawBytes = full,
                                CrcValid = true,
                            });
                        }
                        catch
                        {
                            // Same containment contract as the codec path.
                        }
                    }

                    // Switch 2 Pro rumble decode (issue #66): output report
                    // 0x02 carries one 16-byte block per actuator. The
                    // levels surface as leftMotor and rightMotor on the lane
                    // the first Pro uses. A report of another ID, or one
                    // too short to hold both blocks, is skipped: a console
                    // also sends empty packets on this endpoint.
                    else if (_switch2Protocol
                        && source == (byte)HMOutputSource.HidOutput && reportId == 0x02
                        && dataSize >= Switch2ProPacker.RumbleDataSize && OutputDecoded != null)
                    {
                        try
                        {
                            Switch2ProPacker.DecodeRumble(new ReadOnlySpan<byte>(buf, 0, dataSize),
                                out byte leftMotor, out byte rightMotor);
                            var full = new byte[dataSize + 1];
                            full[0] = reportId;
                            Buffer.BlockCopy(buf, 0, full, 1, dataSize);
                            OutputDecoded.Invoke(this, new HMOutputDecodedEventArgs
                            {
                                ReportId = reportId,
                                Fields = new Dictionary<string, object>
                                {
                                    ["leftMotor"] = leftMotor,
                                    ["rightMotor"] = rightMotor,
                                },
                                RawBytes = full,
                                CrcValid = true,
                            });
                        }
                        catch
                        {
                            // Same containment contract as the codec path.
                        }
                    }

                    // Each port's output fields, from an output report that
                    // covers every port. A shorter one sets nothing.
                    if (Ports.Count > 0 && source == (byte)HMOutputSource.HidOutput)
                        Ports.OnOutputReport(reportId, new ReadOnlySpan<byte>(buf, 0, dataSize));

                    // v1.3.5: arm-handshake watcher. When the profile
                    // declares armOn triggers and a matching host action
                    // arrives, flip the armed flag: SubmitState then
                    // switches from legacy Report 0x01 emission to
                    // vendor-blob Report 0x31 / 0x11 emission via the
                    // extended shared-memory path (see SubmitState's
                    // useExtended branch). Sony BT profiles arm on
                    // Get_Feature 0x05 / 0x09 / 0x20 reads: the same
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
                // doesn't kill the reader thread. An aborted drain may have
                // consumed a coalesced signal that covered a packet it never
                // reached, so the next wait must be short (see below).
                drainFaulted = true;
            }

            // Missed-signal evidence (audit of #34): data discovered by a
            // TIMEOUT wake means a producer published without a signal
            // reaching us. Two strikes before degrading: a packet landing
            // in the instant between timeout expiry and the drain leaves
            // its signal LATCHED (the next wait returns immediately), so a
            // single occurrence is a benign race, while a producer that
            // truly never signals accumulates strikes fast. On the second
            // strike, degrade event mode's timeout to the historical 8 ms
            // permanently; correctness beats idle savings.
            if (lastWakeWasTimeout && drainedAny && ++missedSignalStrikes >= 2)
                eventTimeoutMs = 8;

            // Event mode (issue #34): block on {cancel, doorbell} with a
            // safety timeout (500 ms while signals are proving reliable,
            // 8 ms after any missed-signal evidence, one 8 ms retry after
            // a faulted drain). The driver signals after publishing Head,
            // so an event wake always finds the packet. Poll fallback
            // (older driver, no event): the historical 8 ms CTS-handle
            // wait (T10), plus a throttled re-open attempt every 64 cycles
            // so a driver device that starts after this thread still
            // upgrades the loop to event mode.
            try
            {
                if (waitPair != null)
                {
                    int timeout = drainFaulted ? 8 : eventTimeoutMs;
                    drainFaulted = false;
                    int woke = WaitHandle.WaitAny(waitPair, timeout);
                    lastWakeWasTimeout = woke == WaitHandle.WaitTimeout;
                }
                else
                {
                    ct.WaitHandle.WaitOne(8);
                    if (--openRetryCountdown <= 0)
                    {
                        openRetryCountdown = 64;
                        IntPtr raw = SharedMemoryIO.TryOpenOutputEvent(Index);
                        if (raw != IntPtr.Zero)
                        {
                            doorbell = new AutoResetEvent(false);
                            doorbell.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(raw, ownsHandle: true);
                            waitPair = new WaitHandle[] { ct.WaitHandle, doorbell };
                        }
                    }
                }
            }
            catch { break; }
        }
        }
        finally
        {
            doorbell?.Dispose();
        }
    }

    private bool _disposed;
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMController));
    }

    /// <summary>The Switch 2 Pro composite persona builds each report on the
    /// device side, from the report its host selected and its own counters,
    /// so a caller's finished report has nowhere to go (issue #66).</summary>
    private void ThrowIfDeviceBuildsReports()
    {
        if (_switch2Protocol)
            throw new NotSupportedException(
                $"Profile '{Profile.Id}' builds its own input reports. Use SubmitState.");
    }

    /// <summary>Removes the virtual device from PnP and frees the per-controller
    /// shared memory section. Idempotent: safe to call multiple times. Called
    /// automatically when the owning <see cref="HMContext"/> is disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        // Issue #67: a key or button still held goes up before the device
        // goes away, as it would if the hardware were unplugged.
        try { SendReleaseFrames(); } catch { }
        _disposed = true;
        // Idle frames stop before the output pump, so no repeat can
        // land on a section this Dispose is about to unmap.
        try { _idleCts.Cancel(); } catch { }
        try { _idleThread?.Join(Internal.TimeoutScale.Apply(500)); } catch { }
        try { _outputCts.Cancel(); } catch { }
        try { _outputThread?.Join(Internal.TimeoutScale.Apply(500)); } catch { }
        // Drop the registration before the CTS is disposed, so a concurrent
        // unmap can never reach a disposed CTS through the registry (#45).
        // The thread is already canceled and joined above, so this only
        // needs to forget it, not stop it again.
        try { Internal.SharedMemoryIO.UnregisterOutputPump(Index); } catch { }
        try { _outputCts.Dispose(); } catch { }
        _context.OnControllerDisposing(this);
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForMultipleObjects(uint count, IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);
}
