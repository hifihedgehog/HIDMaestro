using System;
using System.Collections.Generic;

namespace HIDMaestro;

/// <summary>
/// Abstract gamepad state pushed to a virtual controller. The SDK translates
/// this into the profile's native HID report format using the descriptor —
/// the consumer doesn't need to know whether the target is a DualSense, an
/// Xbox 360, an arcade stick, a wheel, or a flight stick.
///
/// <para>v1.3.9 — single unified <see cref="Axes"/> dictionary keyed by
/// <see cref="HMAxis"/> drives every analog input the descriptor declares.
/// Discovery: <see cref="HMProfile.Sticks"/> and
/// <see cref="HMProfile.Triggers"/> enumerate which axes the profile
/// exposes; the consumer writes <c>state.Axes[stick.XAxis] = value</c>.
/// No special-cased stick/trigger slots, no two-range convention. Buttons
/// stay as the <see cref="HMButton"/> bitmask (mapped via the profile's
/// ButtonMap). Sensor surface (touchpad, IMU, battery) keeps its native
/// representation since those aren't analog axes in the HMAxis sense.</para>
/// </summary>
public struct HMGamepadState
{
    /// <summary>Drive any descriptor-declared analog axis by HID usage.
    /// Each value normalizes to <c>[0.0 .. 1.0]</c>; the SDK scales into
    /// the descriptor field's <c>LogicalMin..LogicalMax</c>. For signed
    /// centered axes (sticks, twist rudders), <c>0.5</c> = center.
    /// For unsigned axes (triggers, throttles, sliders, pedals),
    /// <c>0.0</c> = released. Axes the consumer doesn't write default
    /// to centered (signed) or released (unsigned) automatically.
    ///
    /// <para>Discovery: <see cref="HMProfile.Sticks"/> /
    /// <see cref="HMProfile.Triggers"/> list which axes this profile
    /// exposes and what role each serves. Consumers iterate those lists
    /// and write <c>state.Axes[entry.Axis] = value</c>.</para>
    ///
    /// <para>Null on the hot path is free — the encoder skips the dict
    /// walk entirely when no axis has been written. Allocate once and
    /// reuse.</para></summary>
    public Dictionary<HMAxis, float>? Axes;

    /// <summary>Pressed buttons as a bitmask. Mapped to descriptor button
    /// indices via the profile's <see cref="HMProfile.ButtonMap"/> (Sony
    /// profiles remap so <c>HMButton.A</c> → Cross, etc.).</summary>
    public HMButton Buttons;

    /// <summary>Octant direction (8 cardinal+diagonal positions). Use this
    /// for XInput-style or 8-way gamepad sources. For higher-resolution
    /// hat targets (flight sticks, HOTAS), see <see cref="HatDegrees"/>,
    /// <see cref="HatHundredths"/>, or <see cref="HatRaw"/>.</summary>
    public HMHat Hat;

    /// <summary>Continuous angle in degrees, 0 = North, clockwise. The encoder
    /// normalizes to [0, 360) and snaps to the nearest descriptor position.
    /// Use when the source produces an angle. Takes priority over
    /// <see cref="HatHundredths"/>, <see cref="HatRaw"/>, and <see cref="Hat"/>
    /// when set.</summary>
    public float? HatDegrees;

    /// <summary>Angle in hundredths of a degree, 0..35999. Same effect as
    /// <see cref="HatDegrees"/> but integer-only — use for vJoy migration paths
    /// or to keep float off the hot path. Used when <see cref="HatDegrees"/>
    /// is null; takes priority over <see cref="HatRaw"/> and <see cref="Hat"/>.</summary>
    public int? HatHundredths;

    /// <summary>Raw value written directly into the descriptor's hat field,
    /// clamped to the descriptor's LogicalMin..LogicalMax. Use only when you
    /// have queried <see cref="HMProfile.HatLogicalMin"/> /
    /// <see cref="HMProfile.HatLogicalMax"/> and want exact bits. Used when
    /// both angle fields are null; takes priority over <see cref="Hat"/>.</summary>
    public ushort? HatRaw;

    // ── Touchpad (Sony two-finger packet) ─────────────────────────────────

    /// <summary>True when finger 0 is touching the touchpad.</summary>
    public bool TouchpadFinger0Active;

    /// <summary>Finger 0 X coordinate, 0..1919 (DualSense / DS4 native range).</summary>
    public ushort TouchpadFinger0X;

