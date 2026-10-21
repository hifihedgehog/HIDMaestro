using System;
using System.Collections.Generic;
using HIDMaestro.Internal.Uninstall;

namespace HIDMaestro;

/// <summary>
/// Removes HIDMaestro from the machine: every virtual controller and record
/// of one, the driver packages, the signing certificate, the registry keys
/// and the files the SDK wrote, and the shared Windows keys it changed, put
/// back as they were. For an application's uninstaller or a settings button.
/// docs/UNINSTALL.md describes each artifact and what stays.
///
/// <para>The driver is shared by every program on the machine that embeds
/// HIDMaestro. <see cref="Run"/> refuses while another one runs, and the
/// others install the driver again at their next
/// <see cref="HMContext.InstallDriver"/>.</para>
///
/// <para>The calling program is never its own blocker. Its own controllers
/// are removed with everything else, as
/// <see cref="HMContext.RemoveAllVirtualControllers(HMCleanupScope, bool)"/>
/// with <see cref="HMCleanupScope.Everything"/> removes them, and its
/// <see cref="HMController"/> objects stay usable with no device behind
/// them until disposed.</para>
/// </summary>
public static class HMUninstall
{
    /// <summary>List what <see cref="Run"/> would remove and keep, and the
    /// running HIDMaestro programs that would block it. Changes nothing.
    /// Reads more with administrator rights: without them, a folder or key
    /// it cannot read is left out of the list.</summary>
    public static HMUninstallReport Preview(HMUninstallOptions? options = null) =>
        Uninstaller.Preview(options ?? new HMUninstallOptions());

    /// <summary>Remove HIDMaestro from the machine, or, while another
    /// program that embeds HIDMaestro runs, change nothing and return
    /// <see cref="HMUninstallStatus.Blocked"/> with the programs listed. Runs
    /// on the calling thread to its end, which takes seconds to minutes.
    /// Requires administrator rights.
    ///
    /// <para>A program on HIDMaestro 2.0.1 or later that starts during the
    /// removal waits for its end before its first owner claim, which
    /// <see cref="HMContext.InstallDriver"/>, a create, a cleanup, an OEM
    /// name override, the VR driver registration and the transport install
    /// each take first, and then installs the driver again. A program that
    /// holds its claim counts as running, one that only set an OEM name
    /// override included. An older program does not wait. The removal checks for
    /// running programs before it removes devices, before it puts back the
    /// OEM names and while it holds the driver deploy lock, and one it finds
    /// stops it before that step with the status Blocked.</para>
    ///
    /// <para>A file another program holds open inside
    /// %ProgramFiles%\HIDMaestro, in a folder only administrators can change,
    /// is deleted when Windows next starts, and the status is then
    /// <see cref="HMUninstallStatus.RemovedRestartPending"/>.
    /// One anywhere else, such as the OpenVR driver while SteamVR runs,
    /// stays, and is reported failed: close the program and run the removal
    /// again. The machine is never restarted.</para></summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    public static HMUninstallReport Run(HMUninstallOptions? options = null) =>
        Uninstaller.Run(options ?? new HMUninstallOptions());
}

/// <summary>Options for <see cref="HMUninstall"/>.</summary>
public sealed class HMUninstallOptions
{
    /// <summary>Remove usbip-win2's host controller, its driver package, its
    /// scheduled task, its folder and its Apps entry even when no install
    /// record shows HIDMaestro installed it. Never its root-hub filter, whose
    /// removal restarts every USB root hub. Still refused when another
    /// program uses usbip-win2.</summary>
    public bool IncludeUsbip { get; init; }
}

/// <summary>How a removal ended.</summary>
public enum HMUninstallStatus
{
    /// <summary>Everything found was removed or deliberately kept.</summary>
    Removed,

    /// <summary>As <see cref="Removed"/>, with at least one item that
    /// Windows deletes when it next starts.</summary>
    RemovedRestartPending,

    /// <summary>Another program that embeds HIDMaestro runs, and
    /// <see cref="HMUninstallReport.RunningPrograms"/> lists it. A removal
    /// that finds one before it starts changes nothing. One that finds a
    /// program started while it ran stops before the next step, which would
    /// take that program's controllers, overrides or driver, and its entries
    /// say what it had done and what it left. Run it again once the program
    /// has exited.</summary>
    Blocked,

    /// <summary>At least one item could not be removed, or for a preview,
    /// part of the machine could not be read. Its entry says why.</summary>
    Failed,

    /// <summary>A preview: nothing was changed, and no program blocks a
    /// removal.</summary>
    Previewed,
}

