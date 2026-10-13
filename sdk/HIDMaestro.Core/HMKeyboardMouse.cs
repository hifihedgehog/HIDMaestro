using System;

namespace HIDMaestro;

/// <summary>Mouse buttons, numbered as the HID Button page numbers them:
/// bit n is Button n+1. Windows reads Button 1 to 5 of a mouse collection
/// as left, right, middle, back and forward, and Raw Input's RAWMOUSE carries no
/// more than those five. A profile can declare more buttons, in the mouse
/// collection or in a second collection of its own, and
/// <see cref="HMController.SubmitMouse"/> fills them in order (issue
/// #67).</summary>
[Flags]
public enum HMMouseButtons : uint
{
    None     = 0,
    Left     = 1u << 0,
    Right    = 1u << 1,
    Middle   = 1u << 2,
    Back     = 1u << 3,
    Forward  = 1u << 4,
    Button6  = 1u << 5,
    Button7  = 1u << 6,
    Button8  = 1u << 7,
    Button9  = 1u << 8,
    Button10 = 1u << 9,
    Button11 = 1u << 10,
    Button12 = 1u << 11,
    Button13 = 1u << 12,
    Button14 = 1u << 13,
    Button15 = 1u << 14,
    Button16 = 1u << 15,
}

/// <summary>One mouse report's worth of input for
/// <see cref="HMController.SubmitMouse"/>.
///
/// <para><see cref="X"/> and <see cref="Y"/> follow the profile's
/// descriptor. A relative pointer takes a movement in counts since the last
/// report. One larger than the field holds is sent as several reports, every
/// one of which arrives. An absolute pointer takes a position inside the
/// field's logical range (0 to 32767 for the <see cref="HidDescriptorBuilder.Mouse"/>
/// absolute form), clamped to it. <see cref="Wheel"/> and <see cref="Pan"/>
/// are detents, positive up and right.</para></summary>
public struct HMMouseState
{
    public HMMouseButtons Buttons;
    public int X;
    public int Y;
    public int Wheel;
    public int Pan;
}

/// <summary>Keyboard modifier keys, in the order of their HID usages
/// (Keyboard/Keypad page 0xE0 to 0xE7).</summary>
[Flags]
public enum HMKeyModifiers : byte
{
    None         = 0,
    LeftControl  = 1 << 0,
    LeftShift    = 1 << 1,
    LeftAlt      = 1 << 2,
    LeftGui      = 1 << 3,
    RightControl = 1 << 4,
    RightShift   = 1 << 5,
    RightAlt     = 1 << 6,
    RightGui     = 1 << 7,
}

/// <summary>Keyboard LEDs as a host sets them, in the order of their HID
/// usages (LED page 0x01 to 0x05).</summary>
[Flags]
public enum HMKeyboardLeds : byte
{
    None       = 0,
    NumLock    = 1 << 0,
    CapsLock   = 1 << 1,
    ScrollLock = 1 << 2,
    Compose    = 1 << 3,
    Kana       = 1 << 4,
}

/// <summary>Keys by their HID usage on the Keyboard/Keypad page (HID Usage
/// Tables 1.5, section 10). The value is the usage ID, so any usage this
/// list does not name can be passed as <c>(HMKey)0xNN</c>.</summary>
public enum HMKey : byte
{
    None = 0x00,
    ErrorRollOver = 0x01,
    A = 0x04, B = 0x05, C = 0x06, D = 0x07, E = 0x08, F = 0x09, G = 0x0A, H = 0x0B,
    I = 0x0C, J = 0x0D, K = 0x0E, L = 0x0F, M = 0x10, N = 0x11, O = 0x12, P = 0x13,
    Q = 0x14, R = 0x15, S = 0x16, T = 0x17, U = 0x18, V = 0x19, W = 0x1A, X = 0x1B,
    Y = 0x1C, Z = 0x1D,
    D1 = 0x1E, D2 = 0x1F, D3 = 0x20, D4 = 0x21, D5 = 0x22,
    D6 = 0x23, D7 = 0x24, D8 = 0x25, D9 = 0x26, D0 = 0x27,
    Enter = 0x28, Escape = 0x29, Backspace = 0x2A, Tab = 0x2B, Space = 0x2C,
    Minus = 0x2D, Equals = 0x2E, LeftBracket = 0x2F, RightBracket = 0x30,
    Backslash = 0x31, NonUsHash = 0x32, Semicolon = 0x33, Apostrophe = 0x34,
    Grave = 0x35, Comma = 0x36, Period = 0x37, Slash = 0x38, CapsLock = 0x39,
    F1 = 0x3A, F2 = 0x3B, F3 = 0x3C, F4 = 0x3D, F5 = 0x3E, F6 = 0x3F,
    F7 = 0x40, F8 = 0x41, F9 = 0x42, F10 = 0x43, F11 = 0x44, F12 = 0x45,
    PrintScreen = 0x46, ScrollLock = 0x47, Pause = 0x48, Insert = 0x49,
    Home = 0x4A, PageUp = 0x4B, Delete = 0x4C, End = 0x4D, PageDown = 0x4E,
    Right = 0x4F, Left = 0x50, Down = 0x51, Up = 0x52,
    NumLock = 0x53, KeypadDivide = 0x54, KeypadMultiply = 0x55, KeypadMinus = 0x56,
    KeypadPlus = 0x57, KeypadEnter = 0x58,
    Keypad1 = 0x59, Keypad2 = 0x5A, Keypad3 = 0x5B, Keypad4 = 0x5C, Keypad5 = 0x5D,
    Keypad6 = 0x5E, Keypad7 = 0x5F, Keypad8 = 0x60, Keypad9 = 0x61, Keypad0 = 0x62,
    KeypadPeriod = 0x63, NonUsBackslash = 0x64, Application = 0x65, Power = 0x66,
    KeypadEquals = 0x67,
    F13 = 0x68, F14 = 0x69, F15 = 0x6A, F16 = 0x6B, F17 = 0x6C, F18 = 0x6D,
    F19 = 0x6E, F20 = 0x6F, F21 = 0x70, F22 = 0x71, F23 = 0x72, F24 = 0x73,
    Execute = 0x74, Help = 0x75, Menu = 0x76, Select = 0x77, Stop = 0x78,
    Again = 0x79, Undo = 0x7A, Cut = 0x7B, Copy = 0x7C, Paste = 0x7D, Find = 0x7E,
    Mute = 0x7F, VolumeUp = 0x80, VolumeDown = 0x81,
    KeypadComma = 0x85, International1 = 0x87, International2 = 0x88,
    International3 = 0x89, International4 = 0x8A, International5 = 0x8B,
    Lang1 = 0x90, Lang2 = 0x91,
    LeftControl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftGui = 0xE3,
    RightControl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightGui = 0xE7,
}
