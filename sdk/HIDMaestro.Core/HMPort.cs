using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

namespace HIDMaestro;

/// <summary>The controller ports of a device that carries several
/// controllers in one report, such as a multi-port adapter, a multitap or a
/// receiver, as its profile's <c>extendedReport.ports</c> declares them. Get
/// it from <see cref="HMController.Ports"/>. A profile with one controller
/// has no ports, and the set is empty.
///
/// <para>Each port has its own connection, input and output. The device
/// sends one report for all of them, so a submit to one port leaves the
/// others as they were. A port's number never changes: a program that puts a
/// returning controller back on its old port gets that port on the wire.
/// <see cref="HMController.SubmitState"/> submits to port 1.</para>
///
/// <para>A port submit rewrites the whole report from the ports' state, so it
/// replaces a report sent with
/// <see cref="HMController.SubmitRawExtendedReport"/>. A program builds its
/// own reports with one or the other.</para></summary>
public sealed class HMPortSet : IReadOnlyList<HMPort>
{
    private readonly HMController _controller;
    private readonly PortLayout? _in;
    private readonly PortLayout? _out;
    private readonly HMPort[] _ports;

    // The input side, guarded by the controller's submit lock.
    private readonly byte[] _report = Array.Empty<byte>();
    private readonly int[] _connection = Array.Empty<int>();
    private readonly bool[] _flags = Array.Empty<bool>();
    private readonly byte[] _scratch = Array.Empty<byte>();
    private readonly VendorBlobCodec.EncoderState[] _encoders = Array.Empty<VendorBlobCodec.EncoderState>();

    // The output side: each port's last output field values, written only
    // by the controller's output thread.
    private readonly int[][] _output = Array.Empty<int[]>();
    private readonly byte _outputReportId;

    internal HMPortSet(HMController controller, ControllerProfile profile)
    {
        _controller = controller;
        profile.ValidatePorts();
        _in = PortLayout.Of(profile.ExtendedReport, $"Profile '{profile.Id}': extendedReport", output: false);
        _out = PortLayout.Of(profile.ExtendedOutputReport, $"Profile '{profile.Id}': extendedOutputReport", output: true);
        Flags = new HMPortFlags(this, _in);
        if (_in == null)
        {
            _ports = Array.Empty<HMPort>();
            return;
        }

        var spec = profile.ExtendedReport!;
        _report = new byte[spec.Size];
        _report[0] = spec.ReportIdByte;
        _connection = new int[_in.Count];
        _flags = new bool[_in.Flags.Count];
        for (int f = 0; f < _flags.Length; f++) _flags[f] = _in.FlagDefault(f);
        _scratch = new byte[_in.Stride];
        _encoders = new VendorBlobCodec.EncoderState[_in.Count];
        _ports = new HMPort[_in.Count];
        for (int p = 0; p < _in.Count; p++)
        {
            _encoders[p] = new VendorBlobCodec.EncoderState();
            _ports[p] = new HMPort(this, p + 1);
            if (_in.StatusByte >= 0) _report[_in.BlockAt(p) + _in.StatusByte] = _in.Status(0, _flags);
        }
        if (_out != null)
        {
            _out.PrepareOutput("extendedOutputReport");
            _outputReportId = profile.ExtendedOutputReport!.ReportIdByte;
            _output = new int[_in.Count][];
            for (int p = 0; p < _in.Count; p++) _output[p] = new int[_out.OutputNames.Count];
        }
    }

    /// <summary>The controller these ports belong to.</summary>
    public HMController Controller => _controller;

    /// <summary>The number of ports.</summary>
    public int Count => _ports.Length;

    /// <summary>Port <paramref name="index"/> + 1. <c>Ports[0]</c> is port 1.</summary>
    public HMPort this[int index] => _ports[index];

    public IEnumerator<HMPort> GetEnumerator() => ((IEnumerable<HMPort>)_ports).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _ports.GetEnumerator();

    /// <summary>Device-wide switches every port's status byte shows, by the
    /// names the profile gives them, such as a second cable that powers the
    /// controllers' motors.</summary>
    public HMPortFlags Flags { get; }

