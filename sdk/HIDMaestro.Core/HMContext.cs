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
/// owns. Each context numbers its controllers in slots of its own, and
/// <see cref="CreateControllerAt(int, HMProfile)"/> names a slot. Every
/// controller also holds a machine-wide transport index
/// (<see cref="HMController.TransportIndex"/>), so several contexts in one
/// process, and several programs that embed HIDMaestro on one machine, run
/// side by side without sharing a section, an identity or a device.</para>
///
/// <para><b>Driver install:</b> the UMDF2 driver and its XUSB companion are
/// embedded as resources inside this assembly. On first run on a given
/// machine, call <see cref="InstallDriver"/> to stage them under
/// <c>%ProgramData%\HIDMaestro\Staging</c>, in directories only
/// Administrators and SYSTEM can write, and register them with Windows via
/// pnputil. This requires admin and only needs to happen once per machine:
/// subsequent runs detect that the driver is already in the DriverStore and
/// skip the install. Nothing is written to the consuming app's
/// directory.</para>
///
/// <para><b>Admin requirement:</b> Windows requires SeLoadDriverPrivilege
/// (admin) for both <see cref="InstallDriver"/> and <see cref="CreateController"/>.
/// This matches every other virtual-controller library on Windows
/// (ViGEmBus, vJoy, etc.) and is fundamental: there is no API path that
/// lets a standard user create a HIDClass device.</para>
/// </summary>
public sealed class HMContext : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, HMController> _controllers = new();
    // Slots a create in flight has taken, so two threads creating at once
    // never pick the same one.
    private readonly HashSet<int> _pendingSlots = new();
    private readonly List<HMProfile> _profiles = new();
    private readonly Dictionary<string, HMProfile> _profilesById = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Create a new SDK context. Loading profiles and creating
    /// controllers are separate steps; this constructor only allocates the
    /// in-process state.
    ///
    /// <para>v1.3.0: fires a background warm-up that pre-computes the
    /// manifest hash and, when a driver deploy is coming, pre-stages the
    /// embedded driver payload. Both are otherwise done synchronously by the
    /// first <see cref="InstallDriver"/> call. Doing them in parallel with
    /// whatever the consumer is doing on the foreground thread (UI init,
    /// profile lookups, settings UI) hides 200–500 ms of cold-start cost
    /// in the consumer's think-time budget. Failures are silently
    /// swallowed: if the warm-up couldn't stage for some reason, the
    /// foreground InstallDriver path will retry it.</para></summary>
    public HMContext()
    {
        // T28: fan out independent prewarm tasks in parallel. The original
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
            // Only a deploy reads the staged payload. On a machine already
            // running this build's driver, InstallDriver takes the fast path
            // and never touches it, so the prewarm stages (and hashes) the
            // payload only when a deploy is coming. Staging needs elevation;
            // a non-elevated context skips it here and fails nothing.
            try
            {
                if (HMBuildInfo.Carries(HMPayload.Driver, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture) &&
                    !Internal.DriverBuilder.WillTakeFastPath())
                    Internal.DriverBuilder.EnsureExtracted();
            }
            catch { }
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

    /// <summary>Issue #39: true when the USB transport that composite
    /// personas ride (usbip-win2's virtual host controller) is already
    /// installed on this machine.
    ///
    /// <para>This is NOT a gate on creating composite controllers.
    /// The transport is bundled inside HIDMaestro.Core.dll and installs
    /// itself on the first composite create, the same way the UMDF2
    /// driver does, so <see cref="CreateController"/> works either way.
    /// Read this only when you want to tell the user that the first
    /// composite controller will trigger a one-time driver install
    /// (a few seconds, and USB devices blink once as Windows
    /// re-enumerates the root hubs).</para>
    ///
    /// <para>Pure presence probe: requires no admin and installs
    /// nothing. False in a build that carries no USB/IP transport for this
    /// machine (<see cref="HMBuildInfo.UsbipArchitectures"/>), which creates
    /// no composite controller whatever the machine has installed.</para></summary>
    public static bool IsUsbipBackendAvailable =>
        HMBuildInfo.Carries(HMPayload.UsbipTransport, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture) &&
        Internal.Usbip.UsbipBackend.IsAvailable;

    /// <summary>Issue #39: install the bundled USB transport now instead
    /// of on the first composite create. Optional, and only worth calling
    /// when a consumer wants the one-time install to happen at a moment
    /// it controls (an onboarding step, a settings toggle) rather than
    /// mid-session. Idempotent, requires elevation, and reports progress
    /// through <paramref name="progress"/>.</summary>
    /// <exception cref="HMPayloadMissingException">This build carries no
    /// USB/IP transport for this machine's architecture.</exception>
    public static void InstallUsbipBackend(Action<string>? progress = null)
        => Internal.Usbip.UsbipDriverInstaller.EnsureInstalled(progress);

    /// <summary>Stage the embedded driver files under
    /// <c>%ProgramData%\HIDMaestro\Staging</c>, verifying each against its
    /// embedded SHA-256, install the self-signed code-signing certificate to
    /// the trusted root and trusted publisher stores, sign the driver
    /// binaries, and register them with Windows via pnputil. Requires admin.
    /// Idempotent and silent: no user prompts beyond the elevation that
    /// brought the calling process here. A deploy also removes the staging of
    /// SDK versions no running program uses.
    ///
    /// <para>If the driver is already installed at the same build version,
    /// this returns immediately without doing any work.</para>
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Thrown if the calling
    /// process is not elevated.</exception>
    /// <exception cref="InvalidOperationException">Thrown if any step of the
    /// install fails (cert install, signing, pnputil).</exception>
    /// <exception cref="HMPayloadMissingException">This build carries no
    /// driver for this machine's architecture.</exception>
    public void InstallDriver()
    {
        ThrowIfDisposed();
        HMBuildInfo.ThrowIfMissing(HMPayload.Driver);

        // Devices of programs that have exited go first: a device bound to
        // the old INF keeps pnputil from replacing the package, and a
        // driver upgrade below needs nothing left on it. Only provably dead
        // owners' devices are removed. A running program's controllers,
        // this one's included, stay, and the deploy below keeps the
        // installed driver for them.
        Internal.OwnerSweep.ReclaimDeadOwners();

        LastDriverInstall = Internal.DriverDeploy.Run();
    }

    /// <summary>What the last <see cref="InstallDriver"/> call on this
    /// context did, or null before the first one.</summary>
    public HMDriverInstallOutcome? LastDriverInstall { get; private set; }

    /// <summary>Removes the virtual controllers of programs that have
    /// exited without removing theirs, then the driver packages and
    /// <c>HKLM\SOFTWARE\HIDMaestro</c> once no controller of any program
    /// remains on the driver. A running program's controllers, this one's
    /// included, are never touched: dispose a controller to remove it.
    /// Static (no HMContext instance needed). Requires admin.
    ///
    /// <para>Before 1.13.0 this removed every HIDMaestro controller on the
    /// machine. That is now
    /// <see cref="RemoveAllVirtualControllers(HMCleanupScope, bool)"/> with
    /// <see cref="HMCleanupScope.Everything"/>. A program that exits without
    /// disposing its controllers has their devices removed by the SDK as it
    /// exits, and a program that is killed has them removed by the next
    /// sweep of any program on the machine.</para></summary>
    public static void RemoveAllVirtualControllers()
        => RemoveAllVirtualControllers(HMCleanupScope.Orphaned, preserveInstall: false);

    /// <summary>The same removal as <see cref="RemoveAllVirtualControllers()"/>.
    /// With <paramref name="preserveInstall"/> true the driver packages and
    /// <c>HKLM\SOFTWARE\HIDMaestro</c>, which holds the manifest hash behind
    /// <see cref="InstallDriver"/>'s same-version fast path, are kept. Use
    /// that from a program's own launch or exit sweep: removing them forces
    /// the next <see cref="InstallDriver"/> through the multi-second full
    /// deploy for nothing when the version has not changed.</summary>
    public static void RemoveAllVirtualControllers(bool preserveInstall)
        => RemoveAllVirtualControllers(HMCleanupScope.Orphaned, preserveInstall);

    /// <summary>Remove the virtual controllers <paramref name="scope"/>
    /// names. <see cref="HMCleanupScope.Everything"/> removes every
    /// HIDMaestro controller and USB/IP import on the machine, whoever made
    /// it and whether or not that program still runs, which is what an
    /// uninstall, a test harness or a support tool wants. With
    /// <paramref name="preserveInstall"/> false it also removes the driver
    /// packages and <c>HKLM\SOFTWARE\HIDMaestro</c>.</summary>
    public static void RemoveAllVirtualControllers(HMCleanupScope scope, bool preserveInstall)
    {
        if (scope == HMCleanupScope.Everything)
            Internal.DeviceOrchestrator.RemoveEverything(preserveInstall);
        else
            Internal.OwnerSweep.RemoveOrphaned(preserveInstall);
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
    /// assembly. The catalog ships with every supported controller: Xbox
    /// 360, Xbox One/Series, DualShock 4, DualSense, Stadia, common
    /// third-party gamepads: so consumers don't need to ship profile JSONs
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
    public HMController CreateController(HMProfile profile) => CreateController(profile, null);

    /// <summary>Create a new virtual controller with a durable identity
    /// (issue #60). <paramref name="identityKey"/> names the controller
    /// the way the consumer thinks of it, such as a slot number plus a
    /// profile id. Every device path, the container id and, for USB/IP
    /// personas, the USB serial derive from the key, so a controller
    /// created again with the same key comes back at the same paths
    /// after a dispose, a process restart, a reboot or a driver upgrade.
    /// Programs that store a binding against a device path or a serial
    /// keep it across those lives.
    ///
    /// <para>A null or empty key uses the controller's transport index (see
    /// <see cref="HMController.TransportIndex"/>), which is what
    /// <see cref="CreateController(HMProfile)"/> does. That index is the
    /// slot number when no other program on the machine uses it, so a
    /// keyless controller's index and default identity can differ between
    /// runs when other programs that embed HIDMaestro are running. Pass a
    /// key for paths that must stay put. The form <c>index:&lt;N&gt;</c> is
    /// reserved for the default: it names the identity of index N wherever
    /// it is passed. One identity is held by one live controller on the
    /// whole machine, in whichever program created it: dispose the first
    /// before creating the second. A different profile at the same key
    /// keeps the identity and refreshes the descriptor.</para></summary>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile has no descriptor and isn't deployable.</exception>
    /// <exception cref="HMResourceInUseException">A live controller on this
    /// machine, in this program or another, holds the key.</exception>
    /// <exception cref="InvalidOperationException">Driver install failed or
    /// device node creation failed.</exception>
    public HMController CreateController(HMProfile profile, string? identityKey)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor and cannot be deployed.", nameof(profile));
        ThrowIfDisposed();

        // The lowest slot free in this context. Linear scan from 0, no
        // upper bound on the SDK side. XInput's 4-slot limit only constrains
        // Xbox-family profiles (xbox-360-wired, xbox-series-xs-bt, etc.);
        // non-XInput profiles can run beyond 4 simultaneously.
        int slot;
        lock (_lock)
        {
            slot = 0;
            while (_controllers.ContainsKey(slot) || _pendingSlots.Contains(slot)) slot++;
            _pendingSlots.Add(slot);
        }
        try { return CreateAtSlot(slot, profile, identityKey); }
        finally { lock (_lock) _pendingSlots.Remove(slot); }
    }

    /// <summary>The machine-wide half of a create: the lease on a
    /// transport index and an identity, taken before any PnP work, then the
    /// backend's create on them.</summary>
    private HMController CreateAtSlot(int slot, HMProfile profile, string? identityKey)
    {
        // A payload this build lacks refuses the create before the lease,
        // so the refusal touches nothing machine-wide.
        if (profile.RequiresUsbipBackend)
            HMBuildInfo.ThrowIfMissing(HMPayload.UsbipTransport,
                $"Profile '{profile.Id}' is a composite persona, which rides the USB/IP transport.");
        else
            HMBuildInfo.ThrowIfMissing(HMPayload.Driver,
                $"Profile '{profile.Id}' runs on the UMDF2 driver.");

        var lease = Internal.ControllerLease.Acquire(slot, identityKey,
            profile.RequiresUsbipBackend ? "usbip" : "umdf", profile.Id);
        try
        {
            // Issue #39: composite USB personas run on the USB/IP backend,
            // never on UMDF2, which can only present the single HID
            // interface and would silently drop the three USB Audio Class
            // interfaces the profile promises. The backend's transport is
            // bundled and deploys itself on first use, so this path needs no
            // precondition from the consumer.
            if (profile.RequiresUsbipBackend)
                return CreateUsbipController(slot, lease, profile);

            string? instanceId;
            try
            {
                instanceId = Internal.DeviceOrchestrator.SetupController(
                    lease.TransportIndex, profile.Inner, lease.Identity);
            }
            catch
            {
                // Best-effort cleanup of any partial state, then rethrow.
                try { Internal.DeviceOrchestrator.TeardownController(lease.TransportIndex, null); } catch { }
                throw;
            }
            if (instanceId != null)
                Internal.MachineClaims.SetControllerValue(lease.Identity.Token, "InstanceId", instanceId);

            var controller = new HMController(this, slot, lease, profile, instanceId, identityKey: lease.Identity.Key);
            lock (_lock) _controllers[slot] = controller;
            return controller;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    // Issue #39: the USB/IP create path. The backend's device emulator
    // pre-creates the per-index shared sections and events, attaches
    // through usbip-win2's vhci, and the HMController then binds to the
    // same sections it always does. No PnP, no INF, no driver install.
    private HMController CreateUsbipController(int slot, Internal.ControllerLease lease, HMProfile profile)
    {
        int index = lease.TransportIndex;
        Internal.Usbip.UsbipBackendHandle handle =
            Internal.Usbip.UsbipBackend.CreateDevice(profile.Inner, index, lease.Identity);
        try
        {
            Internal.MachineClaims.SetControllerValue(lease.Identity.Token, "VhciPort", handle.VhciPort);
            var controller = new HMController(this, slot, lease, profile, instanceId: null, handle, lease.Identity.Key);
            lock (_lock) _controllers[slot] = controller;
            return controller;
        }
        catch
        {
            try { handle.Dispose(); } catch { }
            try { Internal.SharedMemoryIO.DestroyController(index); } catch { }
            throw;
        }
    }

    /// <summary>Create a controller pinned to a specific slot of this
    /// context. Used by live profile-switching workflows where the consumer
    /// wants to dispose the existing controller at slot N and replace it with
    /// one running a different profile while keeping the same N. The slot
    /// must be free in this context (the previous controller at that slot
    /// must already be disposed). The controller's transport index is N when
    /// no other program on the machine uses N, and otherwise the lowest free
    /// index (see <see cref="HMController.TransportIndex"/>).</summary>
    /// <exception cref="InvalidOperationException">If the slot is already
    /// in use by another live controller of this context.</exception>
    public HMController CreateControllerAt(int index, HMProfile profile) => CreateControllerAt(index, profile, null);

    /// <summary>Create a controller pinned to a specific slot with a
    /// durable identity key. See <see cref="CreateController(HMProfile, string)"/>
    /// for what the key does. A null or empty key uses the transport index.</summary>
    /// <exception cref="InvalidOperationException">If the slot is already
    /// in use by another live controller of this context.</exception>
    /// <exception cref="HMResourceInUseException">A live controller on this
    /// machine, in this program or another, holds the key.</exception>
    public HMController CreateControllerAt(int index, HMProfile profile, string? identityKey)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new ArgumentException($"Profile '{profile.Id}' has no HID descriptor and cannot be deployed.", nameof(profile));
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_controllers.ContainsKey(index) || _pendingSlots.Contains(index))
                throw new InvalidOperationException(
                    $"Controller index {index} is already in use. Dispose the existing controller first.");
            _pendingSlots.Add(index);
        }
        try { return CreateAtSlot(index, profile, identityKey); }
        finally { lock (_lock) _pendingSlots.Remove(index); }
    }

    /// <summary>All currently-live controllers owned by this context.</summary>
    public IReadOnlyCollection<HMController> ActiveControllers
    {
        get { ThrowIfDisposed(); lock (_lock) return _controllers.Values.ToArray(); }
    }

    /// <summary>Re-apply friendly names to every live controller. Call once
    /// after creating ALL controllers: there is a Windows PnP race where the
    /// first controller's friendly name gets overwritten by the SECOND
    /// controller's driver-bind activity. Re-applying after all PnP has
    /// settled makes the writes stick. The proven pre-SDK test app called
    /// this as "Phase 1.5: Finalizing device names".
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
        // machines and was fragile on slow ones. T24-2: tightened poll
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
    /// orphan sweep: the sweep runs ONCE after the batch instead of N
    /// times concurrently.</summary>
    private bool _batchDisposing;

    internal void OnControllerDisposing(HMController controller)
    {
        lock (_lock) _controllers.Remove(controller.Slot);

        try
        {
            // Issue #39: usbip-backend controllers have no PnP devnode and no
            // driver binding. Teardown is detach-from-vhci plus the shared
            // sections; running the UMDF2 PnP teardown would sweep for devices
            // that never existed.
            if (controller.UsbipHandle != null)
            {
                try { controller.UsbipHandle.Dispose(); } catch { }
                try { Internal.SharedMemoryIO.DestroyController(controller.Index); } catch { }
                return;
            }

            Internal.DeviceOrchestrator.TeardownController(
                controller.Index, controller.InstanceId, skipOrphanSweep: _batchDisposing);
        }
        finally
        {
            // The index and identity go back to the machine only once the
            // devices they name are gone.
            controller.Lease?.Dispose();
        }
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
    /// independent: different devnodes, different ContainerIDs, different
    /// kernel locks: so we run all dispose calls in parallel and the
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