/// <summary>What a removal did, or would do, to one item.</summary>
public enum HMUninstallOutcome
{
    /// <summary>A preview found it, and a removal would take it.</summary>
    WouldRemove,

    /// <summary>Removed, or for a shared Windows key, put back as it was
    /// before HIDMaestro changed it.</summary>
    Removed,

    /// <summary>Taken out of use now and deleted when Windows next
    /// starts.</summary>
    RemovedAtRestart,

    /// <summary>Left on purpose. <see cref="HMUninstallEntry.Reason"/> says
    /// why.</summary>
    Kept,

    /// <summary>The removal tried and failed. <see cref="HMUninstallEntry.Reason"/>
    /// says how.</summary>
    Failed,
}

/// <summary>One item a removal found.</summary>
public sealed class HMUninstallEntry
{
    internal HMUninstallEntry(string kind, string location, HMUninstallOutcome outcome, string? reason)
    {
        Kind = kind;
        Location = location;
        Outcome = outcome;
        Reason = reason;
    }

    /// <summary>What the item is: <c>devnode</c>, <c>interface registration</c>,
    /// <c>shared registry key</c>, <c>shared registry value</c>,
    /// <c>WinUSB binding</c>, <c>OEM name override</c>,
    /// <c>DeviceOverrides key</c>, <c>OpenVR registration</c>,
    /// <c>driver package</c>, <c>WUDF service key</c>, <c>registry key</c>,
    /// <c>certificate</c>, <c>folder</c>, <c>service setting</c>,
    /// <c>removal step</c>, or, from the usbip-win2 part, <c>devnode</c>,
    /// <c>driver package</c>, <c>service</c>, <c>registry key</c>,
    /// <c>registry value</c>, <c>scheduled task</c>, <c>folder</c>,
    /// <c>file</c>, <c>Apps entry</c>, <c>hardware id</c> or <c>USB/IP
    /// import</c>.</summary>
    public string Kind { get; }

    /// <summary>Where it is: a device instance ID, a registry path under
    /// HKLM or HKCU, a driver package's published name, a certificate
    /// store and thumbprint, or a path.</summary>
    public string Location { get; }

    /// <summary>What happened to it.</summary>
    public HMUninstallOutcome Outcome { get; }

    /// <summary>Why it was kept, or how it failed. Null otherwise, or a
    /// note on a shared key that was put back.</summary>
    public string? Reason { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        Reason == null ? $"{Outcome} {Kind} {Location}" : $"{Outcome} {Kind} {Location}: {Reason}";
}

/// <summary>A running program that embeds HIDMaestro.</summary>
public sealed class HMRunningProgram
{
    internal HMRunningProgram(int processId, string? imagePath, string? sdkVersion)
    {
        ProcessId = processId;
        ImagePath = imagePath;
        SdkVersion = sdkVersion;
    }

    /// <summary>Its process ID, or 0 when the program is known to run but
    /// its process could not be named.</summary>
    public int ProcessId { get; }

    /// <summary>The path of its executable, or null when it could not be
    /// read.</summary>
    public string? ImagePath { get; }

    /// <summary>The HIDMaestro version it runs, or null when unknown.
    /// A program found through the v1.12.0 pipes reports 1.12.0.</summary>
    public string? SdkVersion { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"process {ProcessId} ({ImagePath ?? "unknown image"}, HIDMaestro {SdkVersion ?? "unknown"})";
}

/// <summary>The result of <see cref="HMUninstall.Preview"/> or
/// <see cref="HMUninstall.Run"/>.</summary>
public sealed class HMUninstallReport
{
    internal HMUninstallReport(bool isPreview, HMUninstallStatus status,
        IReadOnlyList<HMRunningProgram> runningPrograms, IReadOnlyList<HMUninstallEntry> entries)
    {
        IsPreview = isPreview;
        Status = status;
        RunningPrograms = runningPrograms;
        Entries = entries;
    }

    /// <summary>True for <see cref="HMUninstall.Preview"/>.</summary>
    public bool IsPreview { get; }

    /// <summary>How it ended.</summary>
    public HMUninstallStatus Status { get; }

    /// <summary>The other HIDMaestro programs that block a removal. Empty
    /// unless <see cref="Status"/> is <see cref="HMUninstallStatus.Blocked"/>.
    /// Programs older than v1.12.0 leave no sign that they run and are not
    /// listed.</summary>
    public IReadOnlyList<HMRunningProgram> RunningPrograms { get; }

    /// <summary>One entry per item found. Empty for a blocked
    /// <see cref="HMUninstall.Run"/>, which looks no further.</summary>
    public IReadOnlyList<HMUninstallEntry> Entries { get; }
}
