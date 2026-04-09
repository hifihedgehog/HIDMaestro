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
        // The DriverBuilder currently sources the driver binaries from the
        // repo's build/ directory rather than embedded resources. Resource
        // embedding (so the SDK is single-file deployable) is a planned
        // follow-up — this still works correctly when the SDK is consumed
        // from a checkout of the HIDMaestro repo, which is the intended
        // initial use case (PadForge integration).
        if (DriverBuilder.NeedsBuild() || !DriverBuilder.IsDriverInstalled())
        {
            if (!DriverBuilder.FullDeploy(rebuild: DriverBuilder.NeedsBuild()))
                throw new InvalidOperationException(
                    "Driver build/install failed. Run elevated and check the build log " +
                    "in the repo's build/ directory.");
        }
    }

    /// <summary>Removes ALL HIDMaestro virtual devices on the system, including
    /// orphans from previous runs that weren't cleanly disposed. Use this from
    /// a "cleanup" command-line command. Static (no HMContext instance needed).
    /// Requires admin.</summary>
    public static void RemoveAllVirtualControllers()
    {
        Internal.DeviceOrchestrator.RemoveAllVirtualControllers();
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
        var db = ProfileDatabase.LoadEmbedded();
        int added = 0;
        lock (_lock)
        {
            foreach (var inner in db.All)
            {
                if (_profilesById.ContainsKey(inner.Id)) continue;
                var pub = new HMProfile(inner);
                _profiles.Add(pub);
                _profilesById[inner.Id] = pub;
                added++;
            }
            _profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        }
        return added;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Controller lifecycle
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Create a new virtual controller using the given profile.
    /// Allocates the next free controller index, creates the device node
    /// via SetupAPI, sets up per-controller shared memory sections for
    /// input and output, and waits for any XInput slot claim before
    /// returning. Requires admin.
    ///
    /// <para>Returns a live <see cref="HMController"/> ready for input via
    /// <see cref="HMController.SubmitState"/>. Dispose the returned
    /// controller to remove the device, or dispose the entire context to
    /// remove all controllers it owns.</para>
    ///
    /// <para>All three profile paths are supported: plain HID (DualSense,
    /// generic gamepads), xinputhid companion-only (Xbox Series BT, Xbox
    /// One), and non-xinputhid Xbox with XUSB companion (Xbox 360 Wired).
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile has no descriptor and isn't deployable.</exception>
    /// <exception cref="InvalidOperationException">Driver install failed or
    /// device node creation failed.</exception>
    public HMController CreateController(HMProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor and cannot be deployed.", nameof(profile));
        ThrowIfDisposed();

        // Allocate the next free controller index. Linear scan from 0 — there
        // is no upper bound (the unlimited-controllers fix in d0ce7fe removed
        // every i<4 cap on our side). XInput's 4-slot limit only constrains
        // Xbox-family profiles which aren't supported here yet.
        int index;
        lock (_lock)
        {
            index = 0;
            while (_controllers.ContainsKey(index)) index++;
        }

        // The driver INF lives next to the driver binaries in the repo's
        // build/ directory. This will move to embedded-resource extraction
        // when single-file SDK deployment is implemented.
        string infPath = System.IO.Path.Combine(
            Internal.DriverBuilder.BuildDir, "hidmaestro.inf");

        string? instanceId;
        try
        {
            instanceId = Internal.DeviceOrchestrator.SetupController(
                index, profile.Inner, infPath);
        }
        catch
        {
            // Best-effort cleanup of any partial state, then rethrow.
            try { Internal.DeviceOrchestrator.TeardownController(index, null); } catch { }
            throw;
        }

        var controller = new HMController(this, index, profile, instanceId);
        lock (_lock) _controllers[index] = controller;
        return controller;
    }

    /// <summary>Create a controller pinned to a specific index. Used by live
    /// profile-switching workflows where the consumer wants to dispose the
    /// existing controller at index N and replace it with one running a
    /// different profile while keeping the same N. The index must be free
    /// (the previous controller at that index must already be disposed).</summary>
    /// <exception cref="InvalidOperationException">If the index is already
    /// in use by another live controller.</exception>
    public HMController CreateControllerAt(int index, HMProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor and cannot be deployed.", nameof(profile));
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_controllers.ContainsKey(index))
                throw new InvalidOperationException(
                    $"Controller index {index} is already in use. Dispose the existing controller first.");
        }

        string infPath = System.IO.Path.Combine(
            Internal.DriverBuilder.BuildDir, "hidmaestro.inf");

        string? instanceId;
        try
        {
            instanceId = Internal.DeviceOrchestrator.SetupController(
                index, profile.Inner, infPath);
        }
        catch
        {
            try { Internal.DeviceOrchestrator.TeardownController(index, null); } catch { }
            throw;
        }

        var controller = new HMController(this, index, profile, instanceId);
        lock (_lock) _controllers[index] = controller;
        return controller;
    }

    /// <summary>All currently-live controllers owned by this context.</summary>
    public IReadOnlyCollection<HMController> ActiveControllers
    {
        get { ThrowIfDisposed(); lock (_lock) return _controllers.Values.ToArray(); }
    }

    /// <summary>Re-apply friendly names to every live controller. Call once
    /// after creating ALL controllers — there is a Windows PnP race where the
    /// first controller's friendly name gets overwritten by the SECOND
    /// controller's driver-bind activity. Re-applying after all PnP has
    /// settled makes the writes stick. The proven pre-SDK test app called
    /// this as "Phase 1.5 — Finalizing device names". A 2-second sleep is
    /// included to let PnP settle before the re-apply pass.</summary>
    public void FinalizeNames()
    {
        ThrowIfDisposed();
        System.Threading.Thread.Sleep(2000);
        HMController[] all;
        lock (_lock) all = _controllers.Values.ToArray();
        foreach (var c in all)
        {
            string name = c.Profile.Inner.DeviceDescription
                          ?? c.Profile.Inner.ProductString
                          ?? "Controller";
            try { Internal.DeviceProperties.ApplyFriendlyNameForController(c.Index, name); }
            catch { /* per-controller failure shouldn't break the whole pass */ }
        }
    }

    // Called by HMController.Dispose; the context tears down its half of the state.
    internal void OnControllerDisposing(HMController controller)
    {
        lock (_lock) _controllers.Remove(controller.Index);
        Internal.DeviceOrchestrator.TeardownController(controller.Index, controller.InstanceId);
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