    /// <summary>The names of the connections a port can hold, in the
    /// profile's order. The first is the empty port, and the second is the one
    /// <see cref="HMPort.Connect"/> uses when given none.</summary>
    public IReadOnlyList<string> Connections => _in?.Connections ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>The names of each port's output fields, such as a motor
    /// command, as the profile's <c>extendedOutputReport.ports</c> declares
    /// them.</summary>
    public IReadOnlyList<string> OutputNames => _out?.OutputNames ?? (IReadOnlyList<string>)Array.Empty<string>();

    // ── Input ───────────────────────────────────────────────────────────

    /// <summary>Publish the whole report once the ports are set up, so the
    /// device starts from it rather than from nothing. Caller holds the
    /// submit lock.</summary>
    internal void PublishInitialLocked()
    {
        if (_in != null) _controller.WritePortsReportLocked(_report, direct: false);
    }

    /// <summary>Hand the report as it stands to the idle pump, which
    /// repeats it until the next submit. Caller holds the submit
    /// lock.</summary>
    internal void RememberReportLocked()
    {
        if (_in != null) _controller.RememberPortsReportLocked(_report);
    }

    internal string ConnectionOf(int port)
    {
        RequireStatus();
        lock (_controller.SubmitLock) return _in!.Connections[_connection[port]];
    }

    internal bool IsConnected(int port)
    {
        RequireStatus();
        lock (_controller.SubmitLock) return _connection[port] != 0;
    }

    internal void Connect(int port, string? connection)
    {
        RequireStatus();
        int index = connection == null ? 1 : _in!.ConnectionIndex(connection);
        lock (_controller.SubmitLock)
        {
            _controller.ThrowIfDisposedInternal();
            bool wasEmpty = _connection[port] == 0;
            SetConnectionLocked(port, index);
            // A controller plugged into an empty port reports itself at rest
            // until the program submits: hosts take its origin and SDL its
            // calibration from its first report, and zeros put both sticks
            // in a corner.
            if (wasEmpty && index != 0) EncodeLocked(port, default);
            _controller.WritePortsReportLocked(_report, direct: true);
        }
    }

    internal void Connect(int port, string? connection, in HMGamepadState state)
    {
        RequireStatus();
        int index = connection == null ? 1 : _in!.ConnectionIndex(connection);
        if (index == 0)
            throw new ArgumentException("The empty connection holds no controller, so it takes no input. Use Disconnect.", nameof(connection));
        lock (_controller.SubmitLock)
        {
            _controller.ThrowIfDisposedInternal();
            SetConnectionLocked(port, index);
            EncodeLocked(port, in state);
            _controller.WritePortsReportLocked(_report, direct: true);
        }
    }

    /// <summary>Set the port's data bytes from a gamepad state, the six
    /// simple-slot values resolved as SubmitState resolves them, leaving its
    /// connection alone. Caller holds the submit lock.</summary>
    private void EncodeLocked(int port, in HMGamepadState state)
    {
        var layout = _in!;
        _controller.ResolveSlots(in state, out float lx, out float ly, out float rx,
                                 out float ry, out float lt, out float rt);
        VendorBlobCodec.EncodeInput(layout.FieldSpec, in state, lx, ly, rx, ry, lt, rt, _scratch, _encoders[port]);
        int block = layout.BlockAt(port);
        if (layout.DataLo >= 0)
        {
            _scratch.AsSpan(layout.DataLo, layout.DataLength).CopyTo(_report.AsSpan(block + layout.DataLo));
            return;
        }
        for (int i = 0; i < layout.Stride; i++)
            if (i != layout.StatusByte) _report[block + i] = _scratch[i];
    }

    internal void Disconnect(int port)
    {
        RequireStatus();
        lock (_controller.SubmitLock)
        {
            _controller.ThrowIfDisposedInternal();
            SetConnectionLocked(port, 0);
            _controller.WritePortsReportLocked(_report, direct: true);
        }
    }

    private void RequireStatus()
    {
        if (_in == null || _in.StatusByte < 0)
            throw new InvalidOperationException(
                $"Profile '{_controller.Profile.Id}' declares no port status byte, so its ports have no connection.");
    }

