using System;
using System.Collections.Generic;

namespace HIDMaestro;

// =====================================================================
// HMLayout — structured per-profile physical-design declaration (v1.3.9)
//
// Discriminated by HMLayoutKind. Each per-kind layout captures the
// device's published manufacturer-described shape: which descriptor
// fields are sticks vs wheels vs pedals vs throttles, how sub-modules
// (a HOTAS throttle quadrant, a wheel's pedal cluster, a HOTAS rudder
// module) cluster, what semantic role each axis and button serves.
//
// Two surfaces:
//   • HMProfile.Layout — the full structured layout (rich, when authored)
//   • HMProfile.StickCount / TriggerCount / Sticks / Triggers — simple
//     derived views computed FROM Layout when authored, from the
//     classifier heuristic when not (backward-compatible fallback)
//
// Profiles without a layout block keep the v1.3.8 classifier-derived
// behavior unchanged.
// =====================================================================

/// <summary>Discriminator for <see cref="HMLayout"/>. Each value selects
/// a per-kind concrete record with the fields that device class
/// requires.</summary>
public enum HMLayoutKind
{
    /// <summary>No layout authored. Consumers fall back to classifier-
    /// derived StickCount/TriggerCount/AvailableAxes.</summary>
    Unspecified = 0,
    Gamepad,
    Joystick,
    FlightStick,
    Hotas,
    Wheel,
    Pedals,
    Shifter,
    Handbrake,
    SingleAxisAccessory,
    ArcadeStick,
    DancePad,
    Guitar,
    MotionWand,
    Remote,
    ControllerAdapter,
}

/// <summary>Role an analog axis serves on the physical device. Used by
/// every layout kind to label the descriptor's input fields with their
/// real-world meaning so consumers can render the right widget.</summary>
public enum HMAxisRole
{
    Unknown = 0,
    LeftStickX,    LeftStickY,
    RightStickX,   RightStickY,
    LeftTrigger,   RightTrigger,
    Wheel,                    // steering wheel rotation
    Throttle,                 // flight throttle / racing throttle pedal
    Brake,                    // racing brake pedal / flight toe brake
    Clutch,                   // racing clutch pedal
    Accelerator,              // racing accelerator pedal (alias for Throttle on some)
    Rudder,                   // separate rudder pedal axis
    Aileron,                  // flight aileron
    Elevator,                 // flight elevator
    TwistRudder,              // stick-grip rudder via twist
    ThrottleSlider,           // slider on a flight stick base used as throttle
    Dial,                     // generic rotary dial
    ScrollWheel,              // throttle module scroll wheel (X52)
    ModeDial,                 // multi-position rotary mode selector
    Friction,                 // throttle friction adjuster (HOTAS)
    MiniStickX, MiniStickY,   // throttle module mini-stick (X52)
    HandbrakeAxis,            // single-axis handbrake lever
}

/// <summary>Role a button serves on the physical device.</summary>
public enum HMButtonRole
{
    Unknown = 0,

    // Standard gamepad face
    FaceA, FaceB, FaceX, FaceY,
    FaceCross, FaceCircle, FaceSquare, FaceTriangle,

    // Shoulders + stick clicks
    LeftBumper, RightBumper,
    LeftTriggerClick, RightTriggerClick,
    LeftStickClick, RightStickClick,

    // System
    Back, View, Share, Start, Options, Menu, Guide, Home, Capture, Ps, Xbox,
    Mute, ProfileSwitch,

    // Elite paddles + extra remappable
    PaddleP1, PaddleP2, PaddleP3, PaddleP4,
    PaddleM1, PaddleM2, PaddleM3, PaddleM4, PaddleM5, PaddleM6,

    // Wheel paddle shifters
    PaddleShifterUp, PaddleShifterDown,

    // Flight stick / HOTAS
    Trigger, Pinkie, Thumb,
    ModeSwitchLow, ModeSwitchMid, ModeSwitchHigh,

    // H-pattern shifter gears
    Gear1, Gear2, Gear3, Gear4, Gear5, Gear6, Gear7, GearReverse,

    // Sequential shifter
    ShifterUp, ShifterDown,

