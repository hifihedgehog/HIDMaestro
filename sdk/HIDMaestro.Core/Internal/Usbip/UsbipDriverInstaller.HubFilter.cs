using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>usbip-win2's root-hub filter on the root hub of HIDMaestro's
/// own host controller, and on no other hub (issue #76).
///
/// <para>Windows' USB audio driver (usbaudio.sys) reads the bus time through
/// the hub's bus interface (USB_BUS_INTERFACE_USBDI, QueryBusTime). Under the
/// virtual host controller that call fails, and usbaudio then takes a speaker
/// stream in 20 ms pairs where a real controller takes it one 10 ms period
/// at a time. A game that renders to the controller speaker and to its main
/// output from one audio engine drops samples on the main output. Measured
/// 2026-11-24 with a shared-mode event-driven render of silence: padding 1056
/// then 96 at alternate events without the filter, 576 at every event with
/// it, on the same hub of the same machine. The filter answers that call
/// (drivers/ude_filter/query_interface.cpp at v.0.9.8.1, QueryBusTime from
/// KeQueryInterruptTime), and passes select-configuration, select-interface
/// and pipe-reset requests to the host controller (int_dev_ctrl.cpp), which
/// UDE does not.</para>
///
/// <para>usbip-win2's installer adds the filter to every USB 3 root hub
/// through its extension INF (usbip2_filter.inf, AddFilter on
/// USB\ROOT_HUB30), and that install restarts every USB 3 root hub on the
/// machine (drivers/ude_filter/readme.txt). HIDMaestro does not. It runs the
/// filter as a service of its own, <see cref="HubFilterService"/>, and lists
/// that service in the UpperFilters of its own host controller's root hub
/// alone. The filter attaches only to a hub whose device belongs to
/// \Driver\usbip2_ude (device.cpp, is_above_vhci), and no other hub lists
/// it. A restart of that one hub loads it, which this does only while no
/// device sits on the hub: the hub carries nothing but usbip imports. With
/// a device on it, the filter loads at the hub's next start.</para>
///
/// <para>The image is usbip2_filter.sys of the pinned release, unpacked from
/// the same installers as the host controller and checked against its
/// pinned hash (UsbipPackage.json), unmodified. Its signature is its own,
/// embedded in the file, so no package goes into the driver store and no
/// INF matches any device.</para></summary>
internal static partial class UsbipDriverInstaller
{
    /// <summary>The service HIDMaestro runs the root-hub filter under, and
    /// the name a hub's UpperFilters lists. usbip-win2's own installer uses
    /// usbip2_filter, so the two never collide.</summary>
    internal const string HubFilterService = "HIDMaestroUsbipFilter";

    /// <summary>The filter's file in the pinned installers and its embedded
    /// resource name's last part.</summary>
    internal const string HubFilterFile = "usbip2_filter.sys";

    /// <summary>The driver object names of the two filters a hub's stack can
    /// show: usbip-win2's own install, and HIDMaestro's.</summary>
    private const string VendorHubFilterDriver = @"\Driver\usbip2_filter";
    private const string OwnHubFilterDriver = @"\Driver\" + HubFilterService;

    /// <summary>SHA-256 of the pinned usbip2_filter.sys for this machine's
    /// architecture, as UsbipPackage.json lists it.</summary>
    internal static string PinnedHubFilterHash() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "29734c76f96a33dcfdfbaf633997deb8af98df5b8f5c11195e5ac955f0a97399"
            : "b93a5fc3c23d56f9b49634742374e32f269e53f764caa2d5fcb43e3259f7eced";

    /// <summary>The image's file name: the service name and the first eight
    /// hex digits of its hash, so a later pin writes a new file beside an
    /// older one that a running hub still holds.</summary>
    internal static string HubFilterImageName => $"{HubFilterService}_{PinnedHubFilterHash()[..8]}.sys";

    /// <summary>Full path of the image the service loads.</summary>
    internal static string HubFilterImagePath => Path.Combine(Environment.SystemDirectory, "drivers", HubFilterImageName);

    /// <summary>The service's ImagePath value, the form an INF install
    /// writes.</summary>
    private static string HubFilterServiceBinary => @"\SystemRoot\System32\drivers\" + HubFilterImageName;

    /// <summary>The host controllers' interface list that the last pass on a
    /// create found every hub of filtered for, or null.</summary>
    private static string? s_hubFilteredFor;

    /// <summary>What the last pass in this process found, for the probes:
    /// hubs seen, hubs whose stack holds a usbip filter, and hubs that list
    /// the filter and wait for their next start because devices are
    /// attached.</summary>
    internal static (int Hubs, int Filtered, int Waiting) LastHubFilterPass => (
        Volatile.Read(ref s_passHubs), Volatile.Read(ref s_passFiltered), Volatile.Read(ref s_passWaiting));
    private static int s_passHubs, s_passFiltered, s_passWaiting;

