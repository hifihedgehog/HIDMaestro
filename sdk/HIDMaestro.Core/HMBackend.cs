using System;

namespace HIDMaestro;

/// <summary>
/// The way a virtual controller is presented to Windows.
///
/// <para>A profile whose <see cref="HMProfile.Backend"/> is <c>"usbip"</c>
/// has interfaces only the USB/IP backend presents, and runs there on every
/// machine. Every other profile with a HID descriptor runs on the UMDF2
/// driver, and on the USB/IP backend too unless it is an Xbox controller
/// that reaches XInput through the XUSB companion or xinputhid, which the
/// USB/IP backend does not present. <see cref="HMContext.MachineBackend"/>
/// says what <see cref="Auto"/> picks on this machine for every profile not
/// bound to USB/IP, and <see cref="HMProfile.SupportsBackend"/> says
/// whether a profile runs on a backend.</para>
/// </summary>
public enum HMBackend
{
    /// <summary>The backend the profile and the machine call for:
    /// <see cref="Usbip"/> for a profile whose <see cref="HMProfile.Backend"/>
    /// is <c>"usbip"</c>, and otherwise <see cref="HMContext.MachineBackend"/>.
    /// The default of every context and every create.</summary>
    Auto = 0,

    /// <summary>HIDMaestro's own UMDF2 driver: a HID device on the root
    /// enumerator, with the XUSB companion or the xinputhid filter an Xbox
    /// profile asks for.</summary>
    Umdf2 = 1,

    /// <summary>A USB device on the USB/IP host controller HIDMaestro.Core
    /// carries, bound by Windows' own class drivers. A profile with no
    /// <c>usbConfiguration</c> presents one HID interface built from its
    /// own descriptor and identity.</summary>
    Usbip = 2,
}

/// <summary>
/// The profile's data cannot be presented on the backend the create asked
/// for: <see cref="HMBackend.Umdf2"/> for a profile only the USB/IP backend
/// presents, and <see cref="HMBackend.Usbip"/> for a profile whose device
/// the USB/IP backend has no presentation for yet, such as an Xbox
/// controller that reaches XInput through its own driver stack. Derived
/// from <see cref="InvalidOperationException"/>, and thrown before the
/// create takes anything machine-wide.
/// <see cref="HMProfile.SupportsBackend"/> answers without the exception:
/// for <see cref="HMBackend.Auto"/>, about the backend Auto resolves to on
/// this machine.
/// </summary>
public sealed class HMBackendNotSupportedException : InvalidOperationException
{
    /// <summary>The profile's id.</summary>
    public string ProfileId { get; }

    /// <summary>The backend the create resolved to.</summary>
    public HMBackend Backend { get; }

    internal HMBackendNotSupportedException(string profileId, HMBackend backend, string reason)
        : base($"Profile '{profileId}' cannot run on the {Name(backend)} backend: {reason}")
    {
        ProfileId = profileId;
        Backend = backend;
    }

    private static string Name(HMBackend backend) => backend switch
    {
        HMBackend.Umdf2 => "UMDF2",
        HMBackend.Usbip => "USB/IP",
        _ => backend.ToString(),
    };
}