    // Guitar
    StrumUp, StrumDown,
    FretGreen, FretRed, FretYellow, FretBlue, FretOrange,
    SoloGreen, SoloRed, SoloYellow, SoloBlue, SoloOrange,
    Whammy, // (axis, here for completeness if some descriptor declares as button)

    // PS Move
    Move, T,

    // D-pad as buttons (when descriptor encodes dpad as 4 separate buttons)
    DpadUp, DpadDown, DpadLeft, DpadRight,
}

public enum HMPedalType        { Unknown, Potentiometer, HallEffect, LoadCell, Magnetic }
public enum HMRudderKind       { Twist, Rocker, Pedals }
public enum HMShifterKind      { HPattern, Sequential, PaddleLeft, PaddleRight }
public enum HMShifterActuation { Digital, AnalogSqueeze }
public enum HMHatLocation      { Unknown, StickTop, StickThumb, Pinkie, Base, ThrottleModule, WheelHub }
public enum HMTriggerKind      { Analog, Digital }
public enum HMDpadEncoding     { Hat, Buttons }
public enum HMStickSide        { Left, Right }
public enum HMTriggerSide      { Left, Right }
public enum HMRumbleKind       { None, SingleErm, DualErm, VoiceCoilHaptic, ImpulseTriggers }

// =====================================================================
// Reusable sub-records
// =====================================================================

public sealed record HMStick
{
    public HMStickSide Side { get; init; }
    public HMAxis XAxis { get; init; }
    public HMAxis YAxis { get; init; }
    public int? ClickButton { get; init; }
}

public sealed record HMTrigger
{
    public HMAxis Axis { get; init; }
    public HMTriggerSide Side { get; init; }
    public HMTriggerKind Kind { get; init; } = HMTriggerKind.Analog;
    public int? Stages { get; init; } // 1 = single-stage, 2 = two-stage detent (X52 trigger)
}

public sealed record HMDpad
{
    public HMDpadEncoding Encoding { get; init; }
    public HMAxis? HatAxis { get; init; }
    public int? HatPositions { get; init; }
    public int? UpButton { get; init; }
    public int? DownButton { get; init; }
    public int? LeftButton { get; init; }
    public int? RightButton { get; init; }
}

public sealed record HMButtonBinding
{
    public HMButtonRole Role { get; init; }
    public int ButtonIndex { get; init; }
    public string? Label { get; init; } // optional human-friendly override
}

public sealed record HMHatBinding
{
    public HMAxis Axis { get; init; }
    public int Positions { get; init; }
    public HMHatLocation Location { get; init; } = HMHatLocation.Unknown;
    public string? Role { get; init; } // free-text role for HOTAS hats with non-enum roles
}

public sealed record HMHaptics
{
    public HMRumbleKind Rumble { get; init; } = HMRumbleKind.None;
    public bool TriggerHaptics { get; init; }
}

public sealed record HMImu
{
    public bool Accelerometer { get; init; }
    public bool Gyroscope { get; init; }
    public bool Magnetometer { get; init; }
}

public sealed record HMRudder
{
    public HMAxis Axis { get; init; }
    public HMRudderKind Kind { get; init; }
}

public sealed record HMWheelSpec
{
    public HMAxis Axis { get; init; }
    public int? RotationDegrees { get; init; }
    public bool ForceFeedback { get; init; }
}

public sealed record HMPedal
{
    public HMAxis Axis { get; init; }
    public HMAxisRole Role { get; init; }   // Throttle | Brake | Clutch | Rudder | Accelerator
    public HMPedalType Type { get; init; } = HMPedalType.Unknown;
}

public sealed record HMShifter
{
    public HMShifterKind Kind { get; init; }
    public int? ButtonIndex { get; init; }
    public HMShifterActuation Actuation { get; init; } = HMShifterActuation.Digital;
}

public sealed record HMRotaryEncoder
{
    public HMAxis Axis { get; init; }
    public HMAxisRole Role { get; init; } = HMAxisRole.Dial;
    public int? Positions { get; init; } // for detented encoders; null = continuous
}

public sealed record HMRevIndicator
{
    public int LedCount { get; init; }
}

public sealed record HMTriggerButton
{
    public int ButtonIndex { get; init; }
    public int Stages { get; init; } = 1;
}

// =====================================================================
// Simple-view records (the "I just want sticks and triggers" surface)
// =====================================================================