    /// <summary>Caller holds the submit lock.</summary>
    private void SetConnectionLocked(int port, int connection)
    {
        // The status byte first: the port records a connection only once
        // the report shows it.
        int block = _in!.BlockAt(port);
        _report[block + _in.StatusByte] = _in.Status(connection, _flags);
        _connection[port] = connection;
        // An empty port's data bytes are zero.
        if (connection == 0) ClearData(block);
    }

    private void ClearData(int block)
    {
        if (_in!.DataLo >= 0)
        {
            _report.AsSpan(block + _in.DataLo, _in.DataLength).Clear();
            return;
        }
        for (int i = 0; i < _in.Stride; i++)
            if (i != _in.StatusByte) _report[block + i] = 0;
    }

    /// <summary>One port from the SDK's gamepad state, the six simple-slot
    /// values resolved as SubmitState resolves them. An empty port takes the
    /// profile's default connection first. Caller holds the submit lock.</summary>
    internal void SubmitLocked(int port, in HMGamepadState state, bool direct,
                               float lx, float ly, float rx, float ry, float lt, float rt)
    {
        var layout = _in!;
        VendorBlobCodec.EncodeInput(layout.FieldSpec, in state, lx, ly, rx, ry, lt, rt, _scratch, _encoders[port]);
        int block = layout.BlockAt(port);
        if (layout.DataLo >= 0)
        {
            _scratch.AsSpan(layout.DataLo, layout.DataLength).CopyTo(_report.AsSpan(block + layout.DataLo));
        }
        else
        {
            for (int i = 0; i < layout.Stride; i++)
                if (i != layout.StatusByte) _report[block + i] = _scratch[i];
        }
        if (layout.StatusByte >= 0 && _connection[port] == 0) SetConnectionLocked(port, 1);
        _controller.WritePortsReportLocked(_report, direct);
    }

