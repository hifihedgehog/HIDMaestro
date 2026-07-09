using System;

using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>Which virtual VR controller the OpenVR driver impersonates.
/// Values match HMVR_PROFILE_* in driver\openvr\src\vr_shared_layout.h.</summary>
public enum HMVRProfile
{
    /// <summary>Minimal handed controller (system, menu, trigger, grip,
    /// haptic). No ControllerType claimed; SteamVR treats it as a generic
    /// bindable controller.</summary>
    KhrSimpleController = 0,

    /// <summary>Valve Index controller ("knuckles"): A/B buttons,
    /// analog trigger, grip value + force, thumbstick, trackpad with
    /// force, per-finger curl scalars, haptic. No skeletal stream in v1.</summary>
    ValveIndexController = 1,

    /// <summary>Windows Mixed Reality motion controller
    /// ("holographic_controller"): menu + grip buttons, trigger,
    /// clickable trackpad AND thumbstick, haptic.</summary>
    MicrosoftMotionController = 2,
}

/// <summary>Hand the virtual controller occupies. One live
/// <see cref="HMVRController"/> per hand per machine.</summary>
public enum HMVRHand
{
    Left = 0,
    Right = 1,
}

/// <summary>Button bits for <see cref="HMVRState.ButtonBits"/>. Values
/// match HMVR_BTN_* in vr_shared_layout.h. Profiles that lack a component
/// ignore its bit.</summary>
[Flags]
public enum HMVRButton : uint
{
    None = 0,
    SystemClick = 1u << 0,
    SystemTouch = 1u << 1,
    AClick = 1u << 2,
    ATouch = 1u << 3,
    BClick = 1u << 4,
    BTouch = 1u << 5,
    TriggerClick = 1u << 6,
    TriggerTouch = 1u << 7,
    GripClick = 1u << 8,
    GripTouch = 1u << 9,
    ThumbstickClick = 1u << 10,
    ThumbstickTouch = 1u << 11,
    TrackpadClick = 1u << 12,
    TrackpadTouch = 1u << 13,
    MenuClick = 1u << 14,
}

/// <summary>Slot indices for <see cref="HMVRState.Scalars"/>. Values match
/// HMVR_SCALAR_* in vr_shared_layout.h. Thumbstick/trackpad axes are
/// two-sided [-1..1]; everything else is one-sided [0..1].</summary>
public enum HMVRScalar
{
    TriggerValue = 0,
    GripValue = 1,
    GripForce = 2,
    ThumbstickX = 3,
    ThumbstickY = 4,
    TrackpadX = 5,
    TrackpadY = 6,
    TrackpadForce = 7,
    FingerIndex = 8,
    FingerMiddle = 9,
    FingerRing = 10,
    FingerPinky = 11,
}

/// <summary>One input frame for a virtual VR controller. Submit the full
/// state every frame (60-250 Hz), exactly like
/// <see cref="HMController.SubmitState"/>: the driver serves the last
/// submitted state until the next one arrives, so a missed release sticks.</summary>
public struct HMVRState
{
    /// <summary>OR of <see cref="HMVRButton"/> bits.</summary>
    public uint ButtonBits;

    /// <summary>Scalar values indexed by <see cref="HMVRScalar"/>. May be
    /// null (all zeros) or shorter than 16 (missing slots unchanged).</summary>
    public float[]? Scalars;

    /// <summary>Convenience: set or clear a button bit.</summary>
    public void SetButton(HMVRButton button, bool pressed)
    {
        if (pressed) ButtonBits |= (uint)button;
        else ButtonBits &= ~(uint)button;
    }

    /// <summary>Convenience: set a scalar slot, allocating the backing
    /// array on first use.</summary>
    public void SetScalar(HMVRScalar scalar, float value)
    {
        Scalars ??= new float[16];
        Scalars[(int)scalar] = value;
    }
}

/// <summary>A haptic pulse SteamVR requested on the virtual controller
/// (a game called TriggerHapticPulse / applied a haptic binding).</summary>
public sealed class HMVRHapticEventArgs : EventArgs
{
    /// <summary>Haptic component index on the device. Always 0 in v1
    /// (one haptic component per controller).</summary>
    public int ComponentIndex { get; init; }

    /// <summary>Pulse strength, 0..1.</summary>
    public float Amplitude { get; init; }

    /// <summary>Pulse length in seconds.</summary>
    public float Duration { get; init; }

    /// <summary>Pulse frequency in Hz.</summary>
    public float Frequency { get; init; }

    /// <summary>QPC timestamp taken inside vrserver when the event fired.</summary>
    public long TimestampQpc { get; init; }
}

