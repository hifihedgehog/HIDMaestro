using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// The top-level entry point for the HIDMaestro SDK. Owns the in-process
/// state for one consuming application: loaded profile catalog, allocated
/// controller indices, and the lifecycle of every <see cref="HMController"/>
/// it creates.
///
/// <para><b>Lifecycle:</b> create one <see cref="HMContext"/> at app startup,
/// dispose at shutdown. Disposing the context disposes every controller it
/// owns. Multiple contexts in one process are supported but not encouraged
/// (they share the same controller-index pool).</para>
///
/// <para><b>Driver install:</b> the UMDF2 driver and its XUSB companion are
/// embedded as resources inside this assembly. On first run on a given
/// machine, call <see cref="InstallDriver"/> to extract them to %TEMP% and
/// register them with Windows via pnputil. This requires admin and only
/// needs to happen once per machine — subsequent runs detect that the
/// driver is already in the DriverStore and skip the install. The temp
/// extraction is deleted after install; nothing is left in the consuming
/// app's directory.</para>
///
/// <para><b>Admin requirement:</b> Windows requires SeLoadDriverPrivilege
/// (admin) for both <see cref="InstallDriver"/> and <see cref="CreateController"/>.
/// This matches every other virtual-controller library on Windows
/// (ViGEmBus, vJoy, etc.) and is fundamental — there is no API path that
/// lets a standard user create a HIDClass device. A future optional service
/// component may relay create requests on behalf of unprivileged consumers,
/// but the in-process path always requires admin.</para>
/// </summary>
public sealed class HMContext : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, HMController> _controllers = new();
    private readonly List<HMProfile> _profiles = new();
    private readonly Dictionary<string, HMProfile> _profilesById = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Create a new SDK context. Loading profiles and creating
    /// controllers are separate steps; this constructor only allocates the
    /// in-process state.</summary>
    public HMContext()
    {
    }

    // ════════════════════════════════════════════════════════════════════
    //  Driver lifecycle
    // ════════════════════════════════════════════════════════════════════

    /// <summary>True if the HIDMaestro driver is registered in the Windows
    /// driver store. Does not require admin to check.</summary>
    public bool IsDriverInstalled
    {
        get
        {
            ThrowIfDisposed();
            return DriverBuilder.IsDriverInstalled();
        }
    }

    /// <summary>Extract the embedded driver files to %TEMP%, install the
    /// self-signed code-signing certificate to the trusted root and trusted
    /// publisher stores, sign the driver binaries, and register them with
    /// Windows via pnputil. Requires admin. Idempotent and silent — no
    /// user prompts beyond the elevation that brought the calling process
    /// here. The temp extraction is deleted on success.
    ///
    /// <para>If the driver is already installed at the same build version,
    /// this returns immediately without doing any work.</para>
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Thrown if the calling
    /// process is not elevated.</exception>
    /// <exception cref="InvalidOperationException">Thrown if any step of the
    /// install fails (cert install, signing, pnputil).</exception>
    public void InstallDriver()
    {
        ThrowIfDisposed();
        // TODO: implement once driver bytes are embedded as resources
        throw new NotImplementedException("InstallDriver — pending resource embedding");
    }

    // ════════════════════════════════════════════════════════════════════
    //  Profile catalog
    // ════════════════════════════════════════════════════════════════════

    /// <summary>All profiles currently loaded in this context, in stable
    /// order by ID. Empty until you call one of the <c>LoadProfiles*</c>
    /// methods.</summary>
    public IReadOnlyList<HMProfile> AllProfiles
    {
        get { ThrowIfDisposed(); lock (_lock) return _profiles.ToArray(); }
    }

    /// <summary>Look up a profile by its stable ID slug. Returns null if
    /// no profile with that ID is loaded.</summary>
    public HMProfile? GetProfile(string id)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        ThrowIfDisposed();
        lock (_lock)
            return _profilesById.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>Load profiles from a directory containing .json files
    /// matching the HIDMaestro profile schema. Profiles loaded from
    /// multiple sources are merged by ID; later loads override earlier
    /// ones. Schema files (schema.json) are ignored.</summary>
    public int LoadProfilesFromDirectory(string profilesDir)
    {
        if (profilesDir == null) throw new ArgumentNullException(nameof(profilesDir));
        ThrowIfDisposed();
        if (!Directory.Exists(profilesDir))
            throw new DirectoryNotFoundException($"Profiles directory not found: {profilesDir}");

        var db = ProfileDatabase.Load(profilesDir);
        int added = 0;
        lock (_lock)
        {
            foreach (var inner in db.All)
            {
                if (_profilesById.ContainsKey(inner.Id)) continue; // skip dupes
                var pub = new HMProfile(inner);
                _profiles.Add(pub);
                _profilesById[inner.Id] = pub;
                added++;
            }
            _profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        }
        return added;
    }

    /// <summary>Load the default profile catalog embedded in the SDK
    /// assembly. The catalog ships with every supported controller — Xbox
    /// 360, Xbox One/Series, DualShock 4, DualSense, Stadia, common
    /// third-party gamepads — so consumers don't need to ship profile JSONs
    /// alongside their app.</summary>
    public int LoadDefaultProfiles()
    {
        ThrowIfDisposed();
        // TODO: implement once profile JSONs are embedded as resources
        throw new NotImplementedException("LoadDefaultProfiles — pending profile embedding");
    }

    // ════════════════════════════════════════════════════════════════════
    //  Controller lifecycle
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Create a new virtual controller using the given profile.
    /// Allocates the next free controller index (0..3), creates the device
    /// node via SetupAPI, sets up per-controller shared memory sections for
    /// input and output, and waits for any XInput slot claim before
    /// returning. Requires admin.
    ///
    /// <para>Returns a live <see cref="HMController"/> ready for input via
    /// <see cref="HMController.SubmitState"/>. Dispose the returned
    /// controller to remove the device, or dispose the entire context to
    /// remove all controllers it owns.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile has no descriptor and isn't deployable.</exception>
    /// <exception cref="UnauthorizedAccessException">The calling process is not elevated.</exception>
    /// <exception cref="InvalidOperationException">All four controller slots are in use.</exception>
    public HMController CreateController(HMProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor and cannot be deployed.", nameof(profile));
        ThrowIfDisposed();
        // TODO: implement once orchestration is extracted
        throw new NotImplementedException("CreateController — pending orchestration extraction");
    }

    /// <summary>All currently-live controllers owned by this context.</summary>
    public IReadOnlyCollection<HMController> ActiveControllers
    {
        get { ThrowIfDisposed(); lock (_lock) return _controllers.Values.ToArray(); }
    }

    // Called by HMController.Dispose; the context tears down its half of the state.
    internal void OnControllerDisposing(HMController controller)
    {
        lock (_lock) _controllers.Remove(controller.Index);
        // TODO: free shared memory + remove device node once orchestration is extracted
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMContext));
    }

    /// <summary>Disposes every controller this context owns and frees its
    /// resources. Safe to call multiple times.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        HMController[] toDispose;
        lock (_lock)
        {
            toDispose = _controllers.Values.ToArray();
            _controllers.Clear();
        }
        foreach (var c in toDispose)
        {
            try { c.Dispose(); } catch { /* swallow during shutdown */ }
        }
    }
}
