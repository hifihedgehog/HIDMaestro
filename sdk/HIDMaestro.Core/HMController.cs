using System;
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
    private uint _inputSeqNo;

    // Output passthrough reader (rumble/haptics/FFB) — background thread
    // poll-reads the per-controller output section and raises OutputReceived.
    private readonly IntPtr _outputView;
    private readonly Thread? _outputThread;
    private readonly CancellationTokenSource _outputCts = new();

    // Empty 14-byte GIP packet — required by SharedMemoryIO.WriteInputFrame
    // (used by the XUSB companion to populate XInput state). Plain HID
    // profiles don't have an XUSB companion, so an all-zero GIP slice is
    // a valid no-op for them.
    private static readonly byte[] s_emptyGip = new byte[14];

    /// <summary>Raised on the SDK's output-polling thread whenever a host
    /// application sends a rumble, haptic, FFB, feature, or LED command to
    /// this virtual controller. Subscribers must be thread-safe.</summary>
    public event Action<HMController, HMOutputPacket>? OutputReceived;

    internal HMController(HMContext context, int index, HMProfile profile, string? instanceId)
    {
        _context = context;
        Index = index;
        InstanceId = instanceId;
        Profile = profile;

        _reportBuilder = HidReportBuilder.Parse(profile.Inner.GetDescriptorBytes()!);
        _inputView = SharedMemoryIO.EnsureInputMapping(index);

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

        // HMGamepadState uses [-1..+1] for sticks (Y-up convention) and
        // [0..1] for triggers. HidReportBuilder.BuildReport expects [0..1]
        // normalized values for axes (centered at 0.5) and 0..1 for triggers.
        // Convert: x -> (x + 1) / 2 with Y inverted because HID Y is down-positive.
        double Map(float v) => Math.Clamp((v + 1.0) / 2.0, 0.0, 1.0);

        byte[] report = _reportBuilder.BuildReport(
            leftX: Map(state.LeftStickX),
            leftY: Map(-state.LeftStickY),  // invert: HID Y is down-positive
            rightX: Map(state.RightStickX),
            rightY: Map(-state.RightStickY),
            leftTrigger: Math.Clamp(state.LeftTrigger, 0f, 1f),
            rightTrigger: Math.Clamp(state.RightTrigger, 0f, 1f),
            hatValue: (int)state.Hat,
            buttonMask: (uint)state.Buttons);

        SharedMemoryIO.WriteInputFrame(
            _inputView, ref _inputSeqNo, report, report.Length, s_emptyGip);
    }

    /// <summary>Push a raw HID input report. Use this for exotic features
    /// that <see cref="HMGamepadState"/> doesn't model — touchpad coordinates,
    /// gyroscope, sensor packets, vendor extensions. The first byte must be
    /// the HID Report ID (or 0 if the descriptor declares no report IDs).</summary>
    public void SubmitRawReport(ReadOnlySpan<byte> report)
    {
        ThrowIfDisposed();
        if (report.Length == 0) throw new ArgumentException("Report cannot be empty.", nameof(report));
        if (report.Length > 64) throw new ArgumentException("Report exceeds the 64-byte shared section payload.", nameof(report));

        byte[] copy = report.ToArray();
        SharedMemoryIO.WriteInputFrame(
            _inputView, ref _inputSeqNo, copy, copy.Length, s_emptyGip);
    }

    /// <summary>Background polling loop that reads from the per-controller
    /// output shared section and raises <see cref="OutputReceived"/> for
    /// each new packet. Sleeps 8 ms between polls (≈125 Hz) which is
    /// comfortably above the rate at which any host app sends output
    /// packets and well below the cost threshold for an idle thread.</summary>
    private void OutputPollLoop()
    {
        if (_outputView == IntPtr.Zero) return;
        uint lastSeq = 0;
        byte[] buf = new byte[256];
        var ct = _outputCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (SharedMemoryIO.TryReadOutputFrame(_outputView, ref lastSeq,
                        out byte source, out byte reportId, out int dataSize, buf))
                {
                    var data = new ReadOnlyMemory<byte>(buf, 0, dataSize);
                    var pkt = new HMOutputPacket((HMOutputSource)source, reportId, data, lastSeq);
                    OutputReceived?.Invoke(this, pkt);
                }
            }
            catch
            {
                // Swallow polling errors so a transient kernel-side failure
                // doesn't kill the reader thread.
            }
            try { Thread.Sleep(8); } catch { break; }
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
        try { _outputThread?.Join(500); } catch { }
        _context.OnControllerDisposing(this);
    }
}
