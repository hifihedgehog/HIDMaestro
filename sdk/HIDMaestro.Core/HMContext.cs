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
/// <c>%ProgramFiles%\HIDMaestro\Staging</c>, in directories only
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
    // v2.0.0: what each loaded profile was read from, so a second file with
    // its id is refused by name, and every file a load refused.
    private readonly Dictionary<string, string> _profileSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HMProfileLoadIssue> _loadIssues = new();
    // The named models and identity defaults this context's profiles take:
    // the catalog's, and each loaded directory's models folder.
    private Internal.ModelRegistry? _registry;
    // Directory loads run one at a time: each reads the registry, loads
    // under it and writes back the registry with its own models added, and
    // two at once would keep only the later one's.
    private readonly object _directoryLoadLock = new();
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
    /// driver store. Does not require admin to check. It answers from what it
    /// can read, and a driver store it cannot read answers false: pnputil
    /// that cannot start or fails, or, in an elevated process, pnputil's
    /// protected temporary folder that cannot be made. The reason goes to the
    /// diagnostic log, and <see cref="InstallDriver"/> then reports it.</summary>
    /// <exception cref="ObjectDisposedException">The context was
    /// disposed.</exception>
    public bool IsDriverInstalled
    {
        get
        {
            ThrowIfDisposed();
            return AnswerInstalled(DriverBuilder.IsDriverInstalled);
        }
    }

    /// <summary>The getter's answer from <paramref name="check"/>: false, with
    /// the reason logged, when the check throws. Coexistence probe OF11
    /// calls it through reflection with checks that throw.</summary>
    internal static bool AnswerInstalled(Func<bool> check)
    {
        try { return check(); }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"IsDriverInstalled: answered false, the driver store could not be read: {ex.GetType().Name}: {ex.Message}");
            return false;
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
    /// through <paramref name="progress"/>, on the calling thread. The
    /// install runs on a thread of its own, so a callback that blocks holds
    /// up no other program, and an exception the callback throws is dropped
    /// and does not change the outcome.</summary>
    /// <exception cref="HMPayloadMissingException">This build carries no
    /// USB/IP transport for this machine's architecture.</exception>
    public static void InstallUsbipBackend(Action<string>? progress = null)
        => Internal.Usbip.UsbipDriverInstaller.EnsureInstalled(progress);

    /// <summary>Stage the embedded driver files under
    /// <c>%ProgramFiles%\HIDMaestro\Staging</c>, verifying each against its
    /// embedded SHA-256, install the self-signed code-signing certificate to
    /// the trusted root and trusted publisher stores, sign the driver
    /// binaries, and register them with Windows via pnputil. Requires admin.
    /// Idempotent and silent: no user prompts beyond the elevation that
    /// brought the calling process here. A deploy also removes the staging of
    /// SDK versions no running program uses.
    ///
    /// <para>If this build's driver is already installed with both its
    /// packages, this returns <see cref="HMDriverInstallOutcome.AlreadyCurrent"/>
    /// without deploying, and without elevation. In an elevated process the
    /// controllers of programs that have exited are removed first.</para>
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The calling process is
    /// not elevated, and this build's driver is not installed.</exception>
    /// <exception cref="InvalidOperationException">A step of the install
    /// failed: staging, the certificate install, signing or pnputil. Staging
    /// refuses a directory on its path that it can neither reset nor move
    /// aside, one another program created while HIDMaestro was creating it,
    /// a staged file that no longer matches its embedded SHA-256, and a TEMP
    /// directory for its child processes that it cannot protect. Also thrown
    /// when another program held the deploy lock for ten minutes or the
    /// staging lock for two.</exception>
    /// <exception cref="HMDriverVersionException">The installed driver is
    /// older than this SDK supports, and controllers of running programs
    /// keep it from being replaced.</exception>
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
    /// sweep of any program on the machine.</para>
    ///
    /// <para>Each device it removes, and each interface registration it
    /// deletes for a device that is gone, waits for the machine-wide lock
    /// that programs built on this release hold while they register or
    /// remove a device, for up to ten minutes, or once the process is
    /// exiting within the exit's 10 seconds. With nothing to remove it takes
    /// no lock.</para>
    ///
    /// <para>The records Windows keeps of removed devices, shown only with
    /// hidden devices, go with a cleanup that runs once per process on a
    /// background thread: the first of <see cref="InstallDriver"/>, the
    /// first create and this call starts it and returns without waiting for
    /// it to end, and a later call removes none of them. While it runs, a
    /// create in this process and the <see cref="HMCleanupScope.Everything"/>
    /// cleanup wait for it to end: one pass over the records, their
    /// uninstalls, at most 5 seconds of waiting in all, for the machine-wide
    /// PnP lock before each uninstall and for Windows to let the record go
    /// after it, and two passes over interface registrations that wait for
    /// the lock within those 5 seconds. The ones left go with
    /// the next process's first cleanup. When the runtime has already begun
    /// process exit, which the SDK learns from
    /// <c>AssemblyLoadContext.Default.Unloading</c>, raised before
    /// ProcessExit, that first call runs the cleanup itself before it
    /// returns, waiting at most 10 seconds for a create in flight to let it
    /// start. Under .NET the SDK learns this only when code of the program
    /// that refers to the SDK ran or was compiled before exit began: for a
    /// program that uses the SDK only inside its own ProcessExit handler,
    /// the cleanup ends with the process, and the next process's first
    /// cleanup removes what it left.</para></summary>
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

    /// <summary>Disposes a set of controllers from parallel threads,
    /// suppressing the per-controller HID orphan sweep and running it once at
    /// the end. Each devnode removal holds a machine-wide lock, so the
    /// removals themselves run one after another, and the wall clock is
    /// about the sum of the controllers' removals. The per-controller
    /// wall-clock for each Dispose() call is reported through
    /// <paramref name="perControllerCallback"/> so callers can log a
    /// "disposed slot N in M ms" line for each, and it includes the time
    /// that controller's removals waited behind the others'. Use from any
    /// caller that already has a list of HMControllers to dispose together,
    /// such as a test harness's end-of-run cleanup. HMContext.Dispose itself
    /// uses an equivalent path internally. Called from a controller's event
    /// handler, it disposes that controller on the handler's own thread, so
    /// the controller's Dispose does not wait for that thread. An exception
    /// <paramref name="perControllerCallback"/> throws stops nothing: every
    /// controller is disposed and the orphan sweep runs, and then the
    /// exceptions come out as one <see cref="AggregateException"/> holding
    /// each one thrown.</summary>
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
            // Wrapped as the set path below wraps it.
            try { perControllerCallback?.Invoke(arr[0], sw.ElapsedMilliseconds); }
            catch (Exception ex) { throw new AggregateException(ex); }
            return;
        }
        // A callback's exception is kept, not thrown in the loop:
        // Parallel.ForEach starts no new iteration once one has thrown, which
        // would leave the controllers after it undisposed.
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        _batchDisposing = true;
        try
        {
            DisposeConcurrently(arr, c =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { c.Dispose(); } catch { }
                try { perControllerCallback?.Invoke(c, sw.ElapsedMilliseconds); }
                catch (Exception ex) { failures.Enqueue(ex); }
            });
        }
        finally
        {
            _batchDisposing = false;
            Internal.DeviceOrchestrator.RemoveOrphanHidChildrenBatch();
        }
        if (!failures.IsEmpty) throw new AggregateException(failures);
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

    /// <summary>Every profile file a load refused, and every profile that
    /// loaded with a model problem, with the file, the JSON path and the
    /// reason, in the order the loads read them. A file that does not
    /// parse, a key the schema lacks, a key written twice, a value a check
    /// refuses and an id already loaded each refuse the file, and a file
    /// with no id is not a profile. A program whose own loader swallows
    /// exceptions can show these to say why a profile is missing.</summary>
    public IReadOnlyList<HMProfileLoadIssue> ProfileLoadIssues
    {
        get { ThrowIfDisposed(); lock (_lock) return _loadIssues.ToArray(); }
    }

    /// <summary>Load profiles from a directory containing .json files
    /// matching the HIDMaestro profile schema, recursively. schema.json and
    /// the <c>models</c> folder at the directory's top are not read as
    /// profiles. Files load in ordinal order of their paths. A file whose
    /// id is already loaded, from an earlier load or an earlier file, is
    /// refused and names where the id came from, so a user's edited copy of
    /// a catalog profile needs an id of its own. Every refused file is
    /// listed in <see cref="ProfileLoadIssues"/>. Returns the number of
    /// profiles added.
    ///
    /// <para>The <c>models</c> folder holds what a JSON author would otherwise
    /// need a rebuild for, in the catalog's formats: named device models a
    /// profile takes with <c>deviceModelFrom</c>, and an
    /// <c>identity-defaults.json</c> that gives profiles outside the
    /// catalog a model by their identity, tried before the catalog's. They
    /// resolve for this directory's profiles and for later loads.</para></summary>
    public int LoadProfilesFromDirectory(string profilesDir)
    {
        if (profilesDir == null) throw new ArgumentNullException(nameof(profilesDir));
        ThrowIfDisposed();
        if (!Directory.Exists(profilesDir))
            throw new DirectoryNotFoundException($"Profiles directory not found: {profilesDir}");

        // The catalog's registry loads the embedded catalog on first use,
        // outside the context's lock, which creates and disposes wait on.
        var catalog = Internal.ModelRegistry.Catalog;
        lock (_directoryLoadLock)
        {
            Internal.ModelRegistry registry;
            lock (_lock) registry = _registry ??= catalog;
            var db = ProfileDatabase.Load(profilesDir, registry);
            lock (_lock) _registry = db.Registry;
            return Add(db);
        }
    }

    /// <summary>Load the default profile catalog embedded in the SDK
    /// assembly. The catalog ships with every supported controller: Xbox
    /// 360, Xbox One/Series, DualShock 4, DualSense, Stadia, common
    /// third-party gamepads: so consumers don't need to ship profile JSONs
    /// alongside their app. Loading it again adds nothing.</summary>
    public int LoadDefaultProfiles()
    {
        ThrowIfDisposed();
        return Add(ProfileDatabase.LoadEmbedded());
    }

    /// <summary>Adds a database's profiles and issues. A profile whose id is
    /// loaded from another file is refused, and one loaded from the same
    /// file, as when the catalog loads twice, is the same profile and adds
    /// nothing.</summary>
    private int Add(ProfileDatabase db)
    {
        int added = 0;
        lock (_lock)
        {
            foreach (var issue in db.Issues)
                if (!_loadIssues.Any(x => x.File == issue.File && x.JsonPath == issue.JsonPath && x.Reason == issue.Reason))
                    _loadIssues.Add(issue);
            foreach (var inner in db.All)
            {
                string source = inner.SourceFile ?? inner.Id;
                if (_profileSources.TryGetValue(inner.Id, out var earlier))
                {
                    if (string.Equals(earlier, source, StringComparison.OrdinalIgnoreCase)) continue;
                    _loadIssues.Add(new HMProfileLoadIssue(source, inner.Id, "$.id",
                        $"the id '{inner.Id}' is already loaded from {earlier}. Give the profile an id of its own.",
                        HMProfileLoadIssueKind.Refused));
                    continue;
                }
                var pub = new HMProfile(inner);
                _profiles.Add(pub);
                _profilesById[inner.Id] = pub;
                _profileSources[inner.Id] = source;
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
    /// <exception cref="HMProfileException">A defect of the profile itself:
    /// it has no descriptor, its layout names an axis or button the
    /// descriptor lacks, or its device model does not compile. The exception
    /// names the profile's file and the JSON path. That refusal comes before
    /// the create takes anything machine-wide.</exception>
    /// <exception cref="HMDriverVersionException">A UMDF2 create met an
    /// installed driver older than 2.0.0 that other programs' controllers
    /// keep from being replaced. The deploy gate refuses it before any PnP
    /// work, and the exception names the holders.</exception>
    /// <exception cref="InvalidOperationException">This build lacks the
    /// profile's payload (<see cref="HMPayloadMissingException"/>), the
    /// driver install failed, or the device node creation failed.</exception>
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
    /// slot number when no other program on the machine uses it and no
    /// running program built on HIDMaestro 1.12.0 has a controller present
    /// (a device a closed one left keeps at most its own index from use), so
    /// a keyless controller's index and default identity can differ between
    /// runs when other programs that embed HIDMaestro are running. Pass a
    /// key for paths that must stay put. The form <c>index:&lt;N&gt;</c> is
    /// reserved for the default: it names the identity of index N wherever
    /// it is passed. One identity is held by one live controller on the
    /// whole machine, in whichever program created it: dispose the first
    /// before creating the second. A different profile at the same key
    /// keeps the identity and refreshes the descriptor.</para></summary>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="HMProfileException">A defect of the profile itself:
    /// it has no descriptor, its layout names an axis or button the
    /// descriptor lacks, or its device model does not compile. The exception
    /// names the profile's file and the JSON path. That refusal comes before
    /// the create takes anything machine-wide.</exception>
    /// <exception cref="HMDriverVersionException">A UMDF2 create met an
    /// installed driver older than 2.0.0 that other programs' controllers
    /// keep from being replaced. The deploy gate refuses it before any PnP
    /// work, and the exception names the holders.</exception>
    /// <exception cref="HMResourceInUseException">A create whose key another
    /// program's cleanup holds, while it removes a device a closed program
    /// left under that key, first waits up to 120 seconds for that cleanup.
    /// Thrown when a live controller on this machine, in this program or
    /// another, holds the key, and, before any wait, when the key is
    /// <c>index:&lt;N&gt;</c> with N below 64 while a running program built
    /// on HIDMaestro 1.12.0 has a controller present, since that program
    /// gives the identity to its own controller on index N. A device a
    /// closed program left does not refuse its own key: the create takes
    /// that device back. A controller of a context this process dropped
    /// without disposing holds its key until the process exits.</exception>
    /// <exception cref="InvalidOperationException">This build lacks the
    /// profile's payload (<see cref="HMPayloadMissingException"/>), the
    /// driver install failed, or the device node creation failed.</exception>
    public HMController CreateController(HMProfile profile, string? identityKey)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new HMProfileException(profile.Id, profile.Inner.SourceFile, "$.descriptor",
                "the profile has no HID descriptor and cannot be deployed.");
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
        // A profile whose device model does not compile, and a payload this
        // build lacks, refuse the create before the lease, so the refusal
        // touches nothing machine-wide. The profile's checks are the ones it
        // met at load, run again for a profile built or changed since.
        try
        {
            profile.Inner.ValidateAtLoad();
            profile.Inner.ResolveForCreate();
        }
        catch (Exception e) when (Internal.ProfileDefects.IsDefect(e))
        {
            throw Internal.ProfileDefects.ToProfileException(profile.Inner, e);
        }
        if (profile.RequiresUsbipBackend)
            HMBuildInfo.ThrowIfMissing(HMPayload.UsbipTransport,
                $"Profile '{profile.Id}' is a composite persona, which rides the USB/IP transport.");
        else
            HMBuildInfo.ThrowIfMissing(HMPayload.Driver,
                $"Profile '{profile.Id}' runs on the UMDF2 driver.");

        // What programs that have exited left goes first, once per
        // process, before this create claims anything: a keyless program
        // restarted after a crash gets its index and identity back, and a
        // program that only creates composites clears what a killed one
        // left as well.
        Internal.OwnerSweep.SessionSweepOnce();

        // From the lease to the last write of the controller's entry, so the
        // exit handler never deletes the record of a create in flight
        // (OwnerSweep.RegisterExitCleanup). Creates in one process are meant
        // to run one at a time, since creation order anchors every API's
        // view of the controllers. A second create on another thread waits
        // here while the phantom cleanup waits for the write side, since
        // ReaderWriterLockSlim admits no new reader while a writer is
        // waiting. This is the gate's only first entry on a create path.
        // SetupController, called below on this thread, enters it again,
        // and a thread that already holds the read side enters again at once
        // under LockRecursionPolicy.SupportsRecursion, a writer waiting or
        // not (ReaderWriterLockSlim.TryEnterReadLockCore).
        Internal.OwnerSweep.CreateGate.EnterReadLock();
        try { return CreateUnderLease(slot, profile, identityKey); }
        finally { Internal.OwnerSweep.CreateGate.ExitReadLock(); }
    }

    private HMController CreateUnderLease(int slot, HMProfile profile, string? identityKey)
    {
        var lease = Internal.ControllerLease.Acquire(slot, identityKey,
            profile.RequiresUsbipBackend ? "usbip" : "umdf", profile.Id);
        bool keepLease = false;
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

            string? instanceId = null;
            long refused = Internal.DeviceManager.LockTakesRefusedHere;
            try
            {
                instanceId = Internal.DeviceOrchestrator.SetupController(
                    lease.TransportIndex, profile.Inner, lease.Identity);
                if (instanceId != null)
                    Internal.MachineClaims.SetControllerValue(lease.Identity.Token, "InstanceId", instanceId);

                var controller = new HMController(this, slot, lease, profile, instanceId, identityKey: lease.Identity.Key);
                lock (_lock) _controllers[slot] = controller;
                return controller;
            }
            catch (Exception e) when (Internal.DeviceOrchestrator.RefusedBeforeDevices(e))
            {
                // SetupController refused before it made a device and took
                // back what it wrote: a resource another running program
                // holds, or the deploy gate's refusal of an older driver
                // programs hold. The index's devices and configuration can
                // be another running program's.
                throw;
            }
            catch
            {
                // Best-effort cleanup of any partial state, then rethrow.
                // The devices go before the lease below gives their index
                // and identity back to the machine.
                try { Internal.DeviceOrchestrator.TeardownController(lease.TransportIndex, instanceId); } catch { }
                // A create cut short by the exit's bound on the PnP lock keeps
                // its lease, as a dispose does, so the record names what it
                // made for the next sweep.
                if (Internal.DeviceManager.LockTakesRefusedHere != refused) keepLease = true;
                throw;
            }
        }
        catch
        {
            if (!keepLease) lease.Dispose();
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
        Internal.Usbip.UsbipBackendHandle? handle = null;
        try
        {
            // The device emulator maps the index's sections before its
            // attach, so a failed attach leaves them for the catch below.
            handle = Internal.Usbip.UsbipBackend.CreateDevice(profile.Inner, index, lease.Identity);
            var controller = new HMController(this, slot, lease, profile, instanceId: null, handle, lease.Identity.Key);
            lock (_lock) _controllers[slot] = controller;
            return controller;
        }
        catch
        {
            try { handle?.Dispose(); } catch { }
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
    /// index. While a running program built on HIDMaestro 1.12.0 has a
    /// controller present, it is 64 plus N when that is free. One such
    /// program goes unseen and sets no floor: one whose controllers are all
    /// keyboards or mice, or whose ledger write failed, while its
    /// controller's index cannot be read and it shares an index with a
    /// running program built on this release (docs/design/v1.13.0.md,
    /// "Transport index and slot"). A device a closed program left
    /// keeps at most its own index from use.
    /// A keyed create first takes back the index of a device a closed
    /// program left under its identity (see
    /// <see cref="HMController.TransportIndex"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/>
    /// is negative.</exception>
    /// <exception cref="InvalidOperationException">If the slot is already
    /// in use by another live controller of this context, and for the
    /// reasons <see cref="CreateController(HMProfile)"/> gives.</exception>
    /// <exception cref="HMProfileException">As for
    /// <see cref="CreateController(HMProfile)"/>.</exception>
    public HMController CreateControllerAt(int index, HMProfile profile) => CreateControllerAt(index, profile, null);

    /// <summary>Create a controller pinned to a specific slot with a
    /// durable identity key. See <see cref="CreateController(HMProfile, string)"/>
    /// for what the key does. A null or empty key uses the transport index.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/>
    /// is negative.</exception>
    /// <exception cref="InvalidOperationException">If the slot is already
    /// in use by another live controller of this context, and for the
    /// reasons <see cref="CreateController(HMProfile)"/> gives.</exception>
    /// <exception cref="HMProfileException">As for
    /// <see cref="CreateController(HMProfile)"/>.</exception>
    /// <exception cref="HMResourceInUseException">A create whose key another
    /// program's cleanup holds, while it removes a device a closed program
    /// left under that key, first waits up to 120 seconds for that cleanup.
    /// Thrown when a live controller on this machine, in this program or
    /// another, holds the key, and, before any wait, when the key is
    /// <c>index:&lt;N&gt;</c> with N below 64 while a running program built
    /// on HIDMaestro 1.12.0 has a controller present, since that program
    /// gives the identity to its own controller on index N. A device a
    /// closed program left does not refuse its own key: the create takes
    /// that device back. A controller of a context this process dropped
    /// without disposing holds its key until the process exits.</exception>
    public HMController CreateControllerAt(int index, HMProfile profile, string? identityKey)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (!profile.IsDeployable)
            throw new HMProfileException(profile.Id, profile.Inner.SourceFile, "$.descriptor",
                "the profile has no HID descriptor and cannot be deployed.");
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
        bool keepLease = false;

        try
        {
            // Issue #39: usbip-backend controllers have no PnP devnode and no
            // driver binding. Teardown is detach-from-vhci plus the shared
            // sections; running the UMDF2 PnP teardown would sweep for devices
            // that never existed.
            if (controller.UsbipHandle != null)
            {
                // No WinUSB bind after a re-import lands once the ledger
                // below has released the controller's holds.
                try { Internal.WinUsbBinding.StopKeeping(controller); } catch { }
                try { controller.UsbipHandle.Dispose(); } catch { }
                try { Internal.SharedMemoryIO.DestroyController(controller.Index); } catch { }
                // A WinUSB binding (HMController.BindWinUsb) goes back once
                // the device is gone.
                try { Internal.RegistryLedger.Release(controller.Index); } catch { }
                return;
            }

            long refused = Internal.DeviceManager.LockTakesRefusedHere;
            try
            {
                Internal.DeviceOrchestrator.TeardownController(
                    controller.Index, controller.InstanceId, skipOrphanSweep: _batchDisposing);
            }
            finally
            {
                // A teardown during the process's exit that another program's
                // hold of the PnP lock cut short left devices present. The
                // lease, and so the record's entry for them, stays until the
                // process ends, and the next sweep removes them through it.
                if (Internal.DeviceManager.LockTakesRefusedHere != refused)
                    keepLease = true;
            }
        }
        finally
        {
            // The index and identity go back to the machine only once the
            // devices they name are gone.
            if (!keepLease) controller.Lease?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HMContext));
    }

    /// <summary>Runs <paramref name="dispose"/> on every controller at
    /// once. Called from a controller's event handler, it disposes that
    /// controller on the calling thread, the handler's own, where
    /// HMController.Dispose skips the wait for its own thread.
    /// Parallel.ForEach alone could hand that controller to a pool thread,
    /// which would wait 500 ms for the thread the handler holds.</summary>
    private static void DisposeConcurrently(HMController[] controllers, Action<HMController> dispose)
    {
        int own = Array.FindIndex(controllers, c => c.RunsOnCurrentThread);
        if (own < 0)
        {
            System.Threading.Tasks.Parallel.ForEach(controllers, dispose);
            return;
        }
        var others = controllers.Where((_, i) => i != own).ToArray();
        var rest = System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Parallel.ForEach(others, dispose));
        Exception? ownFailure = null;
        try { dispose(controllers[own]); }
        catch (Exception ex) { ownFailure = ex; }
        // Every failure comes out in one AggregateException, as it does
        // from Parallel.ForEach alone.
        try { rest.GetAwaiter().GetResult(); }
        catch (AggregateException ex) when (ownFailure != null)
        {
            throw new AggregateException(ex.InnerExceptions.Prepend(ownFailure));
        }
        if (ownFailure != null) throw new AggregateException(ownFailure);
    }

    /// <summary>Disposes every controller this context owns and frees its
    /// resources. Safe to call multiple times. A context dropped without
    /// Dispose keeps its controllers' devices, transport indices and identity
    /// keys until the process exits: a create of one of those keys in this
    /// process throws <see cref="HMResourceInUseException"/> until then, and
    /// the SDK removes the devices as the process exits.
    ///
    /// <para>A controller's teardown waits for Windows to remove its
    /// devnodes, and for an Xbox controller bound to xinputhid (an Xbox
    /// Series controller over Bluetooth) also for that filter to unload,
    /// which takes the longest. The dispose calls run on parallel threads,
    /// but every devnode removal holds a machine-wide lock that every
    /// registration and removal of a program built on this release holds,
    /// so the removals run one at a time, in this program and across
    /// programs, and the wall clock is about the sum of the controllers'
    /// removals rather than the longest one. Another program's create or
    /// removal waits that long too.</para>
    ///
    /// <para>Called while the process exits, from the program's own
    /// ProcessExit handler, the removals wait for another program's hold of
    /// that lock within one 10 s budget the process shares, as the SDK's own
    /// exit does. This program's removals queue for the lock one at a time
    /// without counting against it, unless one of them holds the lock longer
    /// than the whole 10 s, which refuses the removal queued behind it as if
    /// that hold were stuck: an xinputhid controller's removal can take that
    /// long. Threads that wait together spend the budget once. A controller
    /// whose removal the budget cuts short keeps its
    /// devices, its identity and its entry in the program's record. The
    /// runtime runs ProcessExit handlers one after another in the order they
    /// were added, and the SDK adds its own at the program's first create,
    /// InstallDriver or cleanup. When the program added its handler before
    /// that, the SDK's handler runs next and removes such a controller,
    /// waiting for the lock within its own 10 s. When the program added its
    /// handler later, when another program holds the lock past those 10 s
    /// too, or when the SDK was loaded in a load context that unloads, the
    /// devices stay until the process's claims close and a program built on
    /// this release next sweeps.</para>
    ///
    /// <para>Called from a controller's event handler, it disposes that
    /// controller on the handler's own thread, so the controller's Dispose
    /// does not wait for that thread.</para>
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
            DisposeConcurrently(toDispose, c =>
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