    internal void Submit(int port, in HMGamepadState state)
    {
        _controller.ThrowIfDisposedInternal();
        var latency = _controller.OnSubmitLatencyMicros;
        long start = latency != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        lock (_controller.SubmitLock)
        {
            _controller.ThrowIfDisposedInternal();
            _controller.ResolveSlots(in state, out float lx, out float ly, out float rx,
                                     out float ry, out float lt, out float rt);
            SubmitLocked(port, in state, direct: true, lx, ly, rx, ry, lt, rt);
        }
        if (latency != null)
            latency((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
    }

    internal void SubmitRaw(int port, ReadOnlySpan<byte> data)
    {
        var layout = _in!;
        int expected = layout.DataLo >= 0 ? layout.DataLength : layout.Stride - (layout.StatusByte >= 0 ? 1 : 0);
        if (data.Length != expected)
            throw new ArgumentException(
                $"A port of profile '{_controller.Profile.Id}' takes {expected} data bytes, not {data.Length}.", nameof(data));
        lock (_controller.SubmitLock)
        {
            _controller.ThrowIfDisposedInternal();
            int block = layout.BlockAt(port);
            if (layout.DataLo >= 0)
            {
                data.CopyTo(_report.AsSpan(block + layout.DataLo));
            }
            else
            {
                int at = 0;
                for (int i = 0; i < layout.Stride; i++)
                    if (i != layout.StatusByte) _report[block + i] = data[at++];
            }
            if (layout.StatusByte >= 0 && _connection[port] == 0) SetConnectionLocked(port, 1);
            _controller.WritePortsReportLocked(_report, direct: true);
        }
    }

    internal bool GetFlag(int flag)
    {
        lock (_controller.SubmitLock) return _flags[flag];
    }

    internal void SetFlag(int flag, bool on)
    {
        lock (_controller.SubmitLock)
        {
            _controller.ThrowIfDisposedInternal();
            if (_flags[flag] == on) return;
            _flags[flag] = on;
            for (int p = 0; p < _ports.Length; p++)
                _report[_in!.BlockAt(p) + _in.StatusByte] = _in.Status(_connection[p], _flags);
            _controller.WritePortsReportLocked(_report, direct: true);
        }
    }

    // ── Output ──────────────────────────────────────────────────────────

    internal int GetOutput(int port, string name)
    {
        int field = OutputField(name);
        return Volatile.Read(ref _output[port][field]);
    }

    private int OutputField(string name)
    {
        if (_out == null)
            throw new InvalidOperationException(
                $"Profile '{_controller.Profile.Id}' declares no extendedOutputReport.ports, so its ports have no output.");
        for (int i = 0; i < _out.OutputNames.Count; i++)
            if (string.Equals(_out.OutputNames[i], name, StringComparison.Ordinal)) return i;
        throw new ArgumentException(
            $"'{name}' is not an output field of this device's ports: {string.Join(", ", _out.OutputNames)}.", nameof(name));
    }

    /// <summary>A host's output report, from the output thread. A report of
    /// the declared ID that covers every port updates each port's fields,
    /// and each change raises that port's <see cref="HMPort.OutputChanged"/>.
    /// A shorter one sets nothing.</summary>
    internal void OnOutputReport(byte reportId, ReadOnlySpan<byte> data)
    {
        var layout = _out;
        if (layout == null || reportId != _outputReportId) return;
        int extent = layout.BlockAt(layout.Count);
        if (1 + data.Length < extent) return;
        // The ring keeps the report's first byte apart. Rejoined, a block's
        // offsets are the report's own.
        Span<byte> report = stackalloc byte[extent];
        report[0] = reportId;
        data[..(extent - 1)].CopyTo(report[1..]);
        for (int p = 0; p < layout.Count; p++)
        {
            var block = report.Slice(layout.BlockAt(p), layout.Stride);
            for (int f = 0; f < _output[p].Length; f++)
                SetOutput(p, f, layout.ReadOutput(f, block));
        }
    }

    /// <summary>The device returned to its initial state, from the output
    /// thread: every output field reads zero again.</summary>
    internal void OnDeviceReset()
    {
        for (int p = 0; p < _output.Length; p++)
            for (int f = 0; f < _output[p].Length; f++)
                SetOutput(p, f, 0);
    }

    private void SetOutput(int port, int field, int value)
    {
        if (Interlocked.Exchange(ref _output[port][field], value) == value) return;
        _ports[port].RaiseOutputChanged(_out!.OutputNames[field], value);
    }

    /// <summary>Add each port's output fields to an OutputDecoded dictionary
    /// under "port{n}.{name}".</summary>
    internal void AddDecoded(Dictionary<string, object> fields, byte reportId, ReadOnlySpan<byte> data)
    {
        var layout = _out;
        if (layout == null || reportId != _outputReportId) return;
        int extent = layout.BlockAt(layout.Count);
        if (1 + data.Length < extent) return;
        Span<byte> report = stackalloc byte[extent];
        report[0] = reportId;
        data[..(extent - 1)].CopyTo(report[1..]);
        for (int p = 0; p < layout.Count; p++)
        {
            var block = report.Slice(layout.BlockAt(p), layout.Stride);
            for (int f = 0; f < layout.OutputNames.Count; f++)
                fields[layout.OutputKey(p, f)] = layout.ReadOutput(f, block);
        }
    }

    internal int FlagIndex(string name) => _in != null
        ? _in.FlagIndex(name)
        : throw new ArgumentException($"'{name}' is not a flag this device declares: none.", nameof(name));
}

/// <summary>One controller port of an <see cref="HMPortSet"/>.</summary>
public sealed class HMPort
{
    private readonly HMPortSet _set;

    internal HMPort(HMPortSet set, int number)
    {
        _set = set;
        Number = number;
    }

    /// <summary>The port's number, from 1, as the device and its hosts
    /// number them.</summary>
    public int Number { get; }

    /// <summary>The ports this one belongs to.</summary>
    public HMPortSet Ports => _set;

    /// <summary>What the port holds: one of <see cref="HMPortSet.Connections"/>.</summary>
    /// <exception cref="InvalidOperationException">The device's ports carry
    /// no status byte.</exception>
    public string Connection => _set.ConnectionOf(Number - 1);

    /// <summary>True unless the port holds the empty connection.</summary>
    public bool IsConnected => _set.IsConnected(Number - 1);

    /// <summary>Plug something into the port: a connection named in
    /// <see cref="HMPortSet.Connections"/>, or the profile's default, the
    /// second it lists, when <paramref name="connection"/> is null. A port
    /// that held nothing reports a controller at rest, sticks centered and
    /// triggers and buttons released, until the next submit. A port that
    /// held something keeps its input as last submitted. The other ports are
    /// not touched. The empty connection's name does what
    /// <see cref="Disconnect"/> does.</summary>
    /// <exception cref="ArgumentException">The device declares no
    /// connection of that name.</exception>
    public void Connect(string? connection = null) => _set.Connect(Number - 1, connection);

    /// <summary>Plug something into the port already holding
    /// <paramref name="state"/>: the connection and the input go out in one
    /// report, as a controller plugged in with a button held reports it from
    /// its first report. <paramref name="connection"/> is as for
    /// <see cref="Connect(string?)"/>.</summary>
    /// <exception cref="ArgumentException">The device declares no
    /// connection of that name, or it names the empty connection.</exception>
    public void Connect(string? connection, in HMGamepadState state) => _set.Connect(Number - 1, connection, in state);

    /// <summary>Unplug whatever the port holds. Its data bytes go to zero, as
    /// an empty port's are. The other ports are not touched.</summary>
    public void Disconnect() => _set.Disconnect(Number - 1);

    /// <summary>Set the port's input from the SDK's gamepad state, through
    /// the profile's port fields. An empty port takes the profile's default
    /// connection first.</summary>
    public void SubmitState(in HMGamepadState state) => _set.Submit(Number - 1, in state);

    /// <summary>Set the port's data bytes exactly, in the device's own
    /// units: the profile's <c>ports.data</c> range, or every byte of the
    /// port's block but its status byte. An empty port takes the profile's
    /// default connection first.</summary>
    /// <exception cref="ArgumentException">Not the port's number of data
    /// bytes.</exception>
    public void SubmitRaw(ReadOnlySpan<byte> data) => _set.SubmitRaw(Number - 1, data);

    /// <summary>The host's last value for one of the port's output fields,
    /// named in <see cref="HMPortSet.OutputNames"/>. Zero until a host sets
    /// it, and again after the device returns to its initial state at a port
    /// reset or detach.</summary>
    public int GetOutput(string name) => _set.GetOutput(Number - 1, name);

    /// <summary>Raised on the controller's output thread when a host changes
    /// one of this port's output fields, and when the device's return to its
    /// initial state sets one back to zero. Handlers must be thread-safe. An
    /// exception a handler throws is caught and dropped, as
    /// <see cref="HMController.OutputReceived"/> drops them.</summary>
    public event EventHandler<HMPortOutputEventArgs>? OutputChanged;

    internal void RaiseOutputChanged(string name, int value)
    {
        var handler = OutputChanged;
        if (handler == null) return;
        EventRaise.Each(handler, this, new HMPortOutputEventArgs(this, name, value), nameof(OutputChanged));
    }
}

/// <summary>The device-wide flags of an <see cref="HMPortSet"/>, by name.</summary>
public sealed class HMPortFlags
{
    private readonly HMPortSet _set;
    private readonly PortLayout? _layout;

    internal HMPortFlags(HMPortSet set, PortLayout? layout)
    {
        _set = set;
        _layout = layout;
    }

    /// <summary>The flags the profile declares.</summary>
    public IReadOnlyList<string> Names => _layout?.Flags ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>Whether a flag is on. Setting one rewrites every port's
    /// status byte and sends the report.</summary>
    /// <exception cref="ArgumentException">The device declares no flag of
    /// that name.</exception>
    public bool this[string name]
    {
        get => _set.GetFlag(_set.FlagIndex(name));
        set => _set.SetFlag(_set.FlagIndex(name), value);
    }
}

/// <summary>A change to one of a port's output fields.</summary>
public sealed class HMPortOutputEventArgs : EventArgs
{
    internal HMPortOutputEventArgs(HMPort port, string name, int value)
    {
        Port = port;
        Name = name;
        Value = value;
    }

    /// <summary>The port.</summary>
    public HMPort Port { get; }

    /// <summary>The output field, one of <see cref="HMPortSet.OutputNames"/>.</summary>
    public string Name { get; }

    /// <summary>The value the host wrote, as the field's bytes hold it.</summary>
    public int Value { get; }
}

/// <summary>The protocol of a USB/IP device whose profile declares a
/// <c>protocol</c>: the registers and counters the host's commands and the
/// device's reports set, and the host poll the device is served at. Get it
/// from <see cref="HMController.DeviceProtocol"/>.</summary>
public sealed class HMDeviceProtocol
{
    private readonly ProtocolEngine _engine;

    internal HMDeviceProtocol(ProtocolEngine engine)
    {
        _engine = engine;
    }

    /// <summary>The device's named states, as the profile declares them.</summary>
    public IReadOnlyList<string> StateNames => _engine.Protocol.StateNames;

    /// <summary>Whether a named state is on now.</summary>
    /// <exception cref="ArgumentException">The device declares no state of
    /// that name.</exception>
    public bool GetState(string name)
    {
        int i = Array.IndexOf(_engine.Protocol.StateNames, name);
        if (i < 0)
            throw new ArgumentException(
                $"'{name}' is not a state of this device: {string.Join(", ", _engine.Protocol.StateNames)}.", nameof(name));
        return _engine.GetState(i);
    }

    /// <summary>The host poll period the device's input endpoint
    /// descriptor gives, as Windows schedules it.</summary>
    public TimeSpan DescriptorPollInterval => TimeSpan.FromTicks(_engine.Protocol.DescriptorPollMicros * 10L);

    /// <summary>The host poll period the device is served at: the
    /// descriptor's, the default, or a faster one for a host that polls the
    /// device more often than its descriptor asks, as a host overclocked
    /// with a filter driver does. The descriptors never change. Windows polls
    /// a low-speed interrupt endpoint every 8, 16 or 32 ms, a full-speed one
    /// every 1, 2, 4, 8, 16 or 32 ms and a high-speed one every 125, 250 or
    /// 500 microseconds or 1, 2 or 4 ms (USB_ENDPOINT_DESCRIPTOR, Microsoft
    /// Learn), and the period is one of those.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A period Windows does
    /// not schedule at the device's speed.</exception>
    public TimeSpan HostPollInterval
    {
        get => TimeSpan.FromTicks(_engine.HostPollMicros * 10L);
        set
        {
            long micros = value.Ticks / 10;
            bool exact = value.Ticks % 10 == 0;
            uint speed = _engine.Protocol.Speed;
            bool ok = exact && (speed >= 3
                ? IsPowerOfTwoTimes(micros, 125, 1, 32)
                : speed == 1
                    ? IsPowerOfTwoTimes(micros, 1000, 8, 32)
                    : IsPowerOfTwoTimes(micros, 1000, 1, 32));
            if (!ok)
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    speed >= 3
                        ? "Windows polls a high-speed device every 125, 250 or 500 microseconds or 1, 2 or 4 ms."
                        : speed == 1
                            ? "Windows polls a low-speed device every 8, 16 or 32 ms."
                            : "Windows polls a full-speed device every 1, 2, 4, 8, 16 or 32 ms.");
            _engine.HostPollMicros = (int)micros;
        }
    }

    private static bool IsPowerOfTwoTimes(long value, long unit, long minMultiple, long maxMultiple)
    {
        if (value <= 0 || value % unit != 0) return false;
        long m = value / unit;
        return m >= minMultiple && m <= maxMultiple && (m & (m - 1)) == 0;
    }
}

/// <summary>What <see cref="HMController.BindWinUsb"/> did.</summary>
public enum HMWinUsbBinding
{
    /// <summary>WinUSB is now bound to the virtual device.</summary>
    Bound,
    /// <summary>The device already carried WinUSB, libusbK or libusb0, a
    /// driver libusb opens, for example from Zadig. Nothing was changed.</summary>
    AlreadyBound,
    /// <summary>The device carries a driver from outside Windows, which
    /// this step leaves alone. Nothing was changed.</summary>
    OtherDriver,
}