    /// <summary>Finger 0 Y coordinate, 0..1079 (DualSense / DS4 native range).</summary>
    public ushort TouchpadFinger0Y;

    /// <summary>Finger 0 tracking ID (7 bits, 0..127). Increments per new
    /// touch. Bit 7 (0x80) is the firmware "lifted" flag — encoder OR's
    /// it with the active flag automatically when <see cref="TouchpadFinger0Active"/>
    /// is false.</summary>
    public byte TouchpadFinger0Id;

    /// <summary>True when finger 1 is touching the touchpad.</summary>
    public bool TouchpadFinger1Active;

    /// <summary>Finger 1 X coordinate, 0..1919.</summary>
    public ushort TouchpadFinger1X;

    /// <summary>Finger 1 Y coordinate, 0..1079.</summary>
    public ushort TouchpadFinger1Y;

    /// <summary>Finger 1 tracking ID (7 bits).</summary>
    public byte TouchpadFinger1Id;

    /// <summary>Monotonic touchpad packet counter increments per touch event.
    /// Maps to the <c>touchpadPacketCounter</c> semantic.</summary>
    public byte TouchpadPacketCounter;

    // ── IMU (raw firmware units) ──────────────────────────────────────────

    /// <summary>Gyro pitch, signed 16-bit. Raw firmware units (no calibration).</summary>
    public short GyroPitch;

    /// <summary>Gyro yaw, signed 16-bit.</summary>
    public short GyroYaw;

    /// <summary>Gyro roll, signed 16-bit.</summary>
    public short GyroRoll;

    /// <summary>Accelerometer X, signed 16-bit.</summary>
    public short AccelX;

    /// <summary>Accelerometer Y, signed 16-bit.</summary>
    public short AccelY;

    /// <summary>Accelerometer Z, signed 16-bit.</summary>
    public short AccelZ;

    /// <summary>Sensor packet timestamp in firmware microseconds (32-bit,
    /// rolls over). Maps to the <c>sensorTimestamp</c> semantic.</summary>
    public uint SensorTimestamp;

    // ── Battery + housekeeping ────────────────────────────────────────────

    /// <summary>Battery capacity, 0..10 (Sony firmware convention). Profiles
    /// that emit a 0..100 percentage scale via the <c>uint8</c> field type
    /// should pre-scale before populating; the codec writes the byte verbatim.</summary>
    public byte BatteryLevel;

    /// <summary>Battery is currently charging.</summary>
    public bool BatteryCharging;

    /// <summary>Battery is at full charge (some pads emit a separate "full" bit
    /// in addition to the charging bit).</summary>
    public bool BatteryFull;

    /// <summary>Microphone is muted at the firmware level (DS5 only).</summary>
    public bool MicMuted;

    /// <summary>Headphones detected on the 3.5 mm jack.</summary>
    public bool HeadphonesConnected;
}

/// <summary>Static helpers for populating <see cref="HMGamepadState.Axes"/>
/// from the canonical "left stick X/Y, right stick X/Y, LT, RT" 6-slot
/// convention. Resolves the actual HID axes from
/// <see cref="HMProfile.Sticks"/> / <see cref="HMProfile.Triggers"/> so the
/// caller doesn't have to know whether the active profile's left stick is
/// X+Y or Rx+Ry. All values are <c>[0..1]</c> (0.5 = center on signed axes;
/// 0.0 = released on triggers).</summary>
public static class HMGamepadStateHelpers
{
    /// <summary>Produce a fresh axes dict from the 6-slot convention. For
    /// hot paths, allocate the dict once and update entries by axis key
    /// directly via <see cref="HMProfile.Sticks"/>/<see cref="HMProfile.Triggers"/>
    /// lookups; this helper exists for tests, demos, and one-shot frames
    /// where ergonomics matter more than allocation.</summary>
    public static Dictionary<HMAxis, float> StandardAxes(
        HMProfile profile,
        float leftStickX = 0.5f, float leftStickY = 0.5f,
        float rightStickX = 0.5f, float rightStickY = 0.5f,
        float leftTrigger = 0.0f, float rightTrigger = 0.0f)
    {
        var axes = new Dictionary<HMAxis, float>();
        var sticks = profile.Sticks;
        var triggers = profile.Triggers;
        if (sticks.Count > 0)
        {
            axes[sticks[0].XAxis] = leftStickX;
            if (sticks[0].YAxis != HMAxis.None) axes[sticks[0].YAxis] = leftStickY;
        }
        if (sticks.Count > 1)
        {
            axes[sticks[1].XAxis] = rightStickX;
            if (sticks[1].YAxis != HMAxis.None) axes[sticks[1].YAxis] = rightStickY;
        }
        if (triggers.Count > 0) axes[triggers[0].Axis] = leftTrigger;
        if (triggers.Count > 1) axes[triggers[1].Axis] = rightTrigger;
        return axes;
    }
}

