using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

namespace HIDMaestro;

/// <summary>The audio surfaces of a composite USB controller (issue #39).
/// Non-null on <see cref="HMController.UsbAudio"/> only when the profile
/// runs on the USB/IP backend; every UMDF2 controller leaves it null.
///
/// <para>Two directions, shaped like the existing state surfaces:
/// <see cref="Output"/> is host-to-consumer (the game renders to the
/// controller's audio endpoint; the SDK delivers the PCM windows,
/// channel-role map attached, so channels 3/4's voice-coil haptic lanes
/// are addressable without decoding USB Audio Class topology), and
/// <see cref="Microphone"/> is consumer-to-host (feed PCM; the backend
/// streams it to whatever is recording, silence when the buffer runs
/// dry).</para>
///
/// <para>All events fire on the backend's pacing thread. Handlers must
/// be quick and thread-safe, same contract as
/// <see cref="HMController.OutputReceived"/>.</para></summary>
public sealed class HMUsbAudio
{
    /// <summary>The render direction: speaker plus haptic channels.</summary>
    public HMAudioOutput Output { get; }

    /// <summary>The capture direction: the emulated microphone.</summary>
    public HMMicrophoneInput Microphone { get; }

    /// <summary>Raised when the host writes a UAC control (volume or mute
    /// on either feature unit). Windows drives these from the volume
    /// mixer and the recording control panel.</summary>
    public event EventHandler<HMAudioControlChangedEventArgs>? ControlChanged;

    private readonly Dictionary<byte, string> _unitFunctions = new();

    internal HMUsbAudio(ControllerProfile profile, UsbipEmulatedDevice device)
    {
        var cfg = profile.UsbConfiguration!;

        UsbAudioStreamSpec? outStream = null, inStream = null;
        byte outIface = 0xFF, inIface = 0xFF;
        foreach (var iface in cfg.Interfaces)
        {
            foreach (var alt in iface.AltSettings)
            {
                if (alt.AudioStream == null) continue;
                if (iface.Function == "audioStreamingOut") { outStream = alt.AudioStream; outIface = iface.InterfaceNumber; }
                else if (iface.Function == "audioStreamingIn") { inStream = alt.AudioStream; inIface = iface.InterfaceNumber; }
            }
        }

        Output = new HMAudioOutput(outStream, outIface);
        Microphone = new HMMicrophoneInput(inStream, inIface, device.Audio);

        foreach (var ctl in cfg.AudioControls ?? new List<UsbAudioControlSpec>())
            _unitFunctions[ctl.UnitId] = ctl.Function;

        device.Audio.OutFrames += pcm => Output.RaiseFrames(pcm);
        device.Audio.AltSettingChanged += (iface, alt) =>
        {
            if (iface == outIface) Output.SetStreaming(alt != 0);
            else if (iface == inIface) Microphone.SetStreaming(alt != 0);
        };
        device.Audio.ControlChanged += (unit, selector, channel, raw) =>
        {
            var handler = ControlChanged;
            if (handler == null) return;
            handler(this, new HMAudioControlChangedEventArgs
            {
                Function = _unitFunctions.TryGetValue(unit, out var fn) ? fn : $"unit{unit}",
                IsMute = selector == 1,
                MuteValue = selector == 1 && raw != 0,
                RawValue = raw,
                VolumeDb = selector == 2 ? raw / 256.0 : 0.0,
            });
        };
    }
}

/// <summary>The host-to-consumer audio stream of one composite
/// controller: interleaved PCM windows as the host renders them, at the
/// endpoint's real cadence.</summary>
public sealed class HMAudioOutput
{
    /// <summary>Interleaved channel count (4 on a DualSense: two speaker,
    /// two voice-coil haptic).</summary>
    public int Channels { get; }
    public int SampleRateHz { get; }
    public int BitsPerSample { get; }

