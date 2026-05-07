using System;

namespace HIDMaestro;

/// <summary>
/// Abstract gamepad state pushed to a virtual controller. The SDK translates
/// this into the profile's native HID report format using the descriptor —
/// you don't need to know whether the target is a DualSense, Xbox 360, or
/// arcade stick. Sticks and triggers use normalized floats; buttons are a
/// flags enum; hat uses cardinal directions.
///
/// v1.3.5 also carries the wide-surface fields modern Sony pads emit
/// (touchpad, gyro, accel, battery, mic/headphone state). Profiles that
/// declare matching <c>extendedReport.fields</c> entries pass the values
/// through to the consumer; profiles that don't ignore them at zero cost.
/// For wholly custom payloads use <see cref="HMController.SubmitRawReport"/>.
/// </summary>
public struct HMGamepadState
{
    /// <summary>Left stick X axis. Range: -1.0 (left) .. +1.0 (right). 0 = centered.</summary>
    public float LeftStickX;

    /// <summary>Left stick Y axis. Range: -1.0 (down) .. +1.0 (up). 0 = centered.</summary>
    public float LeftStickY;

    /// <summary>Right stick X axis. Range: -1.0 (left) .. +1.0 (right). 0 = centered.</summary>
    public float RightStickX;

    /// <summary>Right stick Y axis. Range: -1.0 (down) .. +1.0 (up). 0 = centered.</summary>
    public float RightStickY;

    /// <summary>Left trigger. Range: 0.0 (released) .. 1.0 (fully pressed).</summary>
    public float LeftTrigger;

    /// <summary>Right trigger. Range: 0.0 (released) .. 1.0 (fully pressed).</summary>
    public float RightTrigger;

    /// <summary>Pressed buttons as a bitmask.</summary>
    public HMButton Buttons;

    /// <summary>Octant direction (8 cardinal+diagonal positions). Use this for
    /// XInput-style or 8-way gamepad sources. For higher-resolution hat targets
    /// (flight sticks, HOTAS), see <see cref="HatDegrees"/>, <see cref="HatHundredths"/>,
    /// or <see cref="HatRaw"/>.</summary>
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
