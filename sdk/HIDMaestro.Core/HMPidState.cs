using System;

namespace HIDMaestro;

/// <summary>
/// Block Load Status values per HID PID 1.0 §5.5. Returned to the host in the
/// Block Load Report's <c>LoadStatus</c> field after a Create New Effect
/// request. Consumers pass this to
/// <see cref="HMController.PublishPidBlockLoad(byte, PidLoadStatus, ushort)"/>.
/// </summary>
public enum PidLoadStatus : byte
{
    /// <summary>Effect block was successfully allocated.</summary>
    Success = 1,

    /// <summary>Effect block could not be allocated because the device's
    /// effect pool is full. The host should fail or retry the create.</summary>
    Full = 2,

    /// <summary>Effect block allocation failed for some other reason.</summary>
    Error = 3,
}

/// <summary>
/// PID State Report flags per HID PID 1.0 §5.8. Bitfield reflecting the
/// device's current force-feedback state for the most-recently-referenced
/// effect block. Consumers pass this to
/// <see cref="HMController.PublishPidState(byte, PidStateFlags)"/>.
/// </summary>
[Flags]
public enum PidStateFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>Device is paused — effects are suspended but not freed.</summary>
    DeviceIsPaused = 1 << 0,

    /// <summary>Actuators are enabled; the device can render force.</summary>
    ActuatorsEnabled = 1 << 1,

    /// <summary>Safety switch is engaged — the device must not render force.</summary>
    SafetySwitch = 1 << 2,

    /// <summary>Actuator override switch is engaged.</summary>
    ActuatorOverrideSwitch = 1 << 3,

    /// <summary>Actuator power is on.</summary>
    ActuatorPower = 1 << 4,

    /// <summary>The most-recently-referenced effect is currently playing.</summary>
    EffectPlaying = 1 << 5,
}

/// <summary>v1.1.37 — snapshot of the PID Block Load Report fields the
/// driver populates synchronously inside its SetFeature(0x11 Create New
/// Effect) IOCTL handler. Returned by
/// <see cref="HMController.GetCurrentPidBlockLoad"/>. The consumer reads
/// this from its <c>OutputReceived</c> handler for Report ID 0x11 to
/// learn which EBI the driver just assigned — the driver does the picking
/// now (mirroring vJoy's <c>Ffb_GetNextFreeEffect</c>); the consumer's
/// role is to observe and wire the EBI to its own effect tracking.</summary>
public readonly struct HMPidBlockLoad
{
    /// <summary>Effect Block Index (1..N) the driver allocated. 0 means
    /// no effect has been created yet (or the pool was empty when the
    /// last Create New Effect arrived; check <see cref="LoadStatus"/>).</summary>
    public byte EffectBlockIndex { get; }

    /// <summary>Result per HID PID 1.0 §5.5: Success/Full/Error.</summary>
    public PidLoadStatus LoadStatus { get; }

    /// <summary>RAM pool bytes still free after the driver's last
    /// allocation.</summary>
    public ushort RAMPoolAvailable { get; }

    internal HMPidBlockLoad(byte ebi, byte status, ushort ram)
    {
        EffectBlockIndex = ebi;
        LoadStatus = (PidLoadStatus)status;
        RAMPoolAvailable = ram;
    }
}
