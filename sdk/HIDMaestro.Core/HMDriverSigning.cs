using System;
using System.Runtime.InteropServices;

namespace HIDMaestro;

/// <summary>What Windows requires of a driver package's signature before it
/// installs the package.</summary>
public enum HMDriverSigningRequirement
{
    /// <summary>A package signed by a publisher the machine trusts installs.
    /// HIDMaestro's driver installs.</summary>
    TrustedPublisher = 0,

    /// <summary>Only a package Microsoft signed installs. HIDMaestro signs
    /// its driver package on the machine, with a certificate it creates
    /// there, so its driver does not install.</summary>
    MicrosoftSigned = 1,
}

/// <summary>
/// What <see cref="HMDriverSigning.Check"/> read on this machine.
/// </summary>
public sealed class HMDriverSigningStatus
{
    /// <summary>What Windows requires here.</summary>
    public HMDriverSigningRequirement Requirement { get; }

    /// <summary>True when HIDMaestro's driver installs on this machine.
    /// False when <see cref="HMContext.InstallDriver"/>, and the create of
    /// a profile that runs on the driver, throw
    /// <see cref="HMDriverSigningException"/>. A composite profile
    /// (<see cref="HMProfile.RequiresUsbipBackend"/>) runs on usbip-win2's
    /// host controller instead, which this does not cover.</summary>
    public bool CanInstallDriver => Requirement == HMDriverSigningRequirement.TrustedPublisher;

    /// <summary>True when Windows enforces WHQL driver signing, which it
    /// decides as it starts. With it Windows on ARM64 and Windows in S mode
    /// install only Microsoft-signed driver packages. Windows on x64 outside
    /// S mode installs HIDMaestro's package with it set or clear.</summary>
    public bool WhqlEnforcement { get; }

    /// <summary>The machine's architecture.</summary>
    public Architecture OSArchitecture { get; }

    /// <summary>One or two sentences a program can show its user.</summary>
    public string Explanation => Requirement switch
    {
        HMDriverSigningRequirement.MicrosoftSigned when OSArchitecture == Architecture.Arm64 =>
            "Windows on this ARM64 PC installs only drivers that Microsoft signed. "
          + "HIDMaestro signs its driver on the PC, with a certificate it creates there, so the driver cannot be installed here.",
        HMDriverSigningRequirement.MicrosoftSigned =>
            "Windows in S mode installs only drivers that Microsoft signed. "
          + "HIDMaestro signs its driver on the PC, with a certificate it creates there, so the driver cannot be installed here.",
        _ => "Windows installs HIDMaestro's driver on this PC.",
    };

    /// <summary><see cref="Explanation"/>.</summary>
    public override string ToString() => Explanation;

    internal HMDriverSigningStatus(HMDriverSigningRequirement requirement, bool whqlEnforcement, Architecture architecture)
    {
        Requirement = requirement;
        WhqlEnforcement = whqlEnforcement;
        OSArchitecture = architecture;
    }
}

/// <summary>
/// Whether HIDMaestro's driver installs on this machine. HIDMaestro signs
/// its driver package on the machine with a certificate it creates there.
/// Windows on x64 installs such a package. Windows on ARM64 that enforces
/// WHQL driver signing, as an ARM64 PC with Secure Boot does, installs only
/// packages Microsoft signed, and so does Windows in S mode.
///
/// <para>A program asks before it offers virtual controllers, and shows
/// <see cref="HMDriverSigningStatus.Explanation"/> or its own text where
/// the driver cannot install:</para>
/// <code>
/// HMDriverSigningStatus signing = HMDriverSigning.Check();
/// if (!signing.CanInstallDriver)
///     ShowNotice(signing.Explanation);
/// </code>
///
/// <para>Memory integrity does not affect the answer. HIDMaestro's driver
/// installs and runs with it on.</para>
/// </summary>
public static class HMDriverSigning
{
    /// <summary>Reads what Windows requires of a driver package on this
    /// machine. It changes nothing, needs no elevation and no
    /// <see cref="HMContext"/>, and answers the same for the life of the
    /// process, since Windows decides it as it starts.</summary>
    public static HMDriverSigningStatus Check() => Internal.DriverSigningPolicy.Status;
}

/// <summary>
/// Windows on this machine installs only driver packages Microsoft signed,
/// and HIDMaestro's is signed on the machine. Derived from
/// <see cref="InvalidOperationException"/>. <see cref="HMContext.InstallDriver"/>
/// and the create of a profile that runs on the driver throw it before
/// they change anything on a machine that holds no HIDMaestro driver: no
/// certificate, no staged file and no service setting.
/// <see cref="HMDriverSigning.Check"/> gives the same answer without the
/// exception.
/// </summary>
public sealed class HMDriverSigningException : InvalidOperationException
{
    /// <summary>What Windows requires on this machine.</summary>
    public HMDriverSigningStatus Status { get; }

    internal HMDriverSigningException(HMDriverSigningStatus status, string? message = null)
        : base(message ?? status.Explanation)
    {
        Status = status;
    }
}