    /// <summary>What each interleaved channel carries, in order:
    /// <c>speakerLeft</c>, <c>speakerRight</c>, <c>hapticLeft</c>,
    /// <c>hapticRight</c>. This is the routing key for haptics
    /// passthrough.</summary>
    public IReadOnlyList<string> ChannelRoles { get; }

    /// <summary>True while the host has the stream's alternate setting
    /// selected (audio session open).</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>Interleaved 16-bit little-endian PCM for one paced
    /// window (typically a few milliseconds). The memory is only valid
    /// for the duration of the callback; copy to retain.</summary>
    public event Action<HMAudioOutput, ReadOnlyMemory<byte>>? FramesReceived;

    /// <summary>Raised when the host opens or parks the stream.</summary>
    public event Action<HMAudioOutput, bool>? StreamingChanged;

    internal HMAudioOutput(UsbAudioStreamSpec? stream, byte interfaceNumber)
    {
        Channels = stream?.Channels ?? 0;
        SampleRateHz = stream?.SampleRateHz ?? 0;
        BitsPerSample = stream?.BitsPerSample ?? 0;
        ChannelRoles = stream?.ChannelRoles?.ToArray() ?? Array.Empty<string>();
        _ = interfaceNumber;
    }

    internal void RaiseFrames(ReadOnlyMemory<byte> pcm) => FramesReceived?.Invoke(this, pcm);

    internal void SetStreaming(bool streaming)
    {
        if (IsStreaming == streaming) return;
        IsStreaming = streaming;
        StreamingChanged?.Invoke(this, streaming);
    }
}

/// <summary>The consumer-to-host microphone of one composite controller.
/// Feed interleaved PCM; the backend paces it onto the isochronous IN
/// endpoint and fills silence when the buffer runs dry, which is what a
/// muted real microphone sounds like.</summary>
public sealed class HMMicrophoneInput
{
    public int Channels { get; }
    public int SampleRateHz { get; }
    public int BitsPerSample { get; }

    /// <summary>True while the host has the capture stream open.</summary>
    public bool IsStreaming { get; private set; }

    public event Action<HMMicrophoneInput, bool>? StreamingChanged;

    private readonly UsbAudioEngine _engine;

    internal HMMicrophoneInput(UsbAudioStreamSpec? stream, byte interfaceNumber, UsbAudioEngine engine)
    {
        Channels = stream?.Channels ?? 0;
        SampleRateHz = stream?.SampleRateHz ?? 0;
        BitsPerSample = stream?.BitsPerSample ?? 0;
        _engine = engine;
        _ = interfaceNumber;
    }

    /// <summary>Append interleaved 16-bit little-endian PCM at the
    /// stream's declared format. Returns the bytes accepted; the buffer
    /// holds roughly a quarter second and drops the excess so producer
    /// overrun cannot grow capture latency without bound.</summary>
    public int Submit(ReadOnlySpan<byte> interleavedPcm) => _engine.SubmitMicSamples(interleavedPcm);

    /// <summary>Bytes buffered and not yet streamed to the host.</summary>
    public int BufferedBytes => _engine.MicBufferedBytes;

    internal void SetStreaming(bool streaming)
    {
        if (IsStreaming == streaming) return;
        IsStreaming = streaming;
        StreamingChanged?.Invoke(this, streaming);
    }
}

/// <summary>One UAC control write from the host (volume slider, mute).</summary>
public sealed class HMAudioControlChangedEventArgs : EventArgs
{
    /// <summary><c>"speaker"</c> or <c>"microphone"</c>, per the
    /// profile's audioControls declaration.</summary>
    public string Function { get; init; } = "";

    /// <summary>True for a Mute write, false for Volume.</summary>
    public bool IsMute { get; init; }

    /// <summary>The mute state, when <see cref="IsMute"/>.</summary>
    public bool MuteValue { get; init; }

    /// <summary>Volume in dB (UAC1 s16 raw / 256), when a volume write.</summary>
    public double VolumeDb { get; init; }

    /// <summary>The raw UAC1 wire value.</summary>
    public short RawValue { get; init; }
}
