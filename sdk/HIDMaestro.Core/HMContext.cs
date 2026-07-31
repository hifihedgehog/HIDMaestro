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
/// lets a standard user create a HIDClass device.</para>
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
    /// in-process state.
    ///
    /// <para>v1.3.0 — fires a background warm-up that pre-extracts the
    /// embedded driver payload to %TEMP% and pre-computes the manifest
    /// hash. Both are normally done synchronously by the first
    /// <see cref="InstallDriver"/> call; doing them in parallel with
    /// whatever the consumer is doing on the foreground thread (UI init,
    /// profile lookups, settings UI) hides 200–500 ms of cold-start cost
    /// in the consumer's think-time budget. Failures are silently
    /// swallowed — if the warm-up couldn't extract for some reason, the
    /// foreground InstallDriver path will retry it.</para></summary>
    public HMContext()
    {
        // T28 — fan out independent prewarm tasks in parallel. The original
        // serial sequence was: Sha256Hex → EnsureExtracted → LoadEmbedded →
        // IsDriverInstalled → PrewarmGameInputService, taking ~150-250 ms
        // total on a cold launch. Splitting into independent threads lets
        // CPU-bound (Sha256, JSON parse) overlap with I/O-bound (filesystem
        // staging dir creation, sc.exe spawn). Saves 50-100 ms in worst
        // case where the consumer's foreground InstallDriver fires before
        // prewarm completes. Catches per-task so one failure doesn't kill
        // the rest.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { _ = Internal.EmbeddedManifest.Sha256Hex; } catch { }
            try { Internal.DriverBuilder.EnsureExtracted(); } catch { }
            try { Internal.DriverBuilder.IsDriverInstalled(); } catch { }
        });
        System.Threading.Tasks.Task.Run(() =>
        {
            try { _ = Internal.ProfileDatabase.LoadEmbedded(); } catch { }
        });
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Internal.DeviceOrchestrator.PrewarmGameInputService(); } catch { }
        });
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

        // Proactive ghost sweep FIRST, before FullDeploy. Without this, when a
        // prior process crashed or was force-killed (Dispose never ran), its
        // virtual controllers + HIDMAESTRO stay PnP-live and REMAIN BOUND to
        // the old INF. On the next launch, DriverBuilder.FullDeploy calls
        // pnputil /delete-driver /uninstall /force — which fails with "One or
        // more devices are presently installed using the specified INF" and
        // leaves the old INF + stale DLL bytes in DriverStore. The subsequent
        // /add-driver then sees package-already-present + "Needed repairing"
        // and RESTORES the stale bytes from pnputil's internal cache rather
        // than installing the fresh extracted binary. Net effect: every
        // launch since the first one serves the stale driver forever, the
        // v1.1.5 self-heal code never actually loads, input keeps hanging,
        // and the only escape is manual devcon + TrustedInstaller takeown of
        // the FileRepository subdirectory (which users do not have).
        //
        // Running the sweep here FIRST removes the bound devices via devcon
        // (returning "Removed on reboot" is sufficient, as the INF becomes
        // eligible for package deletion immediately), so FullDeploy's
        // /delete-driver call actually succeeds and the fresh extracted
        // binary replaces the DriverStore contents.
        //
        // preserveInstall: evict devices/orphans only. The 2026-07-21 perf
        // audit found the full sweep also deleting the installed packages
        // and the manifest hash, which forced the ~3 s full deploy pipeline
        // on EVERY launch; the same-version fast path never got to run.
        // Package replacement still happens inside FullDeploy's full path
        // when the embedded payload differs.
        Internal.DeviceOrchestrator.RemoveAllVirtualControllers(preserveInstall: true);

        if (!DriverBuilder.FullDeploy())
            throw new InvalidOperationException(
                "Driver install failed. Run elevated and check pnputil output.");
    }

    /// <summary>Removes ALL HIDMaestro virtual devices on the system, including
    /// orphans from previous runs that weren't cleanly disposed. Use this from
    /// a "cleanup" command-line command. Static (no HMContext instance needed).
    /// Requires admin.</summary>
    public static void RemoveAllVirtualControllers()
    {
        Internal.DeviceOrchestrator.RemoveAllVirtualControllers();
    }

    /// <summary>Same device/orphan eviction as
    /// <see cref="RemoveAllVirtualControllers()"/>, but with
    /// <paramref name="preserveInstall"/> = true the installed driver
    /// packages and the SOFTWARE\HIDMaestro configuration (including the
    /// manifest hash that powers <see cref="InstallDriver"/>'s same-version
    /// fast path) are kept. Use this from a consumer's own launch or exit
    /// sweep: a full nuke there forces the next <see cref="InstallDriver"/>
    /// through the multi-second full deploy pipeline for no benefit when
    /// the driver version hasn't changed. Pass false (or call the
    /// parameterless overload) for a true uninstall-grade cleanup.</summary>
    public static void RemoveAllVirtualControllers(bool preserveInstall)
    {
        Internal.DeviceOrchestrator.RemoveAllVirtualControllers(preserveInstall);
    }

    /// <summary>Disposes a set of controllers concurrently, suppressing the
    /// per-controller HID orphan sweep and running it once at the end. The
    /// per-controller wall-clock for each Dispose() call is reported through
    /// <paramref name="perControllerCallback"/> so callers can log a
    /// "disposed slot N in M ms" line for each. Use from any caller that
    /// already has a list of HMControllers to dispose together (e.g., a
    /// test harness's end-of-run cleanup); HMContext.Dispose itself uses an
    /// equivalent path internally.</summary>
    public void DisposeControllersInParallel(
        IEnumerable<HMController> controllers,
        Action<HMController, long>? perControllerCallback = null)
    {
        if (controllers == null) throw new ArgumentNullException(nameof(controllers));
        var arr = controllers.Where(c => c != null).ToArray();
        if (arr.Length == 0) return;
        if (arr.Length == 1)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { arr[0].Dispose(); } catch { }
            perControllerCallback?.Invoke(arr[0], sw.ElapsedMilliseconds);
            return;
        }
        _batchDisposing = true;
        try
        {
            System.Threading.Tasks.Parallel.ForEach(arr, c =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { c.Dispose(); } catch { }
                perControllerCallback?.Invoke(c, sw.ElapsedMilliseconds);
            });
        }
        finally { _batchDisposing = false; }
        Internal.DeviceOrchestrator.RemoveOrphanHidChildrenBatch();
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
        // Issue #39: a composite USB persona needs the opt-in USB/IP
        // backend. Instantiating it here would create the single HID
        // interface UMDF2 can make and silently drop the three USB Audio
        // Class interfaces the profile promises, which is the fidelity
        // gap the profile exists to close. Fail loudly instead.
        if (profile.RequiresUsbipBackend)
            throw new NotSupportedException(
                $"Profile '{profile.Id}' declares backend '{profile.Backend}' and needs the USB/IP backend, " +
                "which is opt-in and not installed. Use the UMDF2 profile for this device, or install the backend.");
        ThrowIfDisposed();

        // Allocate the next free controller index. Linear scan from 0 — no
        // upper bound on the SDK side. XInput's 4-slot limit only constrains
        // Xbox-family profiles (xbox-360-wired, xbox-series-xs-bt, etc.);
        // non-XInput profiles can run beyond 4 simultaneously.
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
        // Issue #39: a composite USB persona needs the opt-in USB/IP
        // backend. Instantiating it here would create the single HID
        // interface UMDF2 can make and silently drop the three USB Audio
        // Class interfaces the profile promises, which is the fidelity
        // gap the profile exists to close. Fail loudly instead.
        if (profile.RequiresUsbipBackend)
            throw new NotSupportedException(
                $"Profile '{profile.Id}' declares backend '{profile.Backend}' and needs the USB/IP backend, " +
                "which is opt-in and not installed. Use the UMDF2 profile for this device, or install the backend.");
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
    /// this as "Phase 1.5 — Finalizing device names".
    ///
    /// Instead of a fixed 2-second sleep, polls for every controller's HID
    /// child to reach DN_STARTED (driver fully bound) before re-applying.
    /// On fast machines this exits in &lt;100ms; on slow machines it adapts
    /// up to 5 seconds rather than failing from an insufficient fixed sleep.
    /// </summary>
    public void FinalizeNames()
    {
        ThrowIfDisposed();

        HMController[] all;
        lock (_lock) all = _controllers.Values.ToArray();

        // Wait until every controller's HID child is in DN_STARTED state,
        // which means PnP is done binding drivers on that device tree.
        // Replaces a fixed Thread.Sleep(2000) that wasted time on fast
        // machines and was fragile on slow ones. T24-2 — tightened poll
        // cadence (100 ms → 25 ms). On fast-machine cases where DN_STARTED
        // is already true on entry, this exits in ≤25 ms instead of waiting
        // up to 100 ms for the next poll. CM_Get_DevNode_Status is sub-µs
        // so the per-poll cost is negligible. Budget remains 5000 ms (scaled).
        int budget = Internal.TimeoutScale.Apply(5000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budget)
        {
            bool allStarted = true;
            foreach (var c in all)
            {
                if (c.InstanceId == null) continue;
                if (!Internal.DeviceManager.IsDeviceStarted(c.InstanceId))
                    { allStarted = false; break; }
            }
            if (allStarted) break;
            System.Threading.Thread.Sleep(25);
        }

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
    /// <summary>Set during HMContext.Dispose's parallel batch teardown so
    /// per-controller TeardownController calls skip the system-wide HID
    /// orphan sweep — the sweep runs ONCE after the batch instead of N
    /// times concurrently.</summary>
    private bool _batchDisposing;

    internal void OnControllerDisposing(HMController controller)
    {
        lock (_lock) _controllers.Remove(controller.Index);
        Internal.DeviceOrchestrator.TeardownController(
            controller.Index, controller.InstanceId, skipOrphanSweep: _batchDisposing);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMContext));
    }

    /// <summary>Disposes every controller this context owns and frees its
    /// resources. Safe to call multiple times.
    ///
    /// <para>Per-controller teardown blocks 5-11s waiting on Windows PnP's
    /// synchronous DIF_REMOVE (xinputhid filter unload for BT, XUSB companion
    /// teardown for non-xinputhid Xbox). The per-controller work is fully
    /// independent — different devnodes, different ContainerIDs, different
    /// kernel locks — so we run all dispose calls in parallel and the
    /// wall-clock collapses from sum(N) to max(N). For 4 controllers
    /// that's typically 34s -> ~10s.</para>
    /// </summary>
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
        if (toDispose.Length == 0) return;
        if (toDispose.Length == 1)
        {
            try { toDispose[0].Dispose(); } catch { /* swallow during shutdown */ }
            return;
        }
        _batchDisposing = true;
        try
        {
            System.Threading.Tasks.Parallel.ForEach(toDispose, c =>
            {
                try { c.Dispose(); } catch { /* swallow during shutdown */ }
            });
        }
        finally
        {
            _batchDisposing = false;
        }
        // Run the system-wide HID orphan sweep ONCE after all per-controller
        // teardowns complete. This avoids N concurrent system-wide HID
        // enumerations during the parallel batch.
        Internal.DeviceOrchestrator.RemoveOrphanHidChildrenBatch();
    }
}
