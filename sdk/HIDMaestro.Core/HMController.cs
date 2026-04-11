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
        _reportBuilder.ButtonMap = profile.Inner.ButtonMap;
        _inputView = SharedMemoryIO.EnsureInputMapping(index);
        _inputEvent = SharedMemoryIO.GetInputEvent(index);

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

        // HMGamepadState uses [-1..+1] for sticks and [0..1] for triggers.
        // HidReportBuilder.BuildReport expects [0..1] normalized values for
        // axes (centered at 0.5). Map directly with no sign flip — the
        // proven test app encoder passed values straight through to
        // BuildReport without inversion, and it produced correct circles
        // in joy.cpl / Gamepad Tester for every profile type. HID Y axis
        // direction is descriptor-defined, not a universal convention, so
        // the SDK keeps the contract simple: caller's LeftStickY = +1
        // becomes HID logical max for that field.
        double Map(float v) => Math.Clamp((v + 1.0) / 2.0, 0.0, 1.0);

        double mlx = Map(state.LeftStickX);
        double mly = Map(state.LeftStickY);
        double mrx = Map(state.RightStickX);
        double mry = Map(state.RightStickY);
        double mlt = Math.Clamp(state.LeftTrigger, 0f, 1f);
        double mrt = Math.Clamp(state.RightTrigger, 0f, 1f);

        byte[] report = _reportBuilder.BuildReport(
            leftX: mlx, leftY: mly,
            rightX: mrx, rightY: mry,
            leftTrigger: mlt, rightTrigger: mrt,
            hatValue: (int)state.Hat,
            buttonMask: (uint)state.Buttons);

        // Pack the GIP-format buffer that the XUSB companion (HMXInput.dll)
        // reads for IOCTL_XUSB_GET_STATE. Only meaningful for non-xinputhid
        // Xbox profiles (Xbox 360 wired etc.) — for other profiles no XUSB
        // companion is bound and the bytes are unused. We pack unconditionally
        // because checking the profile per-frame is more expensive than
        // writing 14 bytes.
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
        // Button high byte: Back, Start, hat in upper bits (companion-specific)
        byte btnHigh = 0;
        if ((b & (uint)HMButton.Back)  != 0) btnHigh |= 0x01;
        if ((b & (uint)HMButton.Start) != 0) btnHigh |= 0x02;
        _gipBuf[13] = btnHigh;

        // Strip the Report ID byte (if any) before writing the HID native
        // bytes. BuildReport puts the Report ID at position 0 when the
        // descriptor declares one. The driver expects the shared memory
        // section to contain only data bytes — the kernel HID stack adds
        // the Report ID prefix when delivering. dataLen capped at 64 to
        // match the shared memory section's data area size.
        int dataStart = _reportBuilder.InputReportId != 0 ? 1 : 0;
        int dataLen = Math.Min(report.Length - dataStart, 64);
        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo, report, dataLen, _gipBuf, dataStart);
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
        if (report.Length > 64) throw new ArgumentException("Report exceeds the 64-byte shared section payload.", nameof(report));

        byte[] copy = report.ToArray();
        // Raw mode reuses the GIP buffer at whatever state SubmitState last
        // left it in (or zero if SubmitState was never called) — raw consumers
        // are expected to also call SubmitState if they need GIP/XInput.
        SharedMemoryIO.WriteInputFrame(
            _inputView, _inputEvent, ref _inputSeqNo, copy, copy.Length, _gipBuf);
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
