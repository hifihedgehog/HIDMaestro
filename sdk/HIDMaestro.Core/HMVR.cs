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
    /// on. Requires admin.</summary>
    public static void SetSteamVRPathHint(string steamVrDir) =>
        VrDriverBuilder.SetSteamVRPathHint(steamVrDir);

    /// <summary>Extract the embedded OpenVR driver to its stable
    /// %ProgramData% home and register it with SteamVR via vrpathreg.
    /// Idempotent (content-hash gated); hot-plugs into a running SteamVR.
    /// Returns false when SteamVR is not installed. Requires admin.</summary>
    public static bool EnsureDriverRegistered() =>
        VrDriverBuilder.EnsureDriverRegistered();

    /// <summary>Unregister the driver and clear the idempotence gate.
    /// Requires admin.</summary>
    public static void UnregisterDriver() =>
        VrDriverBuilder.UnregisterDriver();
}
