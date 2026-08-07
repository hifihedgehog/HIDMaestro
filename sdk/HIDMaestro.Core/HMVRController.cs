using System;
using System.Diagnostics;
using System.Threading;

using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>Which hand a virtual VR controller occupies.</summary>
public enum HMVRHand
{
    Left = 0,
    Right = 1,
}

/// <summary>Buttons of the generic HIDMaestro VR controller profile.
/// Values mirror the driver's <c>HMVR_BTN_*</c> bits
/// (driver/openvr/src/vr_protocol.h); the wire is a plain 32-bit mask.</summary>
[Flags]
public enum HMVRButton : uint
{
    None = 0,
    System = 1u << 0,
    A = 1u << 1,
    ATouch = 1u << 2,
    B = 1u << 3,
    BTouch = 1u << 4,
    TriggerClick = 1u << 5,
    GripClick = 1u << 6,
    StickClick = 1u << 7,
}

/// <summary>Input state for one VR hand. Sticks are two-sided [-1..+1];
/// trigger and grip are one-sided [0..1], matching the scalar units the
/// driver creates each component with.</summary>
public struct HMVRHandState
{
    public HMVRButton Buttons;
    public float Trigger;
    public float Grip;
    public float StickX;
    public float StickY;

    /// <summary>Optional pose override, in SteamVR's standing universe:
    /// meters, +Y up, -Z forward. When false the driver anchors the hand
    /// ahead of the headset (turn your head to point), which is the
    /// right default for consumers that only map buttons.</summary>
    public bool PoseValid;
    public float PositionX, PositionY, PositionZ;
    /// <summary>Unit quaternion, W first. Identity = (1,0,0,0).</summary>
    public float OrientationW, OrientationX, OrientationY, OrientationZ;
}

/// <summary>Both hands, submitted atomically once per frame.</summary>
public struct HMVRState
{
    public HMVRHandState Left;
    public HMVRHandState Right;
}

/// <summary>Haptic pulse the running VR app requested on a virtual
/// controller, surfaced from the OpenVR driver.</summary>
public sealed class HMVRHapticEventArgs : EventArgs
{
    public HMVRHand Hand { get; init; }
    public float DurationSeconds { get; init; }
    public float Frequency { get; init; }
    public float Amplitude { get; init; }
    public long TimestampQpc { get; init; }
}

/// <summary>Real headset pose, published by the driver every server frame
/// so consumers can use head motion as an input source (PadForge #49's
/// lean-to-steer). Position in meters, orientation as a unit quaternion
/// (W,X,Y,Z), SteamVR standing universe.</summary>
public struct HMVRHmdPose
{
    public bool Valid;
    public float PositionX, PositionY, PositionZ;
    public float OrientationW, OrientationX, OrientationY, OrientationZ;
}

/// <summary>A pair of virtual VR controllers inside SteamVR (issue #32).
///
/// <para>This is a sibling of <see cref="HMController"/>, not a subclass:
/// there is no HID descriptor, no devnode, no kernel driver. The device
/// is an OpenVR driver DLL that SteamVR's vrserver loads; this class owns
/// the consumer end of its shared-memory channel. One instance drives
/// BOTH hands, the SteamVR convention (a hand pair is one logical unit),
/// which also keeps the IPC to a single frame.</para>
///
/// <para>Lifecycle: <see cref="HMVR.IsSteamVRInstalled"/> and
/// <see cref="HMVR.EnsureDriverRegistered"/> handle discovery and the
/// one-time vrpathreg registration. Constructing this class creates the
/// IPC section and claims it; the driver (loaded whenever SteamVR runs)
/// notices the claim within its bootstrap poll and registers the
/// controllers with vrserver. Disposal releases the claim, and the driver
/// flips the controllers to disconnected. SteamVR itself may start before
/// or after this object exists; both orders work because each side's
/// attach loop polls for the other.</para></summary>
public sealed class HMVRController : IDisposable
{
    private readonly VrSharedMemory.Channel _channel;
    private readonly byte[] _frame = new byte[VrSharedMemory.InputFrameSize];
    private readonly Thread _hapticThread;
    private readonly CancellationTokenSource _cts = new();
    private ulong _hapticCursor;
    private bool _disposed;

    /// <summary>Raised on a background thread for each haptic pulse a VR
    /// app plays on either virtual controller.</summary>
    public event EventHandler<HMVRHapticEventArgs>? HapticReceived;

    /// <summary>Creates the IPC channel and activates both hands. Throws
    /// <see cref="InvalidOperationException"/> when another consumer owns
    /// the channel, or when the section exists with a mismatched ABI.</summary>
    public HMVRController()
    {
        _channel = VrSharedMemory.Create();
        VrSharedMemory.WriteConfig(_channel,
            handsActive: 0x1 | 0x2 /* HMVR_HAND_LEFT_ACTIVE | RIGHT */);

        _hapticThread = new Thread(HapticLoop)
        {
            IsBackground = true,
            Name = "HMVRController.HapticLoop",
        };
        _hapticThread.Start();
    }

