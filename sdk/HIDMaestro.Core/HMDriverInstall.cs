using System;
using System.Collections.Generic;

namespace HIDMaestro;

/// <summary>What <see cref="HMContext.InstallDriver"/> did with the driver
/// it found installed.</summary>
public enum HMDriverInstallOutcome
{
    /// <summary>The installed driver is this SDK's. No driver package
    /// changed.</summary>
    AlreadyCurrent = 0,

    /// <summary>The driver's main package (hidmaestro.inf) was not
    /// installed, or this same build's driver was missing a package. This
    /// SDK installed its own driver, or added only the missing
    /// packages.</summary>
    Installed = 1,

    /// <summary>An older driver was installed and nothing was running on
    /// it. This SDK replaced it with its own.</summary>
    Upgraded = 2,

    /// <summary>A newer driver is installed. A driver is never downgraded,
    /// and this SDK runs on the newer one.</summary>
    KeptNewer = 3,

    /// <summary>An older driver is installed and controllers on this
    /// machine run on it, this program's own or another program's. It was
    /// kept, and this SDK runs on it. The next InstallDriver with nothing
    /// running on the driver upgrades it.</summary>
    KeptInUse = 4,
}

/// <summary>What an <see cref="HMDriverVersionException"/> is about.</summary>
public enum HMDriverVersionProblem
{
    /// <summary>The installed driver is older than this SDK supports, and
    /// controllers of running programs keep it from being replaced, by
    /// InstallDriver or by a create's deploy gate.
    /// <see cref="HMDriverVersionException.Blockers"/> names those devices and
    /// programs. Once they are gone, InstallDriver upgrades the driver.</summary>
    HeldOlderDriver = 0,

    /// <summary>v2.0.0: the driver that started a create's device does not
    /// serve this SDK's protocol 4. An older driver is upgraded by
    /// InstallDriver once nothing holds it. A newer one that dropped
    /// protocol 4 is never replaced by an older SDK: the program needs an SDK
    /// of that driver's release. The create removed its device.</summary>
    ProtocolNotServed = 1,

    /// <summary>v2.0.0: the driver failed the start of a create's device
    /// over a value the create wrote and the driver could not read, named
    /// in the message. The SDK and the driver disagree on that value's
    /// format. The create removed its device.</summary>
    ValueNotRead = 2,
}

/// <summary>
/// A driver this SDK cannot run on. Derived from
/// <see cref="InvalidOperationException"/>. <see cref="Problem"/> says which
/// of three things it is: an older driver held by running programs, which a
/// create's deploy gate and InstallDriver meet, or, once a create's device
/// started, a driver that does not serve protocol 4 or that could not read a
/// value the create wrote. A start that fails with no value named throws a
/// plain <see cref="InvalidOperationException"/> naming the device's problem
/// code instead.
/// </summary>
public sealed class HMDriverVersionException : InvalidOperationException
{
    /// <summary>Which of the three cases this is.</summary>
    public HMDriverVersionProblem Problem { get; }

    /// <summary>The installed or started driver's version: 0.0.0 when the
    /// driver wrote none, as a driver older than 1.13.0 does.</summary>
    public Version InstalledVersion { get; }

    /// <summary>The oldest driver this SDK runs on.</summary>
    public Version MinimumVersion { get; }

    /// <summary>For <see cref="HMDriverVersionProblem.HeldOlderDriver"/>, what
    /// holds the installed driver: a program, or a device whose program
    /// cannot be identified, which a program closes to let InstallDriver
    /// upgrade it. For the two start cases, the create's own device, which
    /// the create removed: nothing to close.</summary>
    public IReadOnlyList<string> Blockers { get; }

    internal HMDriverVersionException(Version installed, Version minimum, IReadOnlyList<string> blockers)
        : this(HMDriverVersionProblem.HeldOlderDriver,
               $"The installed HIDMaestro driver {installed} is older than {minimum}, the oldest this SDK runs on, " +
               $"and it cannot be replaced while these use it: {string.Join("; ", blockers)}. " +
               "Close those programs, or remove their controllers with " +
               "HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: true), and call InstallDriver again.",
               installed, minimum, blockers)
    {
    }

    internal HMDriverVersionException(HMDriverVersionProblem problem, string message, Version installed, Version minimum, IReadOnlyList<string> blockers)
        : base(message)
    {
        Problem = problem;
        InstalledVersion = installed;
        MinimumVersion = minimum;
        Blockers = blockers;
    }
}
