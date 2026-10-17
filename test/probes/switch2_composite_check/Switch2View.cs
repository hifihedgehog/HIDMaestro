using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>The composite's protocol state, read from the engine its
/// profile's protocol compiles to (v2.0.0), under the names the probe read
/// from Switch2ProDevice at 9bd6279: the registers the profile declares, its
/// reportsSent counter and its command log.</summary>
internal sealed class Switch2View
{
    readonly ProtocolEngine _e;
    readonly int _on, _selected, _mask, _enabled, _leds, _sent;

    Switch2View(ProtocolEngine e)
    {
        _e = e;
        var regs = e.Protocol.Registers;
        _on = regs.IndexOf("reportsOn");
        _selected = regs.IndexOf("selectedReport");
        _mask = regs.IndexOf("featureMask");
        _enabled = regs.IndexOf("featureEnabled");
        _leds = regs.IndexOf("playerLeds");
        _sent = Array.IndexOf(e.Protocol.Model.CounterNames, "reportsSent");
    }

    /// <summary>True for a device whose protocol builds its reports.</summary>
    public static bool Builds(UsbipEmulatedDevice d) => d.Protocol is { Protocol.BuildsReports: true };

    public static Switch2View Of(UsbipEmulatedDevice d) =>
        new(d.Protocol ?? throw new InvalidOperationException($"{d.BusId} runs no protocol."));

    public bool ReportsOn => _e.GetState(_on);
    public byte SelectedReport => (byte)_e.GetRegister(_selected);
    public byte FeatureMask => (byte)_e.GetRegister(_mask);
    public byte FeatureEnabled => (byte)_e.GetRegister(_enabled);
    public byte PlayerLeds => (byte)_e.GetRegister(_leds);
    public uint ReportsSent => _e.GetCounter(_sent);

    public List<byte[]> SnapshotBulkLog() => _e.SnapshotLog().Select(x => x.Command).ToList();
    public List<(long Ticks, byte[] Payload)> SnapshotBulkLogTimed() => _e.SnapshotLog();

    static readonly ControllerProfile s_profile =
        ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch2-pro-controller-composite");
    static readonly DecoderRunner s_rumble = ProgramBinder.BindDecoders(s_profile.GetOrCompileModel())[0];
    static readonly ExtendedReportSpec s_body = s_profile.StateBlockSpecForCodec();
    static readonly VendorBlobCodec.EncoderState s_enc = new();

    /// <summary>The profile's rumble decoder on output report 0x02, ID first.
    /// False for a report shorter than the decoder reads, which is not
    /// decoded.</summary>
    public static unsafe bool DecodeRumble(ReadOnlySpan<byte> report, out byte left, out byte right)
    {
        left = right = 0;
        if (report.Length < s_rumble.Form.MinLength) return false;
        var input = new byte[report.Length + 4];
        report.CopyTo(input);
        var output = new byte[2];
        fixed (byte* sp = input)
        fixed (byte* dp = output)
            s_rumble.Run(sp, dp);
        left = output[0];
        right = output[1];
        return true;
    }

    public static int BodySize => s_body.Size;

    /// <summary>SubmitState's state block for the composite, as HMController
    /// encodes it.</summary>
    public static void BuildBody(in HMGamepadState st, double lx, double ly, double rx, double ry, double lt, double rt, byte[] dst) =>
        VendorBlobCodec.EncodeInput(s_body, in st, (float)lx, (float)ly, (float)rx, (float)ry, (float)lt, (float)rt, dst, s_enc);
}