/// <summary>
/// A virtual SteamVR controller, driven the same way as
/// <see cref="HMController"/>: construct, <see cref="Connect"/>, then a
/// steady <see cref="SubmitState"/> loop plus a <see cref="HapticReceived"/>
/// subscription.
///
/// <para><b>This is not a HID device.</b> There is no device node, no INF,
/// and no relation to <see cref="HMContext.CreateController(HMProfile)"/>'s
/// index pool. The device lives inside SteamVR's vrserver.exe (HIDMaestro's
/// embedded OpenVR driver); this object owns the consumer end of a
/// shared-memory channel to it. SteamVR owns the device lifetime:
/// controllers appear in SteamVR only while a consumer is connected, and
/// vanish (report disconnected) when the consumer exits or disposes.</para>
///
/// <para><b>Profile latching:</b> vrserver has no TrackedDeviceRemoved, so
/// the first profile a hand registers in a SteamVR session defines that
/// hand's component set until SteamVR restarts. Reconnecting the same hand
/// with a different profile keeps the original components.</para>
///
/// <para><b>Prerequisites:</b> <see cref="Connect"/> registers the embedded
/// OpenVR driver with SteamVR (idempotent, hash-gated) which requires the
/// process to be elevated, same as the HID path. SteamVR picks the driver
/// up immediately (vrpathreg hot-plugs a running SteamVR) or at its next
/// launch.</para>
/// </summary>
public sealed class HMVRController : IDisposable
{
    private readonly object _lock = new();
    private bool _connected;
    private bool _disposed;

    public HMVRController(HMVRProfile profile, HMVRHand hand, int handSelectionPriority = 0)
    {
        if (!Enum.IsDefined(profile))
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (!Enum.IsDefined(hand))
            throw new ArgumentOutOfRangeException(nameof(hand));

        Profile = profile;
        Hand = hand;
        HandSelectionPriority = handSelectionPriority;
    }

    public HMVRProfile Profile { get; }
    public HMVRHand Hand { get; }
    public int HandSelectionPriority { get; }

    /// <summary>Raised when SteamVR requests a haptic pulse on this
    /// controller. Fires on the SDK's VR haptic listener thread; keep the
    /// handler cheap (no blocking I/O), the same contract as
    /// <see cref="HMController.OutputReceived"/>.</summary>
    public event Action<HMVRController, HMVRHapticEventArgs>? HapticReceived;

    /// <summary>True if a SteamVR server process is currently running.</summary>
    public bool SteamVRRunning => VrDriverBuilder.IsSteamVRRunning();

    /// <summary>True while HIDMaestro's OpenVR driver (inside vrserver)
    /// has the IPC channel attached.</summary>
    public bool DriverConnected => VrSharedMemoryIO.IsDriverConnected();

    /// <summary>True after a successful <see cref="Connect"/> and before
    /// <see cref="Disconnect"/>/<see cref="Dispose"/>.</summary>
    public bool IsConnected { get { lock (_lock) return _connected; } }

    /// <summary>Ensure the embedded OpenVR driver is extracted + registered
    /// with SteamVR, open the IPC channel, and claim this hand. Idempotent.
    /// Returns true when the channel is up (SteamVR itself may attach
    /// later; watch <see cref="DriverConnected"/>).</summary>
    /// <exception cref="InvalidOperationException">Another live process
    /// owns the VR channel, or this hand is already active.</exception>
    public bool Connect()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_connected) return true;

            // Top-level VR install step, deliberately independent of
            // DriverBuilder.FullDeploy (whose manifest-hash fast path
            // would shadow anything appended after it).
            VrDriverBuilder.EnsureInstalled();

            VrSharedMemoryIO.EnsureChannel();
            VrSharedMemoryIO.RegisterHand((int)Hand, (uint)Profile, HandSelectionPriority);
            VrSharedMemoryIO.SetHapticSink((int)Hand, OnHaptic);
            _connected = true;
            return true;
        }
    }

    /// <summary>Release this hand. The virtual controller reports
    /// disconnected in SteamVR. Idempotent.</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            if (!_connected) return;
            VrSharedMemoryIO.SetHapticSink((int)Hand, null);
            VrSharedMemoryIO.UnregisterHand((int)Hand);
            _connected = false;
        }
    }

    /// <summary>Push the next input frame. Full-state snapshot semantics;
    /// submit at a steady rate (60-250 Hz).</summary>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    public void SubmitState(in HMVRState state)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_connected)
                throw new InvalidOperationException("Connect() before SubmitState().");
        }
        VrSharedMemoryIO.WriteFrame((int)Hand, state.ButtonBits, state.Scalars);
    }

    private void OnHaptic(float amplitude, float duration, float frequency, long timestampQpc)
    {
        HapticReceived?.Invoke(this, new HMVRHapticEventArgs
        {
            ComponentIndex = 0,
            Amplitude = amplitude,
            Duration = duration,
            Frequency = frequency,
            TimestampQpc = timestampQpc,
        });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMVRController));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Disconnect();
    }
}
