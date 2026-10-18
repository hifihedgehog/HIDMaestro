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

    /// <summary>An older driver was installed. This SDK replaced it with
    /// its own when nothing ran on it, and otherwise added its own beside
    /// it: the devices it creates bind its driver, and the controllers
    /// already running keep the one they loaded until they restart.</summary>
    Upgraded = 2,

    /// <summary>A newer driver is installed. A driver is never downgraded,
    /// and this SDK runs on the newer one.</summary>
    KeptNewer = 3,
}

/// <summary>What an <see cref="HMDriverVersionException"/> is about.</summary>
public enum HMDriverVersionProblem
{
    /// <summary>v2.0.0: the driver that started a create's device does not
    /// serve this SDK's protocol 4. A create installs its own driver over an
    /// older one first, so this is a newer driver that dropped protocol 4,
    /// which an older SDK never replaces (the program needs an SDK of that
    /// driver's release), or a mis-stamped one. The create removed its
    /// device.</summary>
    ProtocolNotServed = 1,

    /// <summary>v2.0.0: the driver failed the start of a create's device
    /// over a value the create wrote and the driver could not read, named
    /// in the message. The SDK and the driver disagree on that value's
    /// format. The create removed its device.</summary>
    ValueNotRead = 2,
}

/// <summary>
/// A driver this SDK cannot run on, met once a create's device started:
/// one that does not serve protocol 4, or one that could not read a value
/// the create wrote. Derived from <see cref="InvalidOperationException"/>,
/// and <see cref="Problem"/> says which. An older driver is never a reason:
/// a create and InstallDriver install this SDK's own over it, whoever runs
/// on it. A start that fails with no value named throws a plain
/// <see cref="InvalidOperationException"/> naming the device's problem code
/// instead.
/// </summary>
public sealed class HMDriverVersionException : InvalidOperationException
{
    /// <summary>Which of the two cases this is.</summary>
    public HMDriverVersionProblem Problem { get; }

    /// <summary>The installed or started driver's version: 0.0.0 when the
    /// driver wrote none, as a driver older than 2.0.0 does.</summary>
    public Version InstalledVersion { get; }

    /// <summary>The oldest driver this SDK runs on.</summary>
    public Version MinimumVersion { get; }

    /// <summary>The create's own device, which the create removed: nothing
    /// to close.</summary>
    public IReadOnlyList<string> Blockers { get; }

    internal HMDriverVersionException(HMDriverVersionProblem problem, string message, Version installed, Version minimum, IReadOnlyList<string> blockers)
        : base(message)
    {
        Problem = problem;
        InstalledVersion = installed;
        MinimumVersion = minimum;
        Blockers = blockers;
    }
}