/// <summary>HID-usage-addressable analog axes. Values encode the (UsagePage,
/// Usage) pair as <c>(page &lt;&lt; 8) | usage</c> so consumers can switch
/// directly on a descriptor's declared usages.
///
/// <para>Generic Desktop (page 0x01): X, Y, Z, Rx, Ry, Rz are standard stick
/// and trigger usages. Slider, Dial, Wheel cover throttle quadrants and
/// steering. Vx-Vno are the velocity/relative usages (HM uses Vx/Vy as
/// the hidden separate-trigger pair the WGI Xbox path needs).</para>
///
/// <para>Simulation Controls (page 0x02): the analog flight/automotive/
/// marine/cycling control set. Anything declared as an Input axis can be
/// driven through <see cref="HMGamepadState.Axes"/>.</para></summary>
public enum HMAxis : ushort
{
    None = 0,

    // Generic Desktop (page 0x01)
    X       = 0x0130,
    Y       = 0x0131,
    Z       = 0x0132,
    Rx      = 0x0133,
    Ry      = 0x0134,
    Rz      = 0x0135,
    Slider  = 0x0136,
    Dial    = 0x0137,
    Wheel   = 0x0138,
    Vx      = 0x0140,
    Vy      = 0x0141,
    Vz      = 0x0142,
    Vbrx    = 0x0143,
    Vbry    = 0x0144,
    Vbrz    = 0x0145,
    Vno     = 0x0146,

    // Simulation Controls (page 0x02)
    Aileron          = 0x02B0,
    AileronTrim      = 0x02B1,
    AntiTorque       = 0x02B2,
    CollectiveControl= 0x02B5,
    DiveBrake        = 0x02B6,
    Elevator         = 0x02B8,
    ElevatorTrim     = 0x02B9,
    Rudder           = 0x02BA,
    Throttle         = 0x02BB,
    LandingGear      = 0x02BE,
    ToeBrake         = 0x02BF,
    WingFlaps        = 0x02C3,
    Accelerator      = 0x02C4,
    Brake            = 0x02C5,
    Clutch           = 0x02C6,
    Shifter          = 0x02C7,
    Steering         = 0x02C8,
    TurretDirection  = 0x02C9,
    BarrelElevation  = 0x02CA,
    DivePlane        = 0x02CB,
    Ballast          = 0x02CC,
    BicycleCrank     = 0x02CD,
    HandleBars       = 0x02CE,
    FrontBrake       = 0x02CF,
    RearBrake        = 0x02D0,
}

/// <summary>Standard gamepad button bitmask. Profile-specific renames (Cross/A, Circle/B,
/// Square/X, Triangle/Y) are handled by the SDK based on the active profile.</summary>
[Flags]
public enum HMButton : uint
{
    None         = 0,
    A            = 1u << 0,
    B            = 1u << 1,
    X            = 1u << 2,
    Y            = 1u << 3,
    LeftBumper   = 1u << 4,
    RightBumper  = 1u << 5,
    Back         = 1u << 6,   // Select / Share / View
    Start        = 1u << 7,   // Options / Menu
    LeftStick    = 1u << 8,   // L3
    RightStick   = 1u << 9,   // R3
    Guide        = 1u << 10,  // Xbox / PS / Home
    Touchpad     = 1u << 11,  // PS touchpad click (DualShock 4 / DualSense)
    Share        = 1u << 12,  // Xbox Series Share button (not present on earlier Xbox or Sony)

    // Aliases for clarity when programming against PlayStation profiles
    Cross    = A,
    Circle   = B,
    Square   = X,
    Triangle = Y,
}

/// <summary>D-pad / hat-switch direction. The SDK encodes this into whatever the profile's
/// descriptor declares (4-bit hat, individual buttons, etc.).</summary>
public enum HMHat : byte
{
    None      = 0,
    North     = 1,
    NorthEast = 2,
    East      = 3,
    SouthEast = 4,
    South     = 5,
    SouthWest = 6,
    West      = 7,
    NorthWest = 8,
}
