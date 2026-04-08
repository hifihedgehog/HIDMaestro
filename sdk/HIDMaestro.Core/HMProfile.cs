using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// A controller profile — the description of a real-world controller that a
/// virtual device can masquerade as. Profiles are immutable, owned by the
/// <see cref="HMContext"/> they were loaded from, and identified by a stable
/// string ID slug like "xbox-360-wired" or "dualsense".
///
/// Get instances via <see cref="HMContext.GetProfile(string)"/> or
/// <see cref="HMContext.AllProfiles"/>. The internal HID descriptor and
/// other implementation details are not part of the public surface.
/// </summary>
public sealed class HMProfile
{
    internal ControllerProfile Inner { get; }

    internal HMProfile(ControllerProfile inner) { Inner = inner; }

    /// <summary>Stable identifier slug, e.g. "xbox-series-xs-bt".</summary>
    public string Id => Inner.Id;

    /// <summary>Human-readable name shown in UIs, e.g. "Xbox Series X|S Controller (Bluetooth)".</summary>
    public string Name => Inner.Name;

    /// <summary>Vendor name, e.g. "Microsoft", "Sony", "Logitech".</summary>
    public string Vendor => Inner.Vendor;

    /// <summary>USB Vendor ID as a 16-bit integer.</summary>
    public ushort VendorId => Inner.VendorId;

    /// <summary>USB Product ID as a 16-bit integer.</summary>
    public ushort ProductId => Inner.ProductId;

    /// <summary>The product string the device reports to the OS, e.g. "Wireless Controller".</summary>
    public string ProductString => Inner.ProductString;

    /// <summary>Device Manager display name. Falls back to <see cref="ProductString"/>.</summary>
    public string DisplayName => Inner.DisplayName;

    /// <summary>Controller category — "gamepad", "wheel", "joystick", "arcade", etc.</summary>
    public string Type => Inner.Type;

    /// <summary>True if this profile has a HID descriptor and can be deployed as a virtual controller.
    /// Some catalog entries are placeholders without descriptors yet.</summary>
    public bool IsDeployable => Inner.HasDescriptor;

    public override string ToString() => $"{Id} ({Name})";
}
