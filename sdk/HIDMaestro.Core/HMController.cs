using System;

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
    public HMProfile Profile { get; }

    /// <summary>Raised on the SDK's output-polling thread whenever a host
    /// application sends a rumble, haptic, FFB, feature, or LED command to
    /// this virtual controller. Subscribers must be thread-safe.</summary>
    public event Action<HMController, HMOutputPacket>? OutputReceived;

    internal HMController(HMContext context, int index, HMProfile profile)
    {
        _context = context;
        Index = index;
        Profile = profile;
    }

    /// <summary>The XInput slot this controller occupies, if any. Non-Xbox
    /// profiles return null. Xbox profiles return the assigned slot (0..3)
    /// once the controller has been registered with xinput1_4. May be null
    /// briefly during creation; settles within a few hundred ms of
    /// <see cref="HMContext.CreateController"/> returning.</summary>
    public int? XInputSlot { get; internal set; }

    /// <summary>Push the next input frame to the virtual controller.
    /// The SDK encodes <paramref name="state"/> into the active profile's
    /// HID report layout and publishes it via shared memory.</summary>
    public void SubmitState(in HMGamepadState state)
    {
        ThrowIfDisposed();
        // TODO: implement once orchestration is extracted
        throw new NotImplementedException("SubmitState — pending orchestration extraction");
    }

    /// <summary>Push a raw HID input report. Use this for exotic features
    /// that <see cref="HMGamepadState"/> doesn't model — touchpad coordinates,
    /// gyroscope, sensor packets, vendor extensions. The first byte must be
    /// the HID Report ID (or 0 if the descriptor declares no report IDs).</summary>
    public void SubmitRawReport(ReadOnlySpan<byte> report)
    {
        ThrowIfDisposed();
        // TODO: implement once orchestration is extracted
        throw new NotImplementedException("SubmitRawReport — pending orchestration extraction");
    }

    internal void RaiseOutputReceived(in HMOutputPacket packet)
        => OutputReceived?.Invoke(this, packet);

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
        _context.OnControllerDisposing(this);
    }
}