    /// <summary>True once the OpenVR driver inside vrserver has attached
    /// to the channel and reported itself active. False when SteamVR is
    /// not running (yet).</summary>
    public bool DriverConnected
    {
        get
        {
            var p = new float[3]; var q = new float[4];
            return VrSharedMemory.TryReadStatus(_channel, out uint active, out _, out _, p, q, out ulong hb)
                   && active != 0 && QpcAgeSeconds(hb) < 5.0;
        }
    }

    /// <summary>True while both virtual controllers are registered and
    /// connected inside SteamVR.</summary>
    public bool ControllersLive
    {
        get
        {
            var p = new float[3]; var q = new float[4];
            return VrSharedMemory.TryReadStatus(_channel, out _, out uint live, out _, p, q, out _)
                   && live == 0x3;
        }
    }

    /// <summary>Latest real headset pose, or <c>Valid == false</c> when no
    /// HMD is tracking (including the headless null-driver test rig).</summary>
    public HMVRHmdPose GetHmdPose()
    {
        var p = new float[3]; var q = new float[4];
        if (!VrSharedMemory.TryReadStatus(_channel, out _, out _, out uint poseValid, p, q, out _))
            return default;
        return new HMVRHmdPose
        {
            Valid = poseValid != 0,
            PositionX = p[0], PositionY = p[1], PositionZ = p[2],
            OrientationW = q[0], OrientationX = q[1], OrientationY = q[2], OrientationZ = q[3],
        };
    }

    /// <summary>Submit one frame for both hands. Call at your input rate;
    /// the driver consumes latest-wins at the SteamVR server tick.</summary>
    public void SubmitState(in HMVRState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> f = _frame;
        BitConverter.TryWriteBytes(f, (ulong)Stopwatch.GetTimestamp());
        PackHand(f.Slice(8, VrSharedMemory.ControllerStateSize), in state.Left);
        PackHand(f.Slice(8 + VrSharedMemory.ControllerStateSize, VrSharedMemory.ControllerStateSize), in state.Right);
        VrSharedMemory.WriteInputFrame(_channel, _frame);
    }

    private static void PackHand(Span<byte> dst, in HMVRHandState h)
    {
        dst.Clear();
        BitConverter.TryWriteBytes(dst, (uint)h.Buttons);
        BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateScalarsOffset + 0 * 4), h.Trigger);
        BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateScalarsOffset + 1 * 4), h.Grip);
        BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateScalarsOffset + 2 * 4), h.StickX);
        BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateScalarsOffset + 3 * 4), h.StickY);
        if (h.PoseValid)
        {
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StatePoseValidOffset), 1u);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StatePositionOffset + 0), h.PositionX);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StatePositionOffset + 4), h.PositionY);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StatePositionOffset + 8), h.PositionZ);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateOrientationOffset + 0), h.OrientationW);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateOrientationOffset + 4), h.OrientationX);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateOrientationOffset + 8), h.OrientationY);
            BitConverter.TryWriteBytes(dst.Slice(VrSharedMemory.StateOrientationOffset + 12), h.OrientationZ);
        }
    }

    private void HapticLoop()
    {
        var token = _cts.Token;
        // The driver signals the haptic event after each ring publish; the
        // 250 ms timeout is the degraded-mode bound if a signal is ever
        // missed, the same posture as the driver-side worker's 500 ms
        // recycle wait.
        using var hapticSignal = new AutoResetEvent(false)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(_channel.HapticEvent, ownsHandle: false),
        };
        var waits = new WaitHandle[] { token.WaitHandle, hapticSignal };
        while (!token.IsCancellationRequested)
        {
            while (VrSharedMemory.TryReadHaptic(_channel, ref _hapticCursor,
                       out uint hand, out float dur, out float freq, out float amp, out ulong ts))
            {
                HapticReceived?.Invoke(this, new HMVRHapticEventArgs
                {
                    Hand = hand == 0 ? HMVRHand.Left : HMVRHand.Right,
                    DurationSeconds = dur,
                    Frequency = freq,
                    Amplitude = amp,
                    TimestampQpc = (long)ts,
                });
            }
            WaitHandle.WaitAny(waits, 250);
        }
    }

    private static double QpcAgeSeconds(ulong qpc)
    {
        if (qpc == 0) return double.MaxValue;
        return (double)(Stopwatch.GetTimestamp() - (long)qpc) / Stopwatch.Frequency;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _hapticThread.Join(1000);
        _cts.Dispose();
        _channel.Dispose();
    }
}