/// <summary>v1.3.9 — flat-list view of one stick a profile exposes,
/// surfaced via <see cref="HMProfile.Sticks"/>. Variable count: typical
/// gamepad has 2 (left + right), a flight stick / wheel / HOTAS has 1,
/// a pedals-only device has 0. PadForge-style consumers iterate this
/// list to render a stick widget per entry.</summary>
public sealed record HMSimpleStick
{
    public HMAxis XAxis { get; init; }
    public HMAxis YAxis { get; init; }   // HMAxis.None when only X is used (1D stick)
    public HMAxisRole RoleX { get; init; } = HMAxisRole.Unknown;
    public HMAxisRole RoleY { get; init; } = HMAxisRole.Unknown;
    public string? Label { get; init; }
}

/// <summary>v1.3.9 — flat-list view of one trigger axis the profile
/// exposes, surfaced via <see cref="HMProfile.Triggers"/>. Variable count:
/// typical gamepad has 2 (LT/RT), a 3-pedal sim set has 3 (gas/brake/clutch),
/// a handbrake has 1, a HOTAS may have throttle + twist rudder + slider.
/// The first two entries are also reachable via the encoder's
/// <see cref="HMGamepadState.LeftTrigger"/> / <see cref="HMGamepadState.RightTrigger"/>
/// slots; additional triggers are encoder-reachable only via
/// <see cref="HMGamepadState.ExtraAxes"/>.</summary>
public sealed record HMSimpleTrigger
{
    public HMAxis Axis { get; init; }
    public HMAxisRole Role { get; init; } = HMAxisRole.Unknown;
    public string? Label { get; init; }
}

// =====================================================================
// HMLayout — abstract base + per-kind concrete records
// =====================================================================

/// <summary>Per-profile structured physical-design declaration. Each
/// concrete subclass corresponds to one <see cref="HMLayoutKind"/>
/// value and exposes the fields that kind requires.</summary>
public abstract record HMLayout
{
    public abstract HMLayoutKind Kind { get; }

    /// <summary>Optional manufacturer/spec source URL the layout was
    /// authored from. Stripped from embedded resource at build time;
    /// useful during development/audit.</summary>
    public string? Source { get; init; }
}

public sealed record HMUnspecifiedLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Unspecified;
    public string? Note { get; init; }
}

public sealed record HMGamepadLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Gamepad;
    public IReadOnlyList<HMStick> Sticks { get; init; } = Array.Empty<HMStick>();
    public IReadOnlyList<HMTrigger> Triggers { get; init; } = Array.Empty<HMTrigger>();
    public HMDpad? Dpad { get; init; }
    public IReadOnlyList<HMButtonBinding> FaceButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> ShoulderButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> SystemButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> ExtraButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public HMHaptics? Haptics { get; init; }
    public HMImu? Imu { get; init; }
}

public sealed record HMJoystickLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Joystick;
    public HMStick Stick { get; init; } = new();
    public HMRudder? Rudder { get; init; }
    public HMPedal? Throttle { get; init; }   // single-throttle slider on stick base
    public IReadOnlyList<HMHatBinding> Hats { get; init; } = Array.Empty<HMHatBinding>();
    public HMTriggerButton? Trigger { get; init; }
    public IReadOnlyList<HMButtonBinding> StickButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> BaseButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public bool ForceFeedback { get; init; }
}

public sealed record HMFlightStickLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.FlightStick;
    public HMStick Stick { get; init; } = new();
    public HMRudder? Rudder { get; init; }
    public HMPedal? Throttle { get; init; }
    public IReadOnlyList<HMHatBinding> Hats { get; init; } = Array.Empty<HMHatBinding>();
    public HMTriggerButton? Trigger { get; init; }
    public IReadOnlyList<HMButtonBinding> StickButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> BaseButtons { get; init; } = Array.Empty<HMButtonBinding>();
}

