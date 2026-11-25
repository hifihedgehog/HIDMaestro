using System;

namespace HIDMaestro;

/// <summary>One contact on a touchscreen or touchpad, for
/// <see cref="HMController.SubmitTouch"/>.
///
/// <para>Every value follows the profile's descriptor, as
/// <see cref="HMMouseState"/>'s absolute position does: <see cref="X"/> and
/// <see cref="Y"/> are positions inside the field's logical range
/// (<see cref="HMTouchLayout.X"/> and <see cref="HMTouchLayout.Y"/>), clamped
/// to it, and so are <see cref="Width"/>, <see cref="Height"/>,
/// <see cref="Pressure"/> and <see cref="Azimuth"/> where the descriptor has
/// them. Windows maps a touchscreen's whole range onto the display it
/// assigns the screen to, the primary display unless Tablet PC Settings
/// says otherwise.</para></summary>
public struct HMTouchContact
{
    /// <summary>The contact's identifier, the same from the report that puts
    /// it down to the one that lifts it, inside the descriptor's Contact
    /// Identifier range (<see cref="HMTouchLayout.MaxContactId"/>).</summary>
    public int Id;
    public int X;
    public int Y;
    /// <summary>In range of the surface without touching it: Tip Switch off,
    /// In Range on, on a descriptor that declares In Range
    /// (<see cref="HMTouchLayout.HasInRange"/>). On one that does not, a
    /// hovering contact reads as lifted.</summary>
    public bool Hover;
    /// <summary>A contact that is not a finger, such as a palm: Confidence
    /// off, on a descriptor that declares Confidence.</summary>
    public bool Palm;
    public int Width;
    public int Height;
    public int Pressure;
    public int Azimuth;
}

/// <summary>Pen buttons and modes, for <see cref="HMPenState"/>.</summary>
[Flags]
public enum HMPenButtons : byte
{
    None            = 0,
    /// <summary>Barrel Switch, the button on the pen's side.</summary>
    Barrel          = 1 << 0,
    /// <summary>Eraser, the eraser end touching.</summary>
    Eraser          = 1 << 1,
    /// <summary>Invert, the eraser end toward the surface.</summary>
    Invert          = 1 << 2,
    /// <summary>Secondary Barrel Switch.</summary>
    SecondaryBarrel = 1 << 3,
}

/// <summary>One pen report's worth of input, for
/// <see cref="HMController.SubmitPen"/>. Values follow the profile's
/// descriptor and are clamped to each field's logical range
/// (<see cref="HMPenLayout"/>). A pen out of range reports nothing but that:
/// every switch off and pressure zero, at its last position.</summary>
public struct HMPenState
{
    public bool InRange;
    /// <summary>The tip touching the surface. A tip down is in range.</summary>
    public bool Tip;
    public HMPenButtons Buttons;
    public int X;
    public int Y;
    public int Pressure;
    public int TiltX;
    public int TiltY;
    public int Twist;
}

/// <summary>A field's logical range, minimum and maximum inclusive.</summary>
public readonly record struct HMRange(int Minimum, int Maximum);

/// <summary>What a profile's touchscreen or touchpad collection carries,
/// from its descriptor (<see cref="HMProfile.Touch"/>).</summary>
public sealed class HMTouchLayout
{
    internal HMTouchLayout() { }

    /// <summary>True for a touchpad (Digitizer, Touch Pad), false for a
    /// touchscreen (Digitizer, Touch Screen).</summary>
    public bool IsTouchpad { get; internal init; }

    /// <summary>The contacts one report carries. A submit with more sends
    /// several reports, the first with the whole count, as Windows' hybrid
    /// mode reads them.</summary>
    public int ContactsPerReport { get; internal init; }

    /// <summary>The most contacts one submit can carry: the Contact Count
    /// field's maximum, or <see cref="ContactsPerReport"/> without
    /// one.</summary>
    public int MaxContacts { get; internal init; }

    /// <summary>The largest Contact Identifier the descriptor can
    /// carry.</summary>
    public int MaxContactId { get; internal init; }

    public HMRange X { get; internal init; }
    public HMRange Y { get; internal init; }
    public HMRange? Width { get; internal init; }
    public HMRange? Height { get; internal init; }
    public HMRange? Pressure { get; internal init; }
    public HMRange? Azimuth { get; internal init; }

    /// <summary>The touchpad buttons the report carries, Button 1 first.
    /// None on a touchscreen.</summary>
    public HMMouseButtons Buttons { get; internal init; }

    /// <summary>True when every finger declares In Range, so a contact can
    /// hover (<see cref="HMTouchContact.Hover"/>). The Elan screen of the
    /// Dell XPS 13 9360 declares none: a hovering contact there reads as
    /// lifted.</summary>
    public bool HasInRange { get; internal init; }
}

/// <summary>What a profile's pen collection carries, from its descriptor
/// (<see cref="HMProfile.Pen"/>).</summary>
public sealed class HMPenLayout
{
    internal HMPenLayout() { }

    public HMRange X { get; internal init; }
    public HMRange Y { get; internal init; }
    public HMRange? Pressure { get; internal init; }
    public HMRange? TiltX { get; internal init; }
    public HMRange? TiltY { get; internal init; }
    public HMRange? Twist { get; internal init; }

    /// <summary>The buttons and modes the report carries.</summary>
    public HMPenButtons Buttons { get; internal init; }

    /// <summary>True when the report declares In Range, so a pen can hover.</summary>
    public bool HasInRange { get; internal init; }
}
