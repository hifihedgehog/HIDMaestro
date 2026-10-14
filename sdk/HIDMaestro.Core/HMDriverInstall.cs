using System;
using System.Collections.Generic;

namespace HIDMaestro;

/// <summary>What <see cref="HMContext.InstallDriver"/> did with the driver
/// it found installed.</summary>
public enum HMDriverInstallOutcome
{
    /// <summary>The installed driver is this SDK's. Nothing changed.</summary>
    AlreadyCurrent = 0,

    /// <summary>No driver was installed. This SDK installed its own.</summary>
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

/// <summary>
/// The installed driver is older than this SDK supports, and controllers of
/// running programs keep it from being replaced. Derived from
/// <see cref="InvalidOperationException"/>. The message and
/// <see cref="Blockers"/> name the devices and programs on the driver. Once
/// they are gone, InstallDriver upgrades it.
/// </summary>
public sealed class HMDriverVersionException : InvalidOperationException
{
    /// <summary>The installed driver's version.</summary>
    public Version InstalledVersion { get; }

    /// <summary>The oldest driver this SDK runs on.</summary>
    public Version MinimumVersion { get; }

    /// <summary>What holds the installed driver: a program, or a device
    /// whose program cannot be identified.</summary>
    public IReadOnlyList<string> Blockers { get; }

    internal HMDriverVersionException(Version installed, Version minimum, IReadOnlyList<string> blockers)
        : base($"The installed HIDMaestro driver {installed} is older than {minimum}, the oldest this SDK runs on, " +
               $"and it cannot be replaced while these use it: {string.Join("; ", blockers)}. " +
               "Close those programs, or remove their controllers with " +
               "HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything), and call InstallDriver again.")
    {
        InstalledVersion = installed;
        MinimumVersion = minimum;
        Blockers = blockers;
    }
}