    /// <summary>The filter on each driven host controller's root hub, for a
    /// composite create, passed over while the host controllers' interface
    /// list is the one a pass found every hub filtered for. A host
    /// controller added, reinstalled or removed changes the list, and the
    /// pass runs again. Under the transport lock taken with no wait
    /// (<see cref="WithTransportLockNow"/>), as the owner stamp is: when
    /// another program holds it, or this process's own removal runs, nothing
    /// is written and a later create runs the pass. <paramref name="list"/>
    /// is the create's one read of the interface list, which the owner stamp
    /// shares.</summary>
    private static void AttachHubFilterOnChange(string? list)
    {
        if (list != null && string.Equals(list, Volatile.Read(ref s_hubFilteredFor), StringComparison.Ordinal)) return;
        try
        {
            WithTransportLockNow(() => RunHubFilterPass(list));
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: root hub filter not checked: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The pass inside a host controller install's own work, which
    /// holds the transport lock: the new root hub carries no device yet, so
    /// the filter loads before the create attaches its own.</summary>
    private static void HubFilterPassInInstall()
    {
        try { RunHubFilterPass(VhciClient.InterfaceList()); }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: root hub filter not checked after the install: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One pass and its bookkeeping. The caller holds the transport
    /// lock. A pass whose every hub reached a state no later create in this
    /// process changes (the filter loaded, waiting for the hub's next start,
    /// its service marked for deletion until Windows next starts, or this
    /// pin's filter failed on the hub) keeps the list, so creates pass over
    /// it. A hub not checked, because a lock was held elsewhere, the hub was
    /// down for another reason or a step failed, leaves it for the next
    /// create.</summary>
    private static void RunHubFilterPass(string? list)
    {
        var (hubs, filtered, waiting, unchecked_) = AttachHubFilters();
        Volatile.Write(ref s_passHubs, hubs);
        Volatile.Write(ref s_passFiltered, filtered);
        Volatile.Write(ref s_passWaiting, waiting);
        if (list != null && hubs > 0 && unchecked_ == 0)
            Volatile.Write(ref s_hubFilteredFor, list);
    }

    // ── One restart a Windows session ────────────────────────────────────

    /// <summary>A volatile key, which Windows drops when it next starts: a
    /// value for each root hub a restart was tried on since then. A hub that
    /// Windows refused to restart, or whose restart still runs, is not
    /// restarted for the filter again until then. It stands apart from the
    /// session note (<see cref="CarriedThisSessionKey"/>), whose key alone
    /// tells a removal that a host controller carried a device.</summary>
    internal const string HubRestartKey = @"SOFTWARE\HIDMaestroUsbipHubRestart";

    private static bool HubRestartTried(string hubId)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(HubRestartKey);
            return k?.GetValue(hubId) != null;
        }
        catch { return false; }
    }

    private static void NoteHubRestartTried(string hubId)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(HubRestartKey,
                Microsoft.Win32.RegistryKeyPermissionCheck.Default, Microsoft.Win32.RegistryOptions.Volatile);
            k.SetValue(hubId, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: the hub restart not noted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Delete the restart note, for the removal. True when it is
    /// gone.</summary>
    internal static bool ClearHubRestartNote()
    {
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(HubRestartKey, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(HubRestartKey);
            return k == null;
        }
        catch { return false; }
    }

    // ── A filter that failed ─────────────────────────────────────────────

    /// <summary>The install record's value that holds the pinned filter's
    /// hash once that filter failed to come up on a hub. No pass adds that
    /// filter again: a hub that cannot start with it would leave the
    /// machine with no USB/IP device at all. A later pin is tried
    /// afresh.</summary>
    private const string HubFilterFailedValue = "HubFilterFailed";

    private static bool HubFilterFailed()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UsbipInstallRecord.Key);
            return string.Equals(k?.GetValue(HubFilterFailedValue) as string, PinnedHubFilterHash(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void NoteHubFilterFailed()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UsbipInstallRecord.Key);
            k.SetValue(HubFilterFailedValue, PinnedHubFilterHash(), Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: the filter's failure not noted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One pass over the driven host controllers. The caller holds
    /// the transport lock.</summary>
    private static (int Hubs, int Filtered, int Waiting, int Unchecked) AttachHubFilters()
    {
        int hubs = 0, filtered = 0, waiting = 0, unchecked_ = 0;
        foreach (uint controller in DrivenHostControllerNodes())
        {
            hubs++;
            switch (AttachHubFilter(controller, WaitForRootHub(controller)))
            {
                case HubFilterState.Loaded: filtered++; break;
                case HubFilterState.Waiting: waiting++; break;
                case HubFilterState.Unavailable:
                case HubFilterState.Failed: break;
                default: unchecked_++; break;
            }
        }
        return (hubs, filtered, waiting, unchecked_);
    }

    private enum HubFilterState { Loaded, Waiting, Unavailable, Failed, NotChecked }

    /// <summary>A hub restart's outcome: the hub back as asked within the
    /// budget, the removal refused with the hub left as it was, the hub not
    /// back as asked, the restart still running when the budget ran out, or
    /// no restart because a device was on the hub or an attach held the
    /// gate.</summary>
    private enum HubRestart { Back, Refused, NotBack, Running, Busy }

    // ── The gate between a hub restart and the attaches ──────────────────

    /// <summary>The name, in <see cref="AdminNamespace"/>, of the machine-wide
    /// gate between a root hub's restart and the attaches. A restart holds it
    /// from its last check that no device sits on the hub to its end. An
    /// attach holds it across its plugin request (VhciClient.Attach), and a
    /// replug across its unplug and plug (UsbipBackend.ReplugForStamp,
    /// UsbipBackendHandle.Replug). So no device lands on a hub that a restart
    /// takes away.</summary>
    internal const string HubRestartGateObject = "UsbipHubRestart";

    /// <summary>Hold the hub restart gate until the scope ends, waiting up to
    /// 30 s for a restart in any program to end. An abandoned gate is taken.
    /// Throws when a restart holds it past that. Dispose the scope on the
    /// thread that made it.</summary>
    internal static IDisposable HoldHubRestartGate()
    {
        var m = new Mutex(false, AdminNamespace.Name(HubRestartGateObject));
        bool held;
        try { held = m.WaitOne(TimeoutScale.Apply(30_000)); }
        catch (AbandonedMutexException) { held = true; }
        if (held) return new GateHold(m);
        m.Dispose();
        throw new InvalidOperationException(
            "HIDMaestro's USB/IP root hub is restarting and has not come back in 30 s, so no device can be plugged into it yet.");
    }

    private sealed class GateHold : IDisposable
    {
        private readonly Mutex _m;
        private bool _done;
        internal GateHold(Mutex m) => _m = m;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _m.ReleaseMutex();
            _m.Dispose();
        }
    }

    /// <summary>Hubs this process started or enumerated again after finding
    /// them down: once each.</summary>
    private static readonly HashSet<string> s_hubRecoveryTried = new(StringComparer.OrdinalIgnoreCase);

    private static bool FirstRecoveryHere(string hubId)
    {
        lock (s_hubRecoveryTried) return s_hubRecoveryTried.Add(hubId);
    }

    /// <summary>The filter on one root hub, <paramref name="hub"/>, or 0 when
    /// the host controller published none. Loaded when a usbip filter is in
    /// the hub's stack after the call. Waiting when the hub lists HIDMaestro's
    /// and loads it at its next start: devices are attached, Windows refused
    /// the hub's restart, or a restart was tried this Windows session.
    /// Unavailable while the service is marked for deletion. Failed when this
    /// pin's filter failed on the hub, now or before. NotChecked when a lock
    /// or the gate was held elsewhere, the hub is down for another reason, a
    /// restart still runs, or a step failed.
    ///
    /// <para>Every devnode write runs under the PnP lock taken with no wait,
    /// after the transport lock: the order every transport deploy takes them
    /// in. The hub's restart runs on a thread of its own, as the host
    /// controller's repair does (<see cref="TryRestartHostController(TransportWork)"/>):
    /// CfgMgr can block while Plug and Play is held elsewhere, so the create
    /// waits for the outcome only up to a budget, with the PnP lock held. The
    /// thread holds the hub restart gate (<see cref="HubRestartGateObject"/>)
    /// from its last check that the hub is empty to its end, so a restart
    /// that outlasts the budget still has no device on its hub: the create's
    /// attach waits for the gate. Each hub is restarted at most once each
    /// Windows session (<see cref="HubRestartKey"/>).</para></summary>
    private static HubFilterState AttachHubFilter(uint controller, uint hub)
    {
        string id = hub != 0 ? DeviceIdOf(hub) : DeviceIdOf(controller) + @"\root hub";
        using var pnp = DeviceManager.TryHoldPnpLock(0, out _);
        if (pnp == null) return HubFilterState.NotChecked;

        // The hub's restart on its own thread, in the steps of the host
        // controller's repair (RestartHostControllerDevnode): start it, and
        // when that fails, or removeFirst asks, remove it and start it
        // again, then have its parent enumerate it. Back when the
        // controller's root hub returns within the budget: with HIDMaestro's
        // filter in its stack (withFilter), or started (not withFilter).
        HubRestart Restart(uint target, bool removeFirst, bool withFilter)
        {
            var sw = Stopwatch.StartNew();
            int remove = int.MinValue, start = int.MinValue, tried = 0;
            uint veto = 0;
            var worker = new Thread(() =>
            {
                Mutex? gate = null;
                bool held = false;
                try
                {
                    gate = new Mutex(false, AdminNamespace.Name(HubRestartGateObject));
                    try { held = gate.WaitOne(0); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) return;
                    // A running hub restarts only with no device on it. A hub
                    // that is down has none started.
                    if (removeFirst && (CM_Get_Child(out _, target, 0) == CR_SUCCESS || VhciClient.GetImportedDevices().Count > 0))
                        return;
                    NoteHubRestartTried(id);
                    tried = 1;
                    if (!removeFirst)
                    {
                        start = CM_Setup_DevNode(target, CM_SETUP_DEVNODE_READY);
                        if (start == CR_SUCCESS) return;
                    }
                    remove = CM_Query_And_Remove_SubTreeW(target, out veto, IntPtr.Zero, 0, CM_REMOVE_UI_OK);
                    if (remove != CR_SUCCESS) return;
                    start = CM_Setup_DevNode(target, CM_SETUP_DEVNODE_READY);
                    // The hub's record may be gone with the removal. Its
                    // parent enumerates it again.
                    if (start != CR_SUCCESS) CM_Reenumerate_DevNode(controller, 0);
                }
                catch { /* the outcome below is the verdict */ }
                finally
                {
                    if (held) gate!.ReleaseMutex();
                    gate?.Dispose();
                }
            }) { IsBackground = true, Name = "HMUsbipHubRestart" };
            worker.Start();
            int budget = TimeoutScale.Apply(15_000);
            HubRestart outcome =
                !worker.Join(budget) ? HubRestart.Running
                : tried == 0 ? HubRestart.Busy
                : removeFirst && remove != CR_SUCCESS ? HubRestart.Refused
                : HubBack(controller, withFilter, Math.Max(TimeoutScale.Apply(5_000), budget - (int)sw.ElapsedMilliseconds)) ? HubRestart.Back
                : HubRestart.NotBack;
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} restarted {(withFilter ? "for" : "without")} the root hub filter " +
                $"(remove CR {Volatile.Read(ref remove)}, veto {Volatile.Read(ref veto)}, setup CR {Volatile.Read(ref start)}): {outcome} after {sw.ElapsedMilliseconds} ms.");
            return outcome;
        }

        // A hub that is not started gets five seconds, as one still starting
        // at sign-in needs. Null when it runs, else the state to report. A
        // hub down with a driver failure while it lists HIDMaestro's filter
        // has the filter as its suspect: the filter leaves the list, the hub
        // starts without it, and no pass adds this pin again
        // (HubFilterFailed), unless the filter's service or image is
        // missing, which is no fault of the filter's. A hub down for another
        // reason, one whose restart ended between its removal and its start
        // with its program among them, is started once in this process, and
        // one not in the tree is enumerated again. A disabled hub stays as
        // it is.
        HubFilterState? Down()
        {
            if (HubRunning(controller)) return null;
            if (CM_Get_Child(out uint down, controller, 0) != CR_SUCCESS)
            {
                if (!FirstRecoveryHere(id)) return HubFilterState.NotChecked;
                int again = CM_Reenumerate_DevNode(controller, 0);
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} is not in the tree; its host controller enumerated it again (CR {again}).");
                return WaitForRootHub(controller) != 0 && HubRunning(controller) ? null : HubFilterState.NotChecked;
            }
            if (CM_Get_DevNode_Status(out _, out uint problem, down, 0) != CR_SUCCESS) return HubFilterState.NotChecked;
            string[] listedNow = GetMultiSz(down, CM_DRP_UPPERFILTERS);
            if (FilterFailure(problem) && Array.Exists(listedNow, IsOwnFilter))
            {
                bool intact = HubFilterServiceIntact();
                string[] without = Array.FindAll(listedNow, f => !IsOwnFilter(f));
                byte[] value = MultiSzToBytes(without);
                int cr = CM_Set_DevNode_Registry_PropertyW(down, CM_DRP_UPPERFILTERS, without.Length == 0 ? null : value, without.Length == 0 ? 0u : (uint)value.Length, 0);
                if (intact) NoteHubFilterFailed();
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} listed the root hub filter and stayed down with problem {problem}: unlisted (CR {cr}); " +
                    (intact ? "this filter is not added again." : "its service or image was missing, so a later pass adds it again."));
                if (cr == CR_SUCCESS) Restart(down, removeFirst: false, withFilter: false);
                return intact ? HubFilterState.Failed : HubFilterState.NotChecked;
            }
            if (problem == CM_PROB_DISABLED || !FirstRecoveryHere(id)) return HubFilterState.NotChecked;
            return Restart(down, removeFirst: false, withFilter: false) == HubRestart.Back ? null : HubFilterState.NotChecked;
        }

        if (hub == 0 || !IsStarted(hub))
        {
            if (Down() is { } state) return state;
            if (CM_Get_Child(out uint up, controller, 0) != CR_SUCCESS || !IsStarted(up)) return HubFilterState.NotChecked;
            hub = up;
        }

        string[] stack = GetStackProperty(hub);
        string[] upper = GetMultiSz(hub, CM_DRP_UPPERFILTERS);
        bool listed = Array.Exists(upper, IsOwnFilter);
        bool vendorLoaded = Array.Exists(stack, d => d.Equals(VendorHubFilterDriver, StringComparison.OrdinalIgnoreCase));
        bool ownLoaded = Array.Exists(stack, IsOwnFilterDriver);

        if (vendorLoaded || HubFilterFailed())
        {
            // usbip-win2's own install serves this hub, or HIDMaestro's
            // filter failed on it before. A second filter of the same code
            // would send each request to the host controller twice, so
            // HIDMaestro's leaves the list either way, and a hub that runs it
            // lets it go at its next start.
            if (listed)
            {
                string[] rest = Array.FindAll(upper, f => !IsOwnFilter(f));
                byte[] restValue = MultiSzToBytes(rest);
                int cr = CM_Set_DevNode_Registry_PropertyW(hub, CM_DRP_UPPERFILTERS, rest.Length == 0 ? null : restValue, rest.Length == 0 ? 0u : (uint)restValue.Length, 0);
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} {(vendorLoaded ? "runs usbip-win2's own filter" : "had HIDMaestro's filter fail")}; HIDMaestro's left its UpperFilters (CR {cr}).");
            }
            return vendorLoaded ? HubFilterState.Loaded : HubFilterState.Failed;
        }

        var service = EnsureHubFilterService();
        if (service == HubFilterServiceState.Unavailable) return HubFilterState.Unavailable;
        if (ownLoaded && service == HubFilterServiceState.Current && listed) return HubFilterState.Loaded;

        if (!listed)
        {
            var with = new string[upper.Length + 1];
            Array.Copy(upper, with, upper.Length);
            with[upper.Length] = HubFilterService;
            byte[] value = MultiSzToBytes(with);
            int cr = CM_Set_DevNode_Registry_PropertyW(hub, CM_DRP_UPPERFILTERS, value, (uint)value.Length, 0);
            if (cr != CR_SUCCESS)
            {
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} UpperFilters not written (CR {cr}).");
                return HubFilterState.NotChecked;
            }
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} lists {HubFilterService} in its UpperFilters.");
        }

        // The filter loads when the hub starts. Only a hub that carries no
        // device restarts here: every device on it is an import, and a
        // restart would take each one away from the program that holds it.
        // The restart's own thread checks again under the gate.
        int imports = VhciClient.GetImportedDevices().Count;
        if (CM_Get_Child(out _, hub, 0) == CR_SUCCESS || imports > 0)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} has devices attached ({imports} import(s)); the root hub filter loads at the hub's next start.");
            return HubFilterState.Waiting;
        }
        if (HubRestartTried(id))
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {id} had its restart tried this Windows session; the root hub filter loads at the hub's next start.");
            return HubFilterState.Waiting;
        }

        switch (Restart(hub, removeFirst: true, withFilter: true))
        {
            case HubRestart.Back:
                DeleteOlderHubFilterImages();
                return HubFilterState.Loaded;
            case HubRestart.Refused:
                // Windows refused the removal, and the hub runs as it was.
                return HubFilterState.Waiting;
            case HubRestart.NotBack:
                // A hub down with a driver failure loses the filter. One that
                // runs without it loads it at its next start.
                if (Down() is { } state) return state;
                return HubFilterState.Waiting;
            default:
                // A device or an attach came first, or the restart still
                // runs: the gate keeps attaches off the hub until it ends. A
                // later pass reads the outcome.
                return HubFilterState.NotChecked;
        }
    }

    private static bool IsOwnFilter(string service) => service.Equals(HubFilterService, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnFilterDriver(string driver) => driver.Equals(OwnHubFilterDriver, StringComparison.OrdinalIgnoreCase);

    /// <summary>The problem codes a hub shows when a driver in its stack
    /// failed: its image did not load, was blocked or is unsigned, its
    /// DriverEntry or AddDevice failed, its service is disabled or
    /// unreadable or its configuration in the registry is bad, or the stack
    /// failed the start or after it (cfg.h). A hub that is disabled, held for
    /// eject, waiting on a driver's earlier unload or on its own removal
    /// shows another.</summary>
    private static bool FilterFailure(uint problem) => problem is
        CM_PROB_FAILED_START or CM_PROB_REGISTRY or CM_PROB_FAILED_ADD or CM_PROB_DISABLED_SERVICE
        or CM_PROB_FAILED_DRIVER_ENTRY or CM_PROB_DRIVER_FAILED_LOAD or CM_PROB_DRIVER_SERVICE_KEY_INVALID
        or CM_PROB_FAILED_POST_START or CM_PROB_DRIVER_BLOCKED or CM_PROB_UNSIGNED_DRIVER;

    /// <summary>True when the filter's service exists and is not marked for
    /// deletion, and the image it names is on disk.</summary>
    private static bool HubFilterServiceIntact()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{HubFilterService}");
            if (k == null || (k.GetValue("DeleteFlag") is int flag && flag != 0)) return false;
            if (k.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image) return false;
            const string root = @"\SystemRoot\";
            string path = image.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), image[root.Length..])
                : image;
            return File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>True when the devnode is started with no problem.</summary>
    private static bool IsStarted(uint devInst) =>
        CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == CR_SUCCESS
        && (status & DN_STARTED) != 0 && problem == 0;

    /// <summary>True when the controller's root hub is in the tree and
    /// started, waiting up to five seconds for it to start.</summary>
    private static bool HubRunning(uint controller)
    {
        var sw = Stopwatch.StartNew();
        int budget = TimeoutScale.Apply(5_000);
        while (true)
        {
            if (CM_Get_Child(out uint hub, controller, 0) == CR_SUCCESS && IsStarted(hub)) return true;
            if (sw.ElapsedMilliseconds >= budget) return false;
            Thread.Sleep(100);
        }
    }

    private enum HubFilterServiceState { Current, Changed, Unavailable }

    /// <summary>The filter's image and service: Current when the image file
    /// is present at its pinned hash and the service points at it as an
    /// on-demand kernel driver, Changed when this call pointed the service
    /// at it, so a hub that loaded an earlier image runs that one until its
    /// next start, and Unavailable when the service is marked for deletion,
    /// which Windows finishes at its next start. Writes what is missing or
    /// different. Throws when the image cannot be written or the service
    /// cannot be created.</summary>
    private static HubFilterServiceState EnsureHubFilterService()
    {
        string image = HubFilterImagePath;
        if (!File.Exists(image) || !Sha256Of(image).Equals(PinnedHubFilterHash(), StringComparison.OrdinalIgnoreCase))
            WriteHubFilterImage(image);

        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero)
            throw new InvalidOperationException($"The service control manager could not be opened (Win32 {Marshal.GetLastPInvokeError()}).");
        try
        {
            IntPtr svc = CreateServiceW(scm, HubFilterService, "HIDMaestro USB/IP root hub filter",
                SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG, SERVICE_KERNEL_DRIVER, SERVICE_DEMAND_START,
                SERVICE_ERROR_NORMAL, HubFilterServiceBinary, null, IntPtr.Zero, null, null, null);
            if (svc != IntPtr.Zero)
            {
                CloseServiceHandle(svc);
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: service {HubFilterService} created for {HubFilterImageName}.");
                return HubFilterServiceState.Current;
            }
            int err = Marshal.GetLastPInvokeError();
            if (err == ERROR_SERVICE_MARKED_FOR_DELETE)
            {
                // A removal deleted it while a hub held it. Windows finishes
                // the deletion when it next starts, and a pass after that
                // creates it again.
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: service {HubFilterService} is marked for deletion until Windows next starts; the root hub filter waits.");
                return HubFilterServiceState.Unavailable;
            }
            if (err != ERROR_SERVICE_EXISTS)
                throw new InvalidOperationException($"The {HubFilterService} service could not be created (Win32 {err}).");

            // It exists: point it at the pinned image as an on-demand kernel
            // driver, whatever an earlier pin set.
            if (string.Equals(ServiceImagePath(HubFilterService), HubFilterServiceBinary, StringComparison.OrdinalIgnoreCase))
                return HubFilterServiceState.Current;
            svc = OpenServiceW(scm, HubFilterService, SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG);
            if (svc == IntPtr.Zero)
                throw new InvalidOperationException($"The {HubFilterService} service could not be opened (Win32 {Marshal.GetLastPInvokeError()}).");
            try
            {
                if (!ChangeServiceConfigW(svc, SERVICE_KERNEL_DRIVER, SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL,
                        HubFilterServiceBinary, null, IntPtr.Zero, null, null, null, null))
                    throw new InvalidOperationException($"The {HubFilterService} service could not be changed (Win32 {Marshal.GetLastPInvokeError()}).");
            }
            finally { CloseServiceHandle(svc); }
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: service {HubFilterService} now loads {HubFilterImageName}.");
            // A hub that loaded the earlier image runs it until its next
            // start.
            return HubFilterServiceState.Changed;
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Write the embedded filter for this machine's architecture to
    /// <paramref name="image"/> through a file beside it, checked against
    /// the pinned hash before it takes the name.</summary>
    private static void WriteHubFilterImage(string image)
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string logical = $"HIDMaestro.Usbip.{arch}.{HubFilterFile}";
        string partial = image + ".new";
        using (var src = typeof(UsbipDriverInstaller).Assembly.GetManifestResourceStream(logical)
                         ?? throw new InvalidOperationException($"Embedded resource '{logical}' is missing from HIDMaestro.Core.dll."))
        using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            src.CopyTo(dst);
        try
        {
            string got = Sha256Of(partial);
            if (!got.Equals(PinnedHubFilterHash(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The embedded {HubFilterFile} hashes {got}, not the pinned {PinnedHubFilterHash()}.");
            File.Move(partial, image, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: wrote {image}.");
    }

    /// <summary>Images an earlier pin wrote, once a hub runs the current one.
    /// One a hub still holds stays for a later pass.</summary>
    private static void DeleteOlderHubFilterImages()
    {
        try
        {
            string dir = Path.GetDirectoryName(HubFilterImagePath)!;
            foreach (string f in Directory.EnumerateFiles(dir, HubFilterService + "_*.sys"))
            {
                if (string.Equals(Path.GetFileName(f), HubFilterImageName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: deleted the earlier filter image {f}."); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ── For the removal ──────────────────────────────────────────────────

    /// <summary>The USB 3 root hubs, present or not, whose UpperFilters list
    /// HIDMaestro's filter.</summary>
    internal static List<string> HubsListingFilter()
    {
        var found = new List<string>();
        foreach (string id in DeviceIdList("USB"))
        {
            if (!id.StartsWith(@"USB\ROOT_HUB30\", StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint devInst, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) continue;
            if (Array.Exists(GetMultiSz(devInst, CM_DRP_UPPERFILTERS), f => f.Equals(HubFilterService, StringComparison.OrdinalIgnoreCase)))
                found.Add(id);
        }
        return found;
    }

    /// <summary>Take HIDMaestro's filter out of a hub's UpperFilters, under
    /// the PnP lock, as the removal's other devnode writes are. The hub runs
    /// without it from its next start. True when the list no longer names
    /// it.</summary>
    internal static bool UnlistHubFilter(string hubId)
    {
        using var pnp = DeviceManager.HoldPnpLock();
        if (CM_Locate_DevNodeW(out uint devInst, hubId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return false;
        string[] upper = GetMultiSz(devInst, CM_DRP_UPPERFILTERS);
        var rest = Array.FindAll(upper, f => !f.Equals(HubFilterService, StringComparison.OrdinalIgnoreCase));
        if (rest.Length == upper.Length) return true;
        byte[] value = MultiSzToBytes(rest);
        int cr = CM_Set_DevNode_Registry_PropertyW(devInst, CM_DRP_UPPERFILTERS, rest.Length == 0 ? null : value, rest.Length == 0 ? 0u : (uint)value.Length, 0);
        return cr == CR_SUCCESS
            && !Array.Exists(GetMultiSz(devInst, CM_DRP_UPPERFILTERS), f => f.Equals(HubFilterService, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every filter image HIDMaestro wrote, of any pin, and a
    /// partial write's file.</summary>
    internal static List<string> HubFilterImages()
    {
        var found = new List<string>();
        try
        {
            string dir = Path.GetDirectoryName(HubFilterImagePath)!;
            foreach (string pattern in new[] { HubFilterService + "_*.sys", HubFilterService + "_*.sys.new" })
                found.AddRange(Directory.EnumerateFiles(dir, pattern));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>The service's ImagePath, or null.</summary>
    internal static string? ServiceImagePath(string service)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
            return k?.GetValue("ImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch { return null; }
    }

    /// <summary>Each present host controller of the transport that has a
    /// driver: the devnodes whose root hubs carry the imports.</summary>
    private static List<uint> DrivenHostControllerNodes()
    {
        var found = new List<uint>();
        for (int n = 0; n < 16; n++)
        {
            if (CM_Locate_DevNodeW(out uint devInst, $"ROOT\\USB\\{n:D4}", CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
            bool isUde = false;
            foreach (var hwid in GetMultiSz(devInst, CM_DRP_HARDWAREID))
                if (hwid.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0) { isUde = true; break; }
            if (!isUde || string.IsNullOrEmpty(GetStringProperty(devInst, DEVPKEY_Device_DriverInfPath))) continue;
            found.Add(devInst);
        }
        return found;
    }

    /// <summary>The host controller's root hub, its one child. A host
    /// controller that just started publishes it a moment later, so this
    /// waits up to five seconds for it. 0 when none appears.</summary>
    private static uint WaitForRootHub(uint controller)
    {
        var sw = Stopwatch.StartNew();
        int budget = TimeoutScale.Apply(5_000);
        while (true)
        {
            if (CM_Get_Child(out uint hub, controller, 0) == CR_SUCCESS) return hub;
            if (sw.ElapsedMilliseconds >= budget) return 0;
            Thread.Sleep(100);
        }
    }

    /// <summary>True once the controller's root hub, the same devnode or a
    /// new one, is back within <paramref name="budgetMs"/>: with HIDMaestro's
    /// filter in its stack when <paramref name="withFilter"/>, started
    /// otherwise. Checked at least once.</summary>
    private static bool HubBack(uint controller, bool withFilter, int budgetMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (CM_Get_Child(out uint hub, controller, 0) == CR_SUCCESS
                && (withFilter ? Array.Exists(GetStackProperty(hub), IsOwnFilterDriver) : IsStarted(hub)))
                return true;
            if (sw.ElapsedMilliseconds >= budgetMs) return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>The devnode's instance id, or "?".</summary>
    private static string DeviceIdOf(uint devInst)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(devInst, buf, buf.Length, 0) == CR_SUCCESS ? new string(buf).TrimEnd('\0') : "?";
    }

    /// <summary>The drivers of the devnode's stack, top first
    /// (DEVPKEY_Device_Stack), empty when the devnode is not started.</summary>
    internal static string[] GetStackProperty(uint devInst)
    {
        var key = DEVPKEY_Device_Stack;
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buf, ref size, 0) != CR_SUCCESS) return Array.Empty<string>();
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    // devpkey.h: DEVPKEY_Device_Stack {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 14.
    private static readonly DEVPROPKEY DEVPKEY_Device_Stack =
        new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 14 };

    private const uint CM_DRP_UPPERFILTERS = 0x00000012; // cfgmgr32.h
    private const uint CM_REMOVE_UI_OK = 0x00000000;     // cfgmgr32.h

    private const uint SC_MANAGER_CONNECT = 0x0001;        // winsvc.h
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002; // winsvc.h
    private const uint SERVICE_QUERY_CONFIG = 0x0001;      // winsvc.h
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;     // winsvc.h
    private const uint SERVICE_KERNEL_DRIVER = 0x00000001; // winnt.h
    private const uint SERVICE_DEMAND_START = 0x00000003;  // winnt.h
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;  // winnt.h
    private const int ERROR_SERVICE_EXISTS = 1073;         // winerror.h
    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072; // winerror.h
    private const uint DN_STARTED = 0x00000008;            // cfgmgr32.h

    // shared/cfg.h, the problem codes FilterFailure and Down read.
    private const uint CM_PROB_FAILED_START = 0x0000000A;
    private const uint CM_PROB_REGISTRY = 0x00000013;
    private const uint CM_PROB_DISABLED = 0x00000016;
    private const uint CM_PROB_FAILED_ADD = 0x0000001F;
    private const uint CM_PROB_DISABLED_SERVICE = 0x00000020;
    private const uint CM_PROB_FAILED_DRIVER_ENTRY = 0x00000025;
    private const uint CM_PROB_DRIVER_FAILED_LOAD = 0x00000027;
    private const uint CM_PROB_DRIVER_SERVICE_KEY_INVALID = 0x00000028;
    private const uint CM_PROB_FAILED_POST_START = 0x0000002B;
    private const uint CM_PROB_DRIVER_BLOCKED = 0x00000030;
    private const uint CM_PROB_UNSIGNED_DRIVER = 0x00000034;

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Set_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, byte[]? Buffer, uint ulLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateServiceW(IntPtr hSCManager, string lpServiceName, string? lpDisplayName, uint dwDesiredAccess,
        uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string? lpLoadOrderGroup,
        IntPtr lpdwTagId, string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId, string? lpDependencies, string? lpServiceStartName,
        string? lpPassword, string? lpDisplayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr hSCObject);
}
