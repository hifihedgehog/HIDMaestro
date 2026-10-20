using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Deploys the bundled usbip-win2 transport on demand (issue #39).
///
/// <para>Composite USB personas need a driver-backed USB device for their
/// audio endpoints, which no user-mode API can create. HIDMaestro ships
/// the transport inside HIDMaestro.Core.dll and installs it itself, the
/// same way it ships and installs its own UMDF2 driver: the consumer
/// calls <see cref="HIDMaestro.HMContext.CreateController"/> with a
/// composite profile and it works. There is no separate download, no
/// second package, and nothing for a user to go find.</para>
///
/// <para>The embedded installer is usbip-win2's own signed build,
/// unmodified: the x64 one from the 0.9.8.1 release, the ARM64 one of the
/// same tag from the project's signer. Its SHA-256 is checked after
/// extraction against the pinned value, and a mismatch throws rather than
/// running an unverified installer. The BSD-2-Clause notice ships beside
/// it and is written next to the binary at deploy time.</para>
///
/// <para>Install is silent, needs the same elevation
/// <see cref="HIDMaestro.HMContext.InstallDriver"/> already needs, and
/// is idempotent: present-and-working short-circuits to a no-op. Because
/// usbip-win2's extension INF matches the generic USB 3.0 root-hub
/// hardware ID, PnP re-enumerates the root hubs once during install,
/// which momentarily interrupts USB devices. That happens once per
/// machine, on the first composite controller ever created.</para>
///
/// <para>The install takes the drivers, the VC++ runtime their installer
/// step needs, and the uninstaller with its Apps entry. It adds no GUI,
/// SDK, symbol files, Start menu folder or desktop shortcut
/// (<see cref="InstallerArguments"/>).</para></summary>
internal static partial class UsbipDriverInstaller
{
    public const string Version = "0.9.8.1";
    /// <summary>The installer matching this OS. One AnyCPU assembly carries
    /// both architectures and picks here.</summary>
    internal static string InstallerFile => InstallerFileFor(RuntimeInformation.OSArchitecture);

    /// <summary>The installer for an architecture.</summary>
    internal static string InstallerFileFor(Architecture arch) =>
        arch == Architecture.Arm64
            ? "USBip-" + Version + "-arm64-release-attestation.exe"
            : "USBip-" + Version + "-x64.exe";

    /// <summary>The installer's embedded resource name for an architecture.</summary>
    internal static string InstallerResourceFor(Architecture arch) =>
        "HIDMaestro.Resources." + InstallerFileFor(arch);

    /// <summary>The host controller files' embedded resource names for an
    /// architecture.</summary>
    internal static IEnumerable<string> UdeResourcesFor(Architecture arch)
    {
        string a = arch == Architecture.Arm64 ? "arm64" : "x64";
        foreach (string file in UdeFiles) yield return $"HIDMaestro.Usbip.{a}.{file}";
    }

    private const string NoticeFile = "THIRD-PARTY-NOTICES.txt";

    /// <summary>Pinned SHA-256 of the bundled installer. The MSBuild
    /// PackResources target checks the same value at build time, so a
    /// corrupted or substituted binary fails the build. This is the
    /// runtime half of that check, covering the extracted copy.</summary>
    internal static string InstallerSha256 =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "cab7ff97f79275eeb5c5b8bb2eb3111ff6b6c4eead07d9bec9be5bd1e3a35800"
            : "38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a";

    private static readonly object s_lock = new();

    /// <summary>True when the vhci host controller is present and started,
    /// meaning composite personas can be created right now with no
    /// install step.</summary>
    public static bool IsInstalled => VhciClient.IsAvailable();

    /// <summary>Ensure the transport is installed, deploying the bundled
    /// installer if it is not. Idempotent and safe to call on every
    /// composite create. Returns true when the driver is usable.</summary>
    /// <param name="progress">Optional status callback: consumers with a
    /// UI can surface "installing controller audio support" during the
    /// one-time install, which takes a few seconds and briefly
    /// re-enumerates USB.</param>
    public static bool EnsureInstalled(Action<string>? progress = null)
    {
        HMBuildInfo.ThrowIfMissing(HMPayload.UsbipTransport);
        if (IsInstalled)
        {
            ReplaceLegacyTransportOnce(progress);
            StampOwnerHardwareIdOnChange();
            return true;
        }

        lock (s_lock)
        {
            if (IsInstalled) return true;

            // Every transport deploy holds the machine-wide transport lock
            // and reads the machine again under it: of two programs whose
            // first composites arrive together, one installs and the other
            // finds the host controller. Two installers at once would each
            // create a host controller devnode.
            return TransportWork.Run(work => IsInstalled || InstallLocked(work), progress);
        }
    }

    // ── The transport lock ───────────────────────────────────────────────

    /// <summary>The name, in <see cref="AdminNamespace"/>, of the
    /// machine-wide lock over the transport's install, its repair and the
    /// move off an older host controller.</summary>
    internal const string TransportMutexObject = "Transport";

    /// <summary>One piece of transport work: the usbip-win2 install, the
    /// repair of a host controller that stopped answering, or the move off an
    /// older one.
    ///
    /// <para>It runs on a thread of its own, which holds the machine-wide
    /// transport lock while the work runs and while any thread the work
    /// started and left behind still runs (<see cref="HoldLockFor"/>). This
    /// is not the deploy lock. A UMDF2 create waits for another program's
    /// driver deploy, and never for a transport install, which can run for
    /// minutes and changes nothing the UMDF2 driver uses.</para>
    ///
    /// <para>The calling thread holds no machine-wide lock. It raises the
    /// work's progress to the consumer's callback as each message comes, so
    /// a callback that blocks holds up only its own caller, and an exception
    /// a callback throws is dropped (<see cref="EventRaise"/>) and changes
    /// nothing about the outcome.</para></summary>
    internal sealed class TransportWork
    {
        private readonly BlockingCollection<string> _progress = new();
        private readonly List<Thread> _lingering = new();

        /// <summary>Hand a progress message to the calling thread.</summary>
        internal void Report(string message)
        {
            try { _progress.Add(message); }
            catch (InvalidOperationException) { /* the outcome has gone back already */ }
        }

        /// <summary>Keep the transport lock, after the outcome has gone back
        /// to the caller, until <paramref name="thread"/> ends.</summary>
        internal void HoldLockFor(Thread thread)
        {
            lock (_lingering) _lingering.Add(thread);
        }

        /// <summary>Run <paramref name="work"/> under the transport lock and
        /// return its result, or throw what it threw. A lock that cannot be
        /// taken throws as well, and the work does not run.</summary>
        internal static T Run<T>(Func<TransportWork, T> work, Action<string>? progress)
        {
            var job = new TransportWork();
            T result = default!;
            ExceptionDispatchInfo? error = null;
            var worker = new Thread(() =>
            {
                Mutex? m = null;
                try
                {
                    m = AcquireTransportLock();
                    T r = work(job);
                    lock (job) result = r;
                }
                catch (Exception ex)
                {
                    lock (job) error = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    // The outcome goes back now. The lock stays with this
                    // thread until every thread the work left behind ends.
                    job._progress.CompleteAdding();
                    if (m != null)
                    {
                        Thread[] lingering;
                        lock (job._lingering) lingering = job._lingering.ToArray();
                        foreach (var t in lingering) t.Join();
                        m.ReleaseMutex();
                        m.Dispose();
                    }
                }
            }) { IsBackground = true, Name = "HMUsbipTransport" };
            worker.Start();

            foreach (string message in job._progress.GetConsumingEnumerable())
                EventRaise.Each(progress, message, "usbip-win2 install progress");
            ExceptionDispatchInfo? thrown;
            lock (job)
            {
                thrown = error;
                if (thrown == null) return result;
            }
            thrown.Throw();
            throw thrown.SourceException; // not reached: Throw does not return
        }
    }

    /// <summary>Acquire the transport lock. An abandoned one is acquired,
    /// and the work reads the machine again before it acts.</summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    private static Mutex AcquireTransportLock()
    {
        var m = new Mutex(false, AdminNamespace.Name(TransportMutexObject));
        try
        {
            if (!m.WaitOne(TimeoutScale.Apply(600_000)))
                throw new InvalidOperationException(
                    "Another program's HIDMaestro USB transport install or repair held the machine-wide transport lock for ten minutes.");
        }
        catch (AbandonedMutexException) { }
        catch
        {
            m.Dispose();
            throw;
        }
        return m;
    }

    /// <summary>Coexistence probe section Z calls this through reflection:
    /// <paramref name="work"/> under the transport lock, the way an install
    /// runs, with its progress raised to <paramref name="progress"/>.</summary>
    internal static bool RunForProbe(Func<Action<string>, bool> work, Action<string>? progress) =>
        TransportWork.Run(w => work(w.Report), progress);

    /// <summary>The host controller repair under the transport lock, with
    /// the availability check, the PnP restart and the budget the caller
    /// passes. Coexistence probe section Z calls it through reflection with
    /// stand-ins for the first two, so it never restarts a host controller.
    /// usbip_bundle_check passes the production ones, on a machine whose
    /// transport is installed and not answering.</summary>
    internal static bool RepairForProbe(Func<bool> available, Action restart, int budgetMs, Action<string>? progress) =>
        TransportWork.Run(w => TryRestartHostController(w, available, restart, budgetMs), progress);

    /// <summary>The install itself. The caller holds the transport lock and
    /// has found no host controller answering.</summary>
    private static bool InstallLocked(TransportWork work)
    {
        // A usbip-win2 installer still running would install beside this
        // one, as one left by a program killed mid-install does: the
        // transport lock went with that program, and usbip-win2's script
        // declares no SetupMutex (setup.iss at v.0.9.8.1). Wait for it to
        // end, then read the machine again.
        if (!WaitForRunningInstallers(TimeoutScale.Apply(300_000)))
            throw new InvalidOperationException(
                "A usbip-win2 installer another program started is still running after five minutes. " +
                "Try again once it has finished.");
        if (IsInstalled) return true;

        // A machine can have usbip-win2 already (VIIPER and DS4Windows
        // install the same transport) with its host controller merely
        // not started, or left interface-less by an interrupted PnP
        // operation. Running the installer over an existing install is
        // the wrong repair for that: Inno uninstalls the old version
        // first, and its driver-package removal touches the extension
        // INF bound to every USB root hub, which re-enumerates the whole
        // USB tree and can take many minutes. Restarting the devnode
        // republishes the interface in seconds. Try that first, and only
        // install when the product is genuinely absent.
        if (IsProductInstalled())
        {
            work.Report("Repairing the existing USB audio transport...");
            if (TryRestartHostController(work)) return true;

            throw new InvalidOperationException(
                $"usbip-win2 {Version} is installed on this machine but its virtual host " +
                "controller is not available, and restarting the device did not recover it. " +
                "Reinstalling it from HIDMaestro would first uninstall the existing copy, " +
                "which detaches a filter driver from every USB root hub and can take several " +
                "minutes, so that is not done automatically. Repair or reinstall usbip-win2, " +
                "or reboot, and try again.");
        }

        // The installer's own PrepareToInstall (setup.iss 504-535) runs
        // the uninstaller of any copy whose uninstall entry it finds,
        // before it installs anything. An entry left without its driver
        // package would get that uninstaller run, which is the root-hub
        // filter removal the check above exists to avoid.
        if (InstalledUninstaller() is string uninstaller)
            throw new InvalidOperationException(
                $"usbip-win2 has an uninstall entry on this machine (its uninstaller is {uninstaller}), " +
                "but its driver package is not in the driver store. The bundled installer would run " +
                "that uninstaller first, which detaches a filter driver from every USB root hub, so " +
                "HIDMaestro does not run it. Repair or uninstall usbip-win2 from Settings > Apps, " +
                "and try again.");

        work.Report($"Installing USB audio transport (usbip-win2 {Version})...");

        int? failedWith = null;
        for (int run = 1; ; run++)
        {
            string exe = ExtractInstaller();
            int exitCode = RunSilentInstall(exe);
            // Inno Setup: 0 success, 3010 success-with-reboot-pending. The
            // driver still works before that reboot, so the availability
            // poll below is the verdict.
            if (exitCode == 0 || exitCode == 3010) break;

            // A failing exit code does not say what the installer left. On
            // this command line Inno Setup itself ends with 0 to 8 (issrc
            // 7.1.0 Setup.MainFunc.pas, ecInitializationError to
            // ecPrepareToInstallFailedRestartNeeded, and a restart code only
            // when /RESTARTEXITCODE names one), so any other code comes from
            // outside its code, such as a process that crashed or was ended.
            // The setup its loader started can still be running, or can have
            // installed everything first. So wait for every usbip-win2
            // installer to end, then read the machine: a driver package in
            // the store means the install reached its drivers, and its host
            // controller gets the poll below. A machine with neither the
            // package nor the uninstall entry is the clean machine this
            // install started from, and the installer runs once more.
            // Anything between is left as it is, and the create fails.
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: the installer exited {exitCode}; reading the machine again.");
            failedWith = exitCode;
            if (!WaitForRunningInstallers(TimeoutScale.Apply(300_000)) || IsProductInstalled()) break;
            if (run == 2 || InstalledUninstaller() != null) throw InstallerFailed(exitCode);
            DeviceOrchestrator.LogDiag("UsbipDriverInstaller: nothing of usbip-win2 is on the machine; running the installer again.");
            failedWith = null;
        }

        // PnP settles the new host controller asynchronously. The
        // driver's own device arrives quickly. The root-hub
        // re-enumeration its filter INF triggers takes longer and does
        // not gate us.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutScale.Apply(60_000))
        {
            if (VhciClient.IsAvailable())
            {
                if (failedWith is int code)
                    DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: the host controller answers after the installer exited {code}.");
                StampOwnerHardwareId();
                work.Report("USB audio transport ready.");
                return true;
            }
            Thread.Sleep(250);
        }

        if (failedWith is int failed) throw InstallerFailed(failed);
        throw new InvalidOperationException(
            $"usbip-win2 {Version} installer completed but its virtual host controller did not " +
            "appear within the timeout. Check Device Manager for 'USBip 3.X Emulated Host " +
            "Controller' and any pending-reboot state.");
    }

    // ── Moving a machine off the transport earlier releases installed ────
    //
    // HIDMaestro through v1.8.1 installed usbip-win2 0.9.7.7 (x64 only),
    // and v1.9.0 and v1.9.1 installed 0.9.7.5. The pin is now 0.9.8.1,
    // which carries the fix for the attach work item that blocked driver
    // unload, and a machine still carrying one of the older host
    // controllers is moved to it here with nothing for the user to do.
    //
    // This never runs the vendor installer. Run over an existing install
    // it launches the previous version's uninstaller, which shows a
    // window and deletes the extension INF bound to every USB root hub:
    // the whole USB tree re-enumerates and the global PnP lock is held
    // for tens of minutes. Measured on 26200, 2026-09-20. So the host
    // controller's own INF, SYS and CAT ship inside the assembly, and the
    // swap is one UpdateDriverForPlugAndPlayDevices call that forces the
    // pinned INF onto the existing host controller devnode.
    //
    // Only the host controller driver (usbip2_ude) changes. The root-hub
    // filter is left exactly as it is. The filter hands the host
    // controller its select-configuration, select-interface and pipe-reset
    // requests as a tagged control transfer, and that encoding is the same
    // in 0.9.7.5 and 0.9.8.1 (ude_filter/request.h, ude/filter_request.cpp).
    //
    // It acts on the exact drivers HIDMaestro installed and nothing else. A
    // host controller whose bytes are none of them was put there by
    // something other than HIDMaestro (VIIPER, DS4Windows, Handheld
    // Companion) and may be what that program needs, so it is left alone.

    /// <summary>A host controller an earlier HIDMaestro release installed:
    /// its usbip2_ude.sys hash, and the build stamp in its INF DriverVer,
    /// which tells its package apart in %windir%\INF.</summary>
    internal readonly record struct LegacyUde(string Version, bool Arm64, string SysSha256, string DriverVer);

    internal static readonly LegacyUde[] LegacyUdes =
    {
        // 04/19/2026,21.14.27.907. Installed through v1.8.1.
        new("0.9.7.7", false, "51db440065393e588a6b2585508c50eb3e1510b7b06d9afa6c5bde583751ea7d", "21.14.27.907"),
        // 01/27/2026,8.49.53.229 and 01/30/2026,8.37.35.720. v1.9.0 and v1.9.1.
        new("0.9.7.5", false, "db9d6a97a043deab8e34122dcf429eaabb4af0d10279c8fea1248976a368c1d4", "8.49.53.229"),
        new("0.9.7.5", true, "2a969ba59b3ffe33227ca1de17759a189d085d51c4f4e0f34df0b244ee558ab8", "8.37.35.720"),
    };

    private const string UdeHardwareId = "ROOT\\USBIP_WIN2\\UDE";
    private static readonly string[] UdeFiles = { "usbip2_ude.inf", "usbip2_ude.sys", "usbip2_ude.cat" };

    /// <summary>SHA-256 of each pinned host controller file for this
    /// machine's architecture. The build checks the same values against
    /// UsbipPackage.json before embedding the files.</summary>
    internal static string PinnedUdeHash(string file) =>
        (RuntimeInformation.OSArchitecture == Architecture.Arm64, file) switch
        {
            (false, "usbip2_ude.inf") => "1ae9e8dc497929de1a6be1291ae1dca6a19e7fd6c0351e8c266f65dd91f15073",
            (false, "usbip2_ude.sys") => "abd4fc43dce40e027e4acc884cbfe92433e026206f6565c5ee5284a39441169a",
            (false, "usbip2_ude.cat") => "84dd6cc5985857adf1514a9ee202210ff3ee1384c9be40fd49d6810e23e25f3a",
            (true, "usbip2_ude.inf") => "04b6ac34874198fc73b6bb11c003fc89f87df9d504f78109eb2b500d05ef09c1",
            (true, "usbip2_ude.sys") => "38c64546938ea99b6c3ec69f3e5169ad79afc47a59faceb51cfb35d038e249a2",
            (true, "usbip2_ude.cat") => "14c2aa313740e362256d4da721e18751f0ecf0dc19b5c5f3887f084145a6af78",
            _ => throw new ArgumentOutOfRangeException(nameof(file)),
        };

    // Set once the check below has run in this process, whatever it found.
    // Written under s_lock.
    private static volatile bool s_legacyChecked;

    private static void ReplaceLegacyTransportOnce(Action<string>? progress)
    {
        if (s_legacyChecked) return;
        lock (s_lock)
        {
            if (s_legacyChecked) return;
            // The look needs no lock. On a machine whose host controller is
            // not an older HIDMaestro one, which is every machine once moved,
            // a composite create takes no machine-wide lock and starts no
            // thread here.
            if (InstalledLegacyUde() == null)
            {
                s_legacyChecked = true;
                return;
            }
            // The move is a transport deploy like the install: under the
            // transport lock, so two programs never force the host
            // controller's driver at once, and read again under it. The
            // transport answers already, so nothing here fails the create. A
            // lock this process could not take leaves the move for its next
            // composite create.
            bool ran = false;
            try
            {
                TransportWork.Run(work =>
                {
                    Volatile.Write(ref ran, true);
                    ReplaceLegacyTransport(work);
                    return true;
                }, progress);
            }
            catch (Exception ex)
            {
                // Nothing here removes the old driver before the new one is
                // on the device, so a failure leaves the transport that was
                // answering still answering.
                DeviceOrchestrator.LogDiag(Volatile.Read(ref ran)
                    ? $"UsbipDriverInstaller: legacy replace stopped: {ex.GetType().Name}: {ex.Message}"
                    : $"UsbipDriverInstaller: legacy replace not checked, the transport lock was not taken: {ex.GetType().Name}: {ex.Message}");
            }
            if (Volatile.Read(ref ran)) s_legacyChecked = true;
        }
    }

    /// <summary>The older HIDMaestro host controller this machine runs, or
    /// null when it runs none, or its owner keeps the transport as it is.
    /// Reads the service's image and nothing else.</summary>
    private static LegacyUde? InstalledLegacyUde()
    {
        // A machine whose owner wants the transport left as it is, and the
        // battery, which has to be able to run a composite on an older
        // transport first.
        if (Environment.GetEnvironmentVariable("HIDMAESTRO_KEEP_TRANSPORT") == "1")
        {
            DeviceOrchestrator.LogDiag("UsbipDriverInstaller: HIDMAESTRO_KEEP_TRANSPORT=1; not replacing the host controller.");
            return null;
        }

        string? sys = InstalledUdeImagePath();
        if (sys == null || !File.Exists(sys)) return null;
        string installed = Sha256Of(sys);
        bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        foreach (var legacy in LegacyUdes)
            if (legacy.Arm64 == arm64 && installed.Equals(legacy.SysSha256, StringComparison.OrdinalIgnoreCase))
                return legacy;
        DeviceOrchestrator.LogDiag(
            installed.Equals(PinnedUdeHash("usbip2_ude.sys"), StringComparison.OrdinalIgnoreCase)
                ? $"UsbipDriverInstaller: host controller is the pinned {Version}."
                : $"UsbipDriverInstaller: host controller {installed[..16]} is not one HIDMaestro installed; leaving it alone.");
        return null;
    }

    private static void ReplaceLegacyTransport(TransportWork work)
    {
        if (InstalledLegacyUde() is not LegacyUde old) return;

        // Never change a driver under a device someone is using, ours or
        // another program's. The next process start tries again.
        int imports = VhciClient.GetImportedDevices().Count;
        if (imports > 0)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {old.Version} host controller has {imports} attached device(s); not replacing it now.");
            return;
        }

        // A host controller that has carried a device since Windows started
        // does not answer PnP's query-remove. Measured on 26200, 2026-09-20:
        // the forced update then sits in "Error 481: timed out waiting for
        // this device to complete a PnP query-remove request" for four
        // minutes, the old image stays loaded, the new package cannot
        // start, and the transport is gone until Windows next starts. An
        // unused controller lets go at once. Windows keeps the last arrival
        // time of every device that ever attached through the controller,
        // whoever attached it, so that is what decides. Any doubt means no.
        if (HostControllerUsedSinceBoot(out string why))
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {old.Version} host controller left alone for this session: {why}.");
            return;
        }

        work.Report($"Updating the USB audio transport to usbip-win2 {Version}...");
        DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: replacing the usbip-win2 {old.Version} host controller with {Version}.");

        string inf = StageUdePackage();

        // hmswd force-driver calls UpdateDriverForPlugAndPlayDevices with
        // INSTALLFLAG_FORCE, which puts this INF on every device carrying
        // the hardware id whatever the installed package's DriverVer.
        // It is the call usbip-win2's own devnode.exe makes. It runs in the
        // helper and not here because that call has no timeout: a host
        // controller that will not let go of its driver would hold the
        // calling thread forever, and that thread belongs to the consumer.
        // Out of process it can be given up on, and the transport that was
        // answering is still there to use.
        // The helper and the package are held, and checked under the hold,
        // until the helper has exited: the update reads the package's files
        // while it runs.
        var sw = new Stopwatch();
        using (SwdDeviceFactory.PinHelper(out string helper))
        using (PinUdePackage(inf))
        {
            sw.Start();
            var psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helper)!,
            };
            ProtectedStaging.ConfineChildTemp(psi, Path.GetDirectoryName(helper)!);
            psi.ArgumentList.Add("force-driver");
            psi.ArgumentList.Add(inf);
            psi.ArgumentList.Add(UdeHardwareId);
            using (var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start hmswd.exe."))
            {
                // Drained as it comes, so hmswd never blocks on a full pipe
                // while this waits for it to exit.
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(TimeoutScale.Apply(90_000)))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException(
                        $"The driver update for {UdeHardwareId} did not return in time; keeping the existing transport.");
                }
                if (p.ExitCode != 0 && p.ExitCode != 3010)
                    throw new InvalidOperationException(
                        $"hmswd force-driver returned {p.ExitCode}: {(stderr.Wait(TimeoutScale.Apply(5_000)) ? stderr.Result.Trim() : "")}");
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller:   driver update returned {p.ExitCode} in {sw.ElapsedMilliseconds} ms.");
            }
        }

        while (sw.ElapsedMilliseconds < TimeoutScale.Apply(60_000))
        {
            string? now = InstalledUdeImagePath();
            if (VhciClient.IsAvailable() && now != null && File.Exists(now) &&
                Sha256Of(now).Equals(PinnedUdeHash("usbip2_ude.sys"), StringComparison.OrdinalIgnoreCase))
            {
                // Only now is the old package unused, so a plain delete
                // works and nothing has to be forced. Best effort: a
                // package left behind is inert, it just ranks first again
                // if the devnode is ever recreated.
                foreach (string oem in LegacyUdePackages(old.DriverVer))
                {
                    int rc = RunPnputil($"/delete-driver {oem}");
                    DeviceOrchestrator.LogDiag($"UsbipDriverInstaller:   pnputil /delete-driver {oem} -> {rc}");
                }
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: host controller is {Version} after {sw.ElapsedMilliseconds} ms.");
                work.Report("USB audio transport ready.");
                return;
            }
            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"The usbip-win2 {Version} host controller did not come up within the timeout after the driver update.");
    }

    /// <summary>True when any device has arrived through a usbip host
    /// controller since Windows started, or when that cannot be ruled
    /// out.</summary>
    private static bool HostControllerUsedSinceBoot(out string why)
    {
        try
        {
            DateTime bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

            // The controller's one present child is its virtual root hub,
            // and every attached device is a child of that hub.
            var hubs = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int n = 0; n < 16; n++)
            {
                if (CM_Locate_DevNodeW(out uint devInst, $"ROOT\\USB\\{n:D4}", CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
                bool isUde = false;
                foreach (var id in GetMultiSz(devInst, CM_DRP_HARDWAREID))
                    if (id.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0) { isUde = true; break; }
                if (!isUde) continue;
                if (CM_Get_Child(out uint hub, devInst, 0) != CR_SUCCESS) { why = $"ROOT\\USB\\{n:D4} has no root hub to inspect"; return true; }
                var buf = new char[512];
                if (CM_Get_Device_IDW(hub, buf, buf.Length, 0) != CR_SUCCESS) { why = "root hub id unreadable"; return true; }
                hubs.Add(new string(buf).TrimEnd('\0'));
            }
            if (hubs.Count == 0) { why = "no usbip root hub found"; return true; }

            if (CM_Get_Device_ID_List_SizeW(out uint len, "USB", CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS) { why = "device list unreadable"; return true; }
            var list = new char[len];
            if (CM_Get_Device_ID_ListW("USB", list, len, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS) { why = "device list unreadable"; return true; }

            foreach (string id in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // Phantom lookup: a device that attached and left is what
                // this is looking for.
                if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) continue;
                string? parent = GetStringProperty(dev, DEVPKEY_Device_Parent);
                if (parent == null || !hubs.Contains(parent)) continue;
                DateTime? arrived = GetFileTimeProperty(dev, DEVPKEY_Device_LastArrivalDate);
                if (arrived == null) { why = $"{id} has no arrival time"; return true; }
                if (arrived.Value >= bootUtc) { why = $"{id} arrived at {arrived.Value:HH:mm:ss}Z, after Windows started"; return true; }
            }
            why = "";
            return false;
        }
        catch (Exception ex)
        {
            why = $"could not be determined ({ex.GetType().Name})";
            return true;
        }
    }

    private static string? GetStringProperty(uint devInst, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    private static DateTime? GetFileTimeProperty(uint devInst, DEVPROPKEY key)
    {
        uint size = 8;
        var buf = new byte[8];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0) != CR_SUCCESS || type != DEVPROP_TYPE_FILETIME) return null;
        return DateTime.FromFileTimeUtc(BitConverter.ToInt64(buf, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    private static readonly DEVPROPKEY DEVPKEY_Device_Parent =
        new() { fmtid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 8 };
    private static readonly DEVPROPKEY DEVPKEY_Device_LastArrivalDate =
        new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 102 };
    private const uint DEVPROP_TYPE_FILETIME = 0x10;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 1;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 1;

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);
    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Child(out uint child, uint parent, uint flags);
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_IDW(uint dnDevInst, [Out] char[] buffer, int bufferLen, uint flags);
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);
    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    /// <summary>Write the pinned host controller files to an
    /// administrators-only directory, verify each against its pinned hash,
    /// and return the INF's path.</summary>
    private static string StageUdePackage()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string dir = ProtectedStaging.DirFor(ProtectedStaging.OperationUsbipUde);

        var asm = typeof(UsbipDriverInstaller).Assembly;
        foreach (string file in UdeFiles)
        {
            string logical = $"HIDMaestro.Usbip.{arch}.{file}";
            using var src = asm.GetManifestResourceStream(logical)
                ?? throw new InvalidOperationException($"Embedded resource '{logical}' is missing from HIDMaestro.Core.dll.");
            // Verified against the pinned per-file hash from UsbipPackage.json.
            ProtectedStaging.StageStream(dir, file, src, PinnedUdeHash(file));
        }
        return Path.Combine(dir, "usbip2_ude.inf");
    }

    /// <summary>Pin the staged host controller package's directory and
    /// every directory above it (<see cref="ProtectedStaging.Pin"/>), and
    /// hold each of its files, checked against its pinned hash through the
    /// hold.</summary>
    private static ProtectedStaging.Pinned PinUdePackage(string inf)
    {
        string dir = Path.GetDirectoryName(inf)!;
        var pin = ProtectedStaging.Pin(dir);
        try
        {
            foreach (string file in UdeFiles)
                pin.Hold(Path.Combine(dir, file), PinnedUdeHash(file));
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    /// <summary>Full path of the usbip2_ude.sys the service is set to load,
    /// or null when the service does not exist.</summary>
    internal static string? InstalledUdeImagePath()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbip2_ude");
            if (k?.GetValue("ImagePath") is not string image || image.Length == 0) return null;
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            const string sysroot = @"\SystemRoot\";
            if (image.StartsWith(sysroot, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(windir, image[sysroot.Length..]);
            if (image.StartsWith(@"\??\", StringComparison.Ordinal))
                return image[4..];
            if (image.StartsWith("System32", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(windir, image);
            return image;
        }
        catch { return null; }
    }

    /// <summary>Published names (oemNN.inf) of every driver-store package
    /// that is the given release's usbip2_ude. Read from the INF files themselves, so
    /// it does not depend on the language pnputil prints in.</summary>
    private static System.Collections.Generic.List<string> LegacyUdePackages(string driverVer)
    {
        var found = new System.Collections.Generic.List<string>();
        try
        {
            string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            foreach (string path in Directory.EnumerateFiles(infDir, "oem*.inf"))
            {
                string text;
                try
                {
                    byte[] raw = File.ReadAllBytes(path);
                    text = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE
                        ? System.Text.Encoding.Unicode.GetString(raw)
                        : System.Text.Encoding.UTF8.GetString(raw);
                }
                catch { continue; }
                if (text.IndexOf("usbip2_ude", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (text.IndexOf(driverVer, StringComparison.Ordinal) < 0) continue;
                found.Add(Path.GetFileName(path));
            }
        }
        catch { }
        return found;
    }

    private static string Sha256Of(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>pnputil from System32, through the same runner as every
    /// other pnputil call: its output drained as it comes, so the timeout
    /// holds when pnputil hangs.</summary>
    private static int RunPnputil(string arguments) => PnputilHelper.Run(arguments, timeoutMs: 60_000).exitCode;

    /// <summary>True when usbip-win2's driver package is already in the
    /// driver store, whether or not its host controller is currently
    /// usable. Distinguishes "never installed here" from "installed but
    /// not working", which need different remedies.
    ///
    /// <para>False only on a whole listing of the driver store
    /// (<see cref="PnputilHelper.ListPackages"/>) that names no
    /// usbip2_ude.inf. Any other result is unknown, and unknown is never
    /// "absent": absent leads to the vendor installer, which must not run
    /// over an existing install. A listing that fails throws with the
    /// reason, and nothing is installed.</para>
    ///
    /// <para>A usbip2_ude.inf entry the driver store keeps after its INF
    /// file and FileRepository folder are gone is not listed, so it reads
    /// as absent. Nothing of it is left to repair. The installer then runs
    /// only when usbip-win2's uninstall entry is gone as well
    /// (<see cref="InstallLocked"/>), and the installer's uninstall of an
    /// earlier copy, which detaches the root-hub filter, runs only from
    /// that entry.</para></summary>
    /// <exception cref="InvalidOperationException">The driver store could not
    /// be read.</exception>
    private static bool IsProductInstalled() =>
        IsProductInstalled(() => PnputilHelper.ListPackages(n => string.Equals(n, ProductInf, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The same, with <paramref name="list"/> for the listing.
    /// Coexistence probe OF11 calls it through reflection with listings
    /// that fail.</summary>
    internal static bool IsProductInstalled(Func<List<PnputilHelper.DriverRecord>> list)
    {
        List<PnputilHelper.DriverRecord> listing;
        try { listing = list(); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"HIDMaestro could not read the driver store to tell whether usbip-win2 is installed ({ex.Message.TrimEnd('.')}), so it does not install it.", ex);
        }
        return listing.Exists(r => string.Equals(r.OriginalName, ProductInf, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The INF of usbip-win2's host controller package.</summary>
    private const string ProductInf = "usbip2_ude.inf";

    /// <summary>Wait up to <paramref name="timeoutMs"/> for every running
    /// usbip-win2 installer to end. Returns false when one still runs. An
    /// installer is a process whose name starts with <c>USBip-</c>: the
    /// release installers' own name, and the setup Inno Setup's loader
    /// extracts and runs under the same name with <c>.tmp</c>
    /// (issrc Projects/SetupLdr.dpr, the temporary directory and
    /// <c>PathChangeExt(PathExtractName(SelfFilename), '.tmp')</c>).
    /// Coexistence probe section Z calls this through reflection.</summary>
    internal static bool WaitForRunningInstallers(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var running = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                bool installer = false;
                try { installer = p.Id != Environment.ProcessId && p.ProcessName.StartsWith("USBip-", StringComparison.OrdinalIgnoreCase); }
                catch (InvalidOperationException) { /* exited while read */ }
                if (installer) running.Add(p);
                else p.Dispose();
            }
            try
            {
                if (running.Count == 0) return true;
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: waiting for a running usbip-win2 installer (pid {running[0].Id}).");
                foreach (var p in running)
                {
                    int left = (int)Math.Max(0, until - Environment.TickCount64);
                    try { if (!p.WaitForExit(left)) return false; }
                    catch (InvalidOperationException) { /* not ours to wait on, or gone */ }
                    catch (System.ComponentModel.Win32Exception) { /* access denied: polled below */ }
                }
                if (Environment.TickCount64 >= until) return false;
            }
            finally { foreach (var p in running) p.Dispose(); }
            Thread.Sleep(100);
        }
    }

    /// <summary>usbip-win2's uninstall entry, from its <c>AppId</c>.</summary>
    private const string UninstallEntryKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1";

    /// <summary>The uninstaller usbip-win2's setup would run before it
    /// installs, or null when it would run none. A port of the script's
    /// <c>GetInstalledUninstallString</c> (setup.iss 433-502): the first
    /// <c>UninstallString</c> found in the 64-bit view of HKLM, then the
    /// 32-bit view, then HKCU, read unexpanded as Inno Setup's
    /// <c>RegQueryStringValue</c> reads it (REG_SZ or REG_EXPAND_SZ,
    /// Shared.CommonFunc.pas 912-924), the executable's path taken out of it
    /// quoted or not, and kept only if that file exists.</summary>
    internal static string? InstalledUninstaller()
    {
        string? value = null;
        foreach (var (hive, view) in new[]
                 {
                     (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                     (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
                     (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
                 })
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallEntryKey);
                if (key?.GetValue("UninstallString", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string s)
                {
                    value = s;
                    break;
                }
            }
            catch { }
        }
        if (value == null) return null;
        string path = UninstallerPathOf(value);
        return path.Length > 0 && File.Exists(path) ? path : null;
    }

    /// <summary>The executable's path in an UninstallString, taken out the
    /// way the script does it (setup.iss 446-499).</summary>
    internal static string UninstallerPathOf(string uninstallString)
    {
        string u = uninstallString.Trim();
        if (u.Length == 0) return "";
        if (u[0] == '"')
        {
            u = u[1..];
            int q = u.IndexOf('"');
            return q >= 0 ? u[..q] : u;
        }
        if (File.Exists(u)) return u;

        // Unquoted with arguments: the rightmost ".exe" whose prefix names an
        // existing file, else the rightmost ".exe", else the first word.
        string lower = u.ToLowerInvariant();
        int at = -1;
        for (int i = lower.Length - 4; i >= 0; i--)
        {
            if (string.CompareOrdinal(lower, i, ".exe", 0, 4) != 0) continue;
            if (File.Exists(u[..(i + 4)].Trim())) { at = i; break; }
            if (at < 0) at = i;
        }
        if (at >= 0) return u[..(at + 4)].Trim();
        int space = u.IndexOf(' ');
        return space >= 0 ? u[..space] : u;
    }

    /// <summary>Restart the vhci devnode so it republishes its device
    /// interface. This is the cheap repair for an installed-but-unusable
    /// transport, and it never touches the root-hub filter INF.
    ///
    /// <para>Done through CfgMgr rather than <c>pnputil /restart-device</c>
    /// because that command's <c>/deviceid</c> form does not exist on
    /// Windows 10's pnputil, where it prints usage and exits zero: a
    /// silent no-op that would make this repair look like it ran. The
    /// remove-then-setup pair below is the documented restart sequence
    /// and behaves the same on 10 and 11.</para></summary>
    private static bool TryRestartHostController(TransportWork work) =>
        TryRestartHostController(work, VhciClient.IsAvailable, RestartHostControllerDevnode, TimeoutScale.Apply(30_000));

    private static bool TryRestartHostController(TransportWork work, Func<bool> available, Action restart, int budgetMs)
    {
        // Every CfgMgr call here can block indefinitely inside the PnP
        // subsystem: CM_Query_And_Remove_SubTree waits on the driver
        // stack's removal, and a wedged PnP operation elsewhere on the
        // machine holds a global lock that stalls all of them. Observed
        // during development, where an unrelated stuck pnputil made this
        // sequence hang for tens of minutes.
        //
        // A repair must never be able to hang a consumer's
        // CreateController call, so the PnP work runs on a background
        // thread and this method only ever waits a bounded time for the
        // OUTCOME it cares about: the device interface reappearing. If
        // the thread is still stuck when the budget expires, the repair
        // reports failure and the thread is left to finish (background, so
        // it cannot keep the process alive). The transport lock stays held
        // until it does: a second program waits for it, or gives up after
        // the lock's timeout, and never repairs the same devnode alongside
        // it.
        var worker = new Thread(() =>
        {
            try { restart(); }
            catch { /* the poll below is the only verdict that matters */ }
        })
        { IsBackground = true, Name = "HMUsbipRepair" };
        worker.Start();
        work.HoldLockFor(worker);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            if (available())
            {
                work.Report("USB audio transport ready.");
                return true;
            }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>The restart itself: start the vhci devnode, and when that
    /// fails, remove and set it up again.</summary>
    private static void RestartHostControllerDevnode()
    {
        // Same instance-number caveat as StampOwnerHardwareId: the vhci root
        // is not always 0000. Repair the first present controller that
        // carries the usbip UDE id.
        uint devInst = 0;
        bool located = false;
        for (int n = 0; n < 16 && !located; n++)
        {
            if (CM_Locate_DevNodeW(out devInst, $"ROOT\\USB\\{n:D4}", CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                continue;
            foreach (var hwid in GetMultiSz(devInst, CM_DRP_HARDWAREID))
                if (hwid.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0) { located = true; break; }
        }
        if (!located)
            return;

        // Start it first. The common broken state is a devnode that is
        // root-enumerated with its driver loaded but never started
        // (DN_STARTED clear, no problem code), and for that a plain setup is
        // the whole fix: cheap, and it does not block the way a subtree
        // removal does. Only escalate to remove-and-setup when starting
        // fails.
        if (CM_Setup_DevNode(devInst, CM_SETUP_DEVNODE_READY) == CR_SUCCESS)
            return;

        CM_Query_And_Remove_SubTreeW(devInst, out _, IntPtr.Zero, 0, 0);
        if (CM_Setup_DevNode(devInst, CM_SETUP_DEVNODE_READY) != CR_SUCCESS
            && CM_Locate_DevNodeW(out uint root, null, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
        {
            // Still not up, so ask the root to re-enumerate and let PnP
            // rebuild the devnode.
            CM_Reenumerate_DevNode(root, 0);
        }
    }

    /// <summary>Additional hardware ID stamped onto the emulated host
    /// controller so a composite persona's devnode ancestry names
    /// HIDMaestro (issue #42).
    ///
    /// <para>A composite persona is byte-for-byte a real Sony pad at every
    /// level a filter can inspect, which is required for the UAC class
    /// driver to bind and must not change. That leaves a host with nothing
    /// of its own to match on: the standard HIDMAESTRO token every UMDF2
    /// virtual carries in its hardware IDs is absent from the whole chain,
    /// so a consumer filtering its own virtual pads out of enumeration
    /// picks the persona up as a second controller. On hardware that
    /// showed up as SDL assigning player index 1 and lighting a lone pad
    /// red.</para>
    ///
    /// <para>The token goes on the host controller, the one node
    /// HIDMaestro brings to the tree, and never on the persona. It is
    /// added to the existing hardware IDs rather than replacing them: the
    /// upstream ROOT\USBIP_WIN2\UDE id stays first, so driver matching
    /// resolves exactly as before and the write is additive.</para></summary>
    private const string OwnerHardwareId = "ROOT\\HIDMAESTRO_UDE";

    /// <summary>Append <see cref="OwnerHardwareId"/> to the host
    /// controller's hardware IDs if it is not already there. Idempotent,
    /// cheap enough to run on every composite create (one registry read,
    /// and a write only on the first call per machine), and best-effort:
    /// a machine where this cannot be written still creates controllers,
    /// it just cannot be filtered by the token.</summary>
    internal static bool StampOwnerHardwareId() => StampAll() > 0;

    /// <summary>The host controllers' interface list that the last stamp
    /// pass on a create found every controller of stamped, or null.</summary>
    private static string? s_stampedFor;

    /// <summary><see cref="StampOwnerHardwareId"/> for a composite create,
    /// passed over while the host controllers' interface list is the one
    /// a pass found each of them stamped for. A host controller added,
    /// reinstalled under a new instance number or removed changes the
    /// list, and the pass runs again. The pass reads sixteen instance
    /// numbers, about 90 microseconds of each composite create on the bench
    /// machine on 2026-10-19. The count compared is one interface per host
    /// controller: a list that holds more interfaces than the pass stamped
    /// controllers is not kept, and the next create runs the pass
    /// again.</summary>
    private static void StampOwnerHardwareIdOnChange()
    {
        string? list = VhciClient.InterfaceList();
        if (list != null && string.Equals(list, Volatile.Read(ref s_stampedFor), StringComparison.Ordinal)) return;
        int stamped = StampAll();
        if (list != null && stamped == list.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length)
            Volatile.Write(ref s_stampedFor, list);
    }

    /// <summary>Stamp every present host controller of the transport, and
    /// count those that carry the stamp after it.</summary>
    private static int StampAll()
    {
        // The vhci host controller is ROOT-enumerated, and its instance
        // NUMBER is not stable: deleting and reinstalling the transport
        // (or a second install racing a phantom of the first) makes PnP
        // allocate ROOT\USB\0001 while a stamped 0000 lingers, and every
        // attach then rides the unstamped sibling. Found live 2026-08-07:
        // 0000 present and stamped, 0001 present, unstamped, and carrying
        // the personas, which broke the #42 ancestry filter downstream.
        // So: enumerate the instance-number space and stamp EVERY present
        // controller whose hardware IDs mark it as the usbip UDE root,
        // rather than assuming instance 0000.
        int stamped = 0;
        for (int n = 0; n < 16; n++)
        {
            string instanceId = $"ROOT\\USB\\{n:D4}";
            if (StampOne(instanceId))
                stamped++;
        }
        return stamped;
    }

    private static bool StampOne(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                return false;

            string[] ids = GetMultiSz(devInst, CM_DRP_HARDWAREID);
            bool isUde = false;
            foreach (var id in ids)
            {
                if (string.Equals(id, OwnerHardwareId, StringComparison.OrdinalIgnoreCase))
                    return true; // already stamped
                if (id.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0)
                    isUde = true;
            }
            if (!isUde)
                return false; // some other root-enumerated USB controller: leave it alone

            // Upstream's id stays at index 0 so driver matching is unchanged.
            var merged = new string[ids.Length + 1];
            Array.Copy(ids, merged, ids.Length);
            merged[ids.Length] = OwnerHardwareId;

            // SetupDi, not CM_Set_DevNode_Registry_Property. The CfgMgr
            // setter reports CR_SUCCESS on this devnode and the Enum key is
            // left untouched, so the stamp silently does nothing. The
            // SPDRP_HARDWAREID path below is what usbip-win2's own
            // installer uses to write this exact property
            // (userspace/devnode/main.cpp, install_devnode_and_driver).
            byte[] buf = MultiSzToBytes(merged);
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == INVALID_HANDLE_VALUE) return false;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
                    return false;
                if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, buf, (uint)buf.Length))
                    return false;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            DeviceOrchestrator.LogDiag(
                $"UsbipDriverInstaller: stamped {OwnerHardwareId} onto {instanceId} " +
                $"(was {ids.Length} id(s)).");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Read a REG_MULTI_SZ devnode property, or an empty array.</summary>
    private static string[] GetMultiSz(uint devInst, uint prop)
    {
        uint len = 0, type = 0;
        CM_Get_DevNode_Registry_PropertyW(devInst, prop, ref type, null, ref len, 0);
        if (len == 0) return Array.Empty<string>();
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(devInst, prop, ref type, buf, ref len, 0) != CR_SUCCESS)
            return Array.Empty<string>();
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)len)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] MultiSzToBytes(string[] values)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in values) { sb.Append(v); sb.Append('\0'); }
        sb.Append('\0'); // REG_MULTI_SZ terminator
        return System.Text.Encoding.Unicode.GetBytes(sb.ToString());
    }

    // cfgmgr32.h and setupapi.h number these DIFFERENTLY, and the two
    // sets are one apart on exactly this property: CM_DRP_DEVICEDESC is
    // 1 and CM_DRP_HARDWAREID is 2, while SPDRP_DEVICEDESC is 0 and
    // SPDRP_HARDWAREID is 1. Reading with the SetupDi number returns the
    // device description, which then gets written back as the hardware
    // id and unbinds the driver.
    private const uint CM_DRP_HARDWAREID = 0x00000002; // cfgmgr32.h
    private const uint SPDRP_HARDWAREID = 0x00000001;  // setupapi.h
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, ref uint pulRegDataType, byte[]? Buffer, ref uint pulLength, uint ulFlags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint Flags, ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CM_SETUP_DEVNODE_READY = 0;
    private const int CR_SUCCESS = 0;

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint pdnDevInst, string? pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Query_And_Remove_SubTreeW(uint dnAncestor, out uint pVetoType, IntPtr pszVetoName, uint ulNameLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Setup_DevNode(uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    /// <summary>Stage the bundled installer and its license notice into the
    /// protected per-version "usbip" directory (Administrators/SYSTEM-write
    /// only), verifying the binary against the pinned SHA-256. Through
    /// v1.12.0 this lived in <c>%TEMP%\HIDMaestro_usbip_&lt;ver&gt;</c>, which
    /// any process of the user could write between the check and the run.</summary>
    private static string ExtractInstaller()
    {
        string dir = ProtectedStaging.DirFor(ProtectedStaging.OperationUsbip);
        string exePath = Path.Combine(dir, InstallerFile);

        var asm = typeof(UsbipDriverInstaller).Assembly;
        string logical = InstallerResourceFor(RuntimeInformation.OSArchitecture);
        using (var stream = asm.GetManifestResourceStream(logical)
            ?? throw new InvalidOperationException(
                $"The bundled usbip-win2 installer ('{logical}') is missing from " +
                "HIDMaestro.Core.dll. The PackResources MSBuild target fetches and embeds it; " +
                "rebuild the SDK."))
        {
            // Verified against the pinned release hash, not merely the
            // embedded bytes' own hash, so a substituted embedded resource is
            // also caught.
            ProtectedStaging.StageStream(dir, InstallerFile, stream, InstallerSha256);
        }

        // Ship the BSD-2-Clause notice next to the binary, which is the
        // license's "documentation and/or other materials provided with
        // the distribution" requirement met at the point of deployment.
        try
        {
            using var notice = asm.GetManifestResourceStream("HIDMaestro.Resources." + NoticeFile);
            if (notice != null)
            {
                using var nf = File.Create(Path.Combine(dir, NoticeFile));
                notice.CopyTo(nf);
            }
        }
        catch { /* the notice is also embedded in the assembly itself */ }

        return exePath;
    }

    /// <summary>The installer's command line: no UI, no message boxes, no
    /// reboot, and the smallest set it allows. Checked against the compiled
    /// script inside both pinned installers (innounp 2.71.1) and the source
    /// at tag v.0.9.8.1 (userspace/innosetup/setup.iss):
    /// <list type="bullet">
    /// <item><c>/COMPONENTS=main,client</c> deselects <c>gui</c>, <c>sdk</c>
    /// and <c>pdb</c>, which the default type, <c>full</c>, installs (lines
    /// 126-131). <c>client</c> is the drivers themselves: the filter and the
    /// host controller (lines 176-177).</item>
    /// <item><c>/TASKS=vcredist</c> keeps only the VC++ runtime, which
    /// <c>devnode.exe</c>, the program that installs the host controller,
    /// imports (MSVCP140.dll, VCRUNTIME140.dll). <c>desktopicon</c> goes,
    /// along with <c>gui</c> (line 166).</item>
    /// <item><c>/NOICONS</c> checks "Don't create a Start Menu folder", the
    /// only switch that drops the uninstall shortcut <c>main</c> creates
    /// (line 135). It applies because the script sets
    /// <c>AllowNoIcons=yes</c>.</item>
    /// <item><c>/NORESTART</c> because the script sets
    /// <c>AlwaysRestart=yes</c> (line 112), and a very silent setup without
    /// it "will reboot without asking".</item>
    /// </list></summary>
    internal const string InstallerArguments =
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS=main,client /TASKS=vcredist /NOICONS";

    /// <summary>The installer's <see cref="ProcessStartInfo"/>: its arguments,
    /// no window, its working directory and TEMP and TMP confined to its own
    /// protected staging folder. Its loader extracts and runs its setup
    /// program inside TEMP (issrc SetupLdr.dpr, GetTempDir reads TMP first),
    /// so confining TEMP keeps that in a folder only an administrator can
    /// reach. A seam <see cref="RunSilentInstall"/> and the coexistence probe
    /// both use, so dropping the confinement shows in the test.</summary>
    internal static ProcessStartInfo BuildInstallerStartInfo(string exePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = InstallerArguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        ProtectedStaging.ConfineChildTemp(psi, Path.GetDirectoryName(exePath)!);
        return psi;
    }

    /// <summary>Run the Inno Setup installer silently with
    /// <see cref="InstallerArguments"/> and return its exit code.</summary>
    private static int RunSilentInstall(string exePath)
    {
        // Point-of-use check under a pin held until the installer exits: the
        // staged installer still matches the pinned hash, and its directory
        // cannot be renamed or replaced while it runs elevated.
        using var pin = ProtectedStaging.PinFile(exePath, InstallerSha256);
        var psi = BuildInstallerStartInfo(exePath);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the usbip-win2 installer.");

        if (!p.WaitForExit(TimeoutScale.Apply(300_000)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException(
                "The usbip-win2 installer did not finish within the timeout.");
        }
        return p.ExitCode;
    }

    private static InvalidOperationException InstallerFailed(int exitCode) =>
        new($"The usbip-win2 installer failed with exit code {exitCode}. " +
            "Installing a driver requires elevation, the same as HMContext.InstallDriver.");
}
