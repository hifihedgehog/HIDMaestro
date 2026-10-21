using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>Entry point for the virtual VR controller subsystem (issue #32).
///
/// <para>VR controllers are not OS devices: they exist inside a VR
/// runtime's session, and SteamVR is the one runtime on Windows with a
/// public driver model. HIDMaestro therefore ships an OpenVR driver,
/// embedded in this assembly and registered with SteamVR on first use.
/// Nothing here runs, installs, or registers anything until a consumer
/// calls into it, and machines without SteamVR are entirely unaffected:
/// <see cref="IsSteamVRInstalled"/> simply reports false.</para>
///
/// <para>SteamVR itself is a free dependency, obtainable two ways: the
/// Steam client (app 250820), or entirely Steam-free via Valve's own
/// steamcmd
/// (<c>steamcmd +force_install_dir C:\SteamVR +login anonymous
/// +app_update 250820 validate +quit</c>), which needs no account and no
/// Steam client. Discovery here handles both shapes; steamcmd installs
/// are found via <see cref="SetSteamVRPathHint"/> or the conventional
/// <c>C:\SteamVR</c>.</para></summary>
public static class HMVR
{
    /// <summary>True when a SteamVR install (either shape) is present.</summary>
    public static bool IsSteamVRInstalled => VrDriverBuilder.IsSteamVRInstalled;

    /// <summary>True while vrserver.exe is running.</summary>
    public static bool IsSteamVRRunning => VrDriverBuilder.IsSteamVRRunning;

    /// <summary>Full path of the detected SteamVR install, or null.</summary>
    public static string? SteamVRPath => VrDriverBuilder.FindSteamVR();

    /// <summary>Record the location of a steamcmd-style SteamVR install
    /// (no registry footprint of its own) so discovery finds it from now
    /// on. Requires admin. Takes the program's HIDMaestro owner claim first,
    /// so it waits for a removal another program runs, and throws
    /// <see cref="InvalidOperationException"/> with nothing written while
    /// this process runs <see cref="HMUninstall.Run"/>.</summary>
    public static void SetSteamVRPathHint(string steamVrDir) =>
        VrDriverBuilder.SetSteamVRPathHint(steamVrDir);

    /// <summary>Extract the embedded OpenVR driver to its stable
    /// %ProgramData% home and register it with SteamVR in the OpenVR path
    /// registry, the file vrpathreg edits. The SDK writes that file itself
    /// and runs nothing from the SteamVR folder. Idempotent: the extraction
    /// is content-hash gated and the registration is written only when
    /// missing. Returns false when SteamVR is not installed. Requires admin.
    ///
    /// <para>The registry is the one in the profile of the account the
    /// process runs as: the signed-in user for a program the user elevated
    /// through UAC, the administrator's own account when an administrator's
    /// credentials were typed at a standard user's prompt, and SYSTEM's
    /// profile for a service. SteamVR reads the registry of the user it runs
    /// as, when it starts, so a driver registered while SteamVR runs loads
    /// at its next start. SteamVR shows no new-driver warning for it.</para>
    ///
    /// <para>A failure to update the registry throws
    /// <see cref="InvalidOperationException"/> with the cause as its inner
    /// exception.</para>
    ///
    /// <para>With SteamVR present, the call takes the program's HIDMaestro
    /// owner claim before it writes, so it waits for a removal another
    /// program runs (<see cref="HMUninstall"/>), and a removal refuses while
    /// this program runs. Without SteamVR it returns false and claims
    /// nothing, whatever else runs. With SteamVR present and this process's
    /// own removal running, it throws <see cref="InvalidOperationException"/>
    /// and writes nothing.</para></summary>
    public static bool EnsureDriverRegistered() => VrDriverBuilder.EnsureDriverRegistered();

    /// <summary>Unregister the driver from the same OpenVR path registry
    /// <see cref="EnsureDriverRegistered"/> writes, and clear the idempotence
    /// gate. A failure to update the registry throws
    /// <see cref="InvalidOperationException"/> with the cause as its inner
    /// exception. Requires admin. Waits for a removal another program runs,
    /// and does nothing while this process's own removal runs, which takes
    /// the registration itself.</summary>
    public static void UnregisterDriver() =>
        VrDriverBuilder.UnregisterDriver();

    /// <summary>The name SteamVR knows HIDMaestro's driver by
    /// (<c>hidmaestro</c>).</summary>
    public static string DriverName => VrDriverBuilder.DriverName;

    /// <summary>Folders of other OpenVR drivers SteamVR can see under
    /// <see cref="DriverName"/>: entries in the user's
    /// <c>openvrpaths.vrpath</c> external driver list and folders in
    /// SteamVR's own <c>drivers</c> directory, other than HIDMaestro's own.
    /// OpenVR requires a driver's name to be unique, so with any of these
    /// present SteamVR may load the other driver in place of HIDMaestro's.
    /// Empty when there is no conflict. Reads only and changes nothing.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> FindDriverNameConflicts() =>
        VrDriverBuilder.FindDriverNameConflicts();
}
