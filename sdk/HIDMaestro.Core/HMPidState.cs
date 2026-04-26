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