public sealed record HMHotasLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Hotas;
    public HMStick Stick { get; init; } = new();
    public HMRudder? StickRudder { get; init; }       // twist-grip rudder on the stick (if any)
    public IReadOnlyList<HMHatBinding> StickHats { get; init; } = Array.Empty<HMHatBinding>();
    public HMTriggerButton? StickTrigger { get; init; }
    public IReadOnlyList<HMButtonBinding> StickButtons { get; init; } = Array.Empty<HMButtonBinding>();

    // Throttle module
    public HMPedal? ThrottlePrimary { get; init; }       // main throttle (Throttle role)
    public IReadOnlyList<HMRotaryEncoder> ThrottleSecondary { get; init; } = Array.Empty<HMRotaryEncoder>();
    public IReadOnlyList<HMHatBinding> ThrottleHats { get; init; } = Array.Empty<HMHatBinding>();
    public IReadOnlyList<HMButtonBinding> ThrottleButtons { get; init; } = Array.Empty<HMButtonBinding>();

    // Rudder module (some HOTAS sets bundle a separate rudder rocker on the throttle base)
    public HMPedal? RudderModule { get; init; }
}

public sealed record HMWheelLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Wheel;
    public HMWheelSpec Wheel { get; init; } = new();
    public IReadOnlyList<HMPedal> Pedals { get; init; } = Array.Empty<HMPedal>();
    public IReadOnlyList<HMShifter> Shifters { get; init; } = Array.Empty<HMShifter>();
    public IReadOnlyList<HMButtonBinding> WheelButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMRotaryEncoder> RotaryEncoders { get; init; } = Array.Empty<HMRotaryEncoder>();
    public HMDpad? Dpad { get; init; }
    public HMRevIndicator? RevIndicator { get; init; }
}

public sealed record HMPedalsLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Pedals;
    public IReadOnlyList<HMPedal> Pedals { get; init; } = Array.Empty<HMPedal>();
}

public sealed record HMShifterLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Shifter;
    public IReadOnlyList<HMShifterKind> Modes { get; init; } = Array.Empty<HMShifterKind>();
    public IReadOnlyDictionary<string, HMButtonBinding>? HPatternGears { get; init; }
    public int? SequentialUpButton { get; init; }
    public int? SequentialDownButton { get; init; }
}

public sealed record HMHandbrakeLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Handbrake;
    public HMAxis Axis { get; init; }
}

public sealed record HMSingleAxisAccessoryLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.SingleAxisAccessory;
    public HMAxis Axis { get; init; }
    public HMAxisRole Role { get; init; } = HMAxisRole.Unknown;
}

public sealed record HMArcadeStickLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.ArcadeStick;
    public HMDpad Joystick { get; init; } = new();
    public IReadOnlyList<HMButtonBinding> FaceButtons { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> SystemButtons { get; init; } = Array.Empty<HMButtonBinding>();
}

public sealed record HMDancePadLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.DancePad;
    public IReadOnlyDictionary<string, HMButtonBinding> Pads { get; init; } =
        new Dictionary<string, HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> SystemButtons { get; init; } = Array.Empty<HMButtonBinding>();
}

public sealed record HMGuitarLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Guitar;
    public IReadOnlyList<HMButtonBinding> Frets { get; init; } = Array.Empty<HMButtonBinding>();
    public IReadOnlyList<HMButtonBinding> SoloFrets { get; init; } = Array.Empty<HMButtonBinding>();
    public HMDpad? Strum { get; init; }    // up/down dpad encoding
    public HMAxis? WhammyAxis { get; init; }
    public int? TiltButton { get; init; }
    public IReadOnlyList<HMButtonBinding> SystemButtons { get; init; } = Array.Empty<HMButtonBinding>();
}

public sealed record HMMotionWandLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.MotionWand;
    public HMTriggerButton? Trigger { get; init; }
    public HMAxis? TriggerAxis { get; init; } // PS Move T trigger has analog axis
    public IReadOnlyList<HMButtonBinding> Buttons { get; init; } = Array.Empty<HMButtonBinding>();
    public HMImu? Imu { get; init; }
    public bool RgbLightbar { get; init; }
}

public sealed record HMRemoteLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.Remote;
    public IReadOnlyList<HMButtonBinding> Buttons { get; init; } = Array.Empty<HMButtonBinding>();
}

public sealed record HMControllerAdapterLayout : HMLayout
{
    public override HMLayoutKind Kind => HMLayoutKind.ControllerAdapter;
    public int Ports { get; init; }
    public string? PerPortLayout { get; init; } // free-text description of each port's per-port shape
}
