using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>WinUSB on one virtual USB/IP device whose profile declares
/// <c>usbConfiguration.winUsb</c>, so libusb hosts can open it with nothing
/// the device reports changed. docs/design/coexistence.md has the measurements
/// (E1 to E7), taken on the Wii U GameCube adapter, the first profile to use
/// it.
///
/// <para>Zadig binds WinUSB with <c>UpdateDriverForPlugAndPlayDevices</c> and
/// a hardware-ID INF (libwdi installer.c:876, 895), which takes every
/// matching device present and every later one. This binding takes one
/// devnode:</para>
/// <list type="bullet">
/// <item>Each profile's package has an INF that names no hardware ID any
/// device has, and matches the device's vendor-scoped compatible ID behind
/// fifteen IDs no device carries, so it ranks below the driver Windows gives
/// the device. A bind is refused when no other driver ranks above the
/// package, since Windows would then pick the package for a real device on
/// its own.</item>
/// <item><c>DiInstallDevice</c> puts it on the virtual device's devnode and
/// nowhere else.</item>
/// <item>The registry ledger records the devnode and its driver first, and
/// puts the devnode back once the last holder lets go and the devnode is
/// gone: it gets <c>CONFIGFLAG_REINSTALL</c>, so its next arrival, a real
/// device's included, installs again by rank. A restore installs no driver
/// on a present devnode, whichever device is on it. After a crash the next
/// program's reclaim does the same.</item>
/// </list>
/// <para>The bound devnode itself carries no reinstall flag. libusb reads
/// the flag as install state 1 and skips the device as one whose driver
/// "is reporting an issue" (windows_winusb.c, the DEV and EXT passes of
/// winusb_get_device_list), so no libusb host could open it. A program
/// killed while bound therefore leaves its instance on WinUSB, as Zadig
/// would, until a HIDMaestro program's reclaim, at its InstallDriver or
/// first create, releases its hold and the
/// devnode is gone.</para></summary>
internal static partial class WinUsbBinding
{
    /// <summary>The original INF names of HIDMaestro's WinUSB packages start
    /// with this, followed by the profile id.</summary>
    internal const string PackagePrefix = "hmwinusb_";
    internal const string Provider = "HIDMaestro";

    /// <summary>One profile's package: its file names, and the IDs and
    /// values its INF carries.</summary>
    internal sealed record Package(string InfName, string CatalogName, ushort VendorId, ushort ProductId,
                                   string CompatibleId, Guid InterfaceGuid, string DeviceName, string ProfileId)
    {
        /// <summary>The protected staging operation, one per profile.</summary>
        public string StagingOperation => "winusb-" + ProfileId;
    }

    /// <summary>The package for <paramref name="profile"/>, once its device
    /// is shown to take one: a device with one interface and no device
    /// class, and a GUID to register. Null when the profile declares no
    /// winUsb.</summary>
    internal static Package? PackageFor(ControllerProfile profile, Usbip.UsbDescriptorSet d)
    {
        var spec = profile.UsbConfiguration?.WinUsb;
        if (spec == null) return null;
        string at = $"Profile '{profile.Id}': usbConfiguration.winUsb";
        if (!profile.RequiresUsbipBackend)
            throw new InvalidOperationException($"{at} applies to the usbip backend only.");
        if (!Guid.TryParse(spec.InterfaceGuid, out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException($"{at}.interfaceGuid \"{spec.InterfaceGuid}\" is not a GUID.");
        foreach (char c in profile.Id)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                throw new InvalidOperationException($"{at}: the profile id names the package, so it may hold only a-z, 0-9 and '-'.");
        }
        // WinUSB goes on the device's own devnode, which a device with one
        // interface and no device class has: Windows loads no composite
        // driver for it, and its compatible IDs come from that interface
        // (Microsoft Learn, "Standard USB identifiers").
        if (d.DeviceDescriptor[4] != 0 || d.NumInterfaces != 1)
            throw new InvalidOperationException(
                $"{at} needs a device with one interface and bDeviceClass 0. This one has {d.NumInterfaces} " +
                $"interfaces and bDeviceClass 0x{d.DeviceDescriptor[4]:X2}.");
        var (cls, sub, prot) = d.FirstInterfaceClass;
        string compat = $@"USB\COMPAT_VID_{d.VendorId:X4}&Class_{cls:X2}&SubClass_{sub:X2}&Prot_{prot:X2}";
        string name = PackagePrefix + profile.Id;
        string deviceName = !string.IsNullOrEmpty(profile.ProductString) ? profile.ProductString! : profile.Name;
        return new Package(name + ".inf", name + ".cat", d.VendorId, d.ProductId, compat, guid,
                           deviceName, profile.Id);
    }

    /// <summary>The INF, built from Microsoft's WinUSB template: Include and
    /// Needs of winusb.inf and the interface GUID. The first ID on the model
    /// line is its hardware ID and matches nothing. The compatible ID that
    /// does match comes sixteenth among the compatible IDs. DriverVer changes
    /// only when this text does.</summary>
    internal static string InfText(Package p)
    {
        var ids = new StringBuilder($@"USB\VID_{p.VendorId:X4}&PID_{p.ProductId:X4}&HIDMAESTRO_WINUSB");
        for (int i = 1; i <= 15; i++) ids.Append($@", USB\HIDMAESTRO_WINUSB_RANK_{i:D2}");
        ids.Append(", ").Append(p.CompatibleId);
        string line = $"%DeviceName% = Install, {ids}";
        // An INF string doubles a quote to carry one.
        string deviceName = p.DeviceName.Replace("\"", "\"\"", StringComparison.Ordinal);
        string guid = p.InterfaceGuid.ToString().ToUpperInvariant();
        return
$@";
; HIDMaestro: WinUSB for one virtual device of profile {p.ProfileId}.
; No device's hardware ID matches, and the one compatible ID that does comes
; after fifteen that match nothing, so the package ranks below the driver
; Windows gives the device and is never installed on a device by itself.
; HIDMaestro installs it on its own virtual device's devnode with
; DiInstallDevice.
;

[Version]
Signature   = ""$Windows NT$""
Class       = USBDevice
ClassGuid   = {{88BAE032-5A81-49f0-BC3D-A4FF138216D6}}
Provider    = %Provider%
CatalogFile = {p.CatalogName}
DriverVer   = 10/14/2026,1.13.0.0
PnpLockdown = 1

[Manufacturer]
%Provider% = Models,NTamd64,NTarm64

[Models.NTamd64]
{line}

[Models.NTarm64]
{line}

[Install]
Include = winusb.inf
Needs   = WINUSB.NT

[Install.Services]
Include = winusb.inf
Needs   = WINUSB.NT.Services

[Install.HW]
AddReg = DeviceAddReg

[DeviceAddReg]
HKR,,DeviceInterfaceGUIDs,0x10000,""{{{guid}}}""

[Strings]
Provider   = ""{Provider}""
DeviceName = ""{deviceName}""
";
    }

    // Guards s_published and each controller's keeper creation. No lock is
    // taken under it.
    private static readonly object s_lock = new();
    // The controllers with a keeper not yet stopped. Guarded by s_lock.
    private static readonly HashSet<HMController> s_keeping = new();
    // Published name by original INF name, for the packages this process
    // added. Guarded by s_lock.
    private static readonly Dictionary<string, string> s_published = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name, in <see cref="AdminNamespace"/>, of the
    /// machine-wide lock over the devnodes a binding changes.</summary>
    internal const string DevnodeMutexObject = "WinUsb";

    /// <summary>How long a claim waits for the registry ledger's lock while
    /// it holds the devnode lock and the keeper's gate.</summary>
    private const int ClaimTimeoutMs = 30_000;

    /// <summary>The version of the rule that keeps every driver and setup
    /// class change off a devnode that is leaving or gone: 5 since a
    /// restore never installs a driver on a present devnode, and only marks
    /// one for reinstall once it is gone. 6 since no cleanup removes a
    /// devnode and a process exit stops its re-binds before it detaches. The
    /// probes that bind and dispose a real adapter refuse to run on a build
    /// below the version they were written for, so a run of old code cannot
    /// change a departing devnode.</summary>
    internal const int DepartingDevnodeRule = 6;

    /// <summary>Take the machine-wide lock over the devnodes a binding
    /// changes, held by the calling thread until it calls
    /// <c>ReleaseMutex</c>. A bind's ledger claim and install, a restore and
    /// the uninstall-grade cleanup each run under it, so a restore that finds
    /// an entry unheld finishes before any bind can claim the devnode.
    ///
    /// <para>Lock order: the deploy lock, then this one, then a controller's
    /// keeper gate, then the registry ledger's. A bind adds its package under
    /// the deploy lock before it takes this one, never inside it.</para></summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static Mutex AcquireDevnodeLock() => AcquireUnlessStopped(DevnodeMutexObject, 120_000, null,
        "Another program held HIDMaestro's machine-wide WinUSB lock for two minutes.");

    /// <summary>Take a mutex of <see cref="AdminNamespace"/> for up to
    /// <paramref name="timeoutMs"/>, giving up once
    /// <paramref name="keeper"/>'s controller is disposed. An abandoned mutex
    /// is taken.</summary>
    private static Mutex AcquireUnlessStopped(string objectName, int timeoutMs, Keeper? keeper, string timeoutMessage)
    {
        var m = new Mutex(false, AdminNamespace.Name(objectName));
        try
        {
            var until = Environment.TickCount64 + TimeoutScale.Apply(timeoutMs);
            while (true)
            {
                try { if (m.WaitOne(250)) return m; }
                catch (AbandonedMutexException) { return m; }
                ThrowIfStopped(keeper);
                if (Environment.TickCount64 >= until) throw new InvalidOperationException(timeoutMessage);
            }
        }
        catch
        {
            m.Dispose();
            throw;
        }
    }

    /// <summary>A controller's binding, kept across re-imports of its
    /// device. <see cref="Gate"/> covers the claim and install of each bind
    /// and the dispose, never a wait.</summary>
    internal sealed class Keeper
    {
        internal readonly object Gate = new();
        // The devnode the controller holds in the ledger, under Gate.
        internal string? InstanceId;
        // Set at dispose, under Gate. Read without it by the waits, which
        // give up once it is set.
        internal volatile bool Stopped;
        // Where the program's BindWinUsb call stands, under Gate.
        internal BindPhase Phase;
        // A re-import came while that call ran, under Gate.
        internal bool Missed;
    }

    /// <summary>Binding: the program's BindWinUsb call runs, and a re-import
    /// meanwhile is bound once it returns. Kept: it returned, and each
    /// re-import is bound. Refused: it threw, and no re-import is bound,
    /// since the program was told nothing was.</summary>
    internal enum BindPhase { None, Binding, Kept, Refused }

    private static void ThrowIfStopped(Keeper? keeper)
    {
        if (keeper is { Stopped: true }) throw new ObjectDisposedException(nameof(HMController));
    }

    /// <summary>Bind WinUSB to the controller's virtual device, and bind it
    /// again whenever usbip-win2 imports the device again after a drop. The
    /// import that comes back can be a new devnode on input.inf: another
    /// device can take the instance the binding was on while the host
    /// controller waits to re-attach. A call that throws binds nothing later
    /// either: no re-import is bound until the program calls again.</summary>
    internal static HMWinUsbBinding Bind(HMController controller)
    {
        var handle = controller.UsbipHandle
            ?? throw new InvalidOperationException(
                $"Profile '{controller.Profile.Id}' runs on the UMDF2 driver. A WinUSB binding is for a USB/IP device.");
        Keeper keeper;
        lock (s_lock)
        {
            keeper = controller.WinUsbKeeper ??= new Keeper();
            s_keeping.Add(controller);
        }
        lock (keeper.Gate)
        {
            ThrowIfStopped(keeper);
            keeper.Phase = BindPhase.Binding;
            keeper.Missed = false;
        }
        // Set before the bind, so a drop and re-import while it runs is
        // bound once it returns. The server's port tracker reports each port
        // on a thread that holds the device's port lock, so the bind runs
        // elsewhere.
        handle.Device.Reattached = _ => QueueRebind(controller, handle, keeper);
        HMWinUsbBinding result;
        try { result = BindKept(controller, handle, keeper, rebind: false); }
        catch
        {
            handle.Device.Reattached = null;
            lock (keeper.Gate) keeper.Phase = BindPhase.Refused;
            throw;
        }
        bool missed;
        lock (keeper.Gate)
        {
            keeper.Phase = BindPhase.Kept;
            missed = keeper.Missed;
        }
        if (missed) QueueRebind(controller, handle, keeper);
        return result;
    }

    private static void QueueRebind(HMController controller, Usbip.UsbipBackendHandle handle, Keeper keeper) =>
        ThreadPool.UnsafeQueueUserWorkItem(_ => Rebind(controller, handle, keeper), null);

    /// <summary>Stop binding the controller's device again. Called at its
    /// dispose before the ledger releases its holds. It waits only for a
    /// claim and install in progress, so no claim lands after the release.
    /// A bind waiting for a devnode or a lock gives up.</summary>
    internal static void StopKeeping(HMController controller)
    {
        Keeper? keeper;
        lock (s_lock)
        {
            keeper = controller.WinUsbKeeper;
            s_keeping.Remove(controller);
        }
        if (keeper == null) return;
        if (controller.UsbipHandle is { } handle) handle.Device.Reattached = null;
        lock (keeper.Gate) keeper.Stopped = true;
    }

    /// <summary>Stop every keeper of this process, as each one's dispose
    /// would. The process-exit cleanup calls this before it detaches the
    /// imports, so no re-bind on a pool thread is inside its claim and
    /// install when the device leaves: each stop waits for one in progress
    /// and keeps any later one from starting.</summary>
    internal static void StopKeepingAll()
    {
        HMController[] all;
        lock (s_lock) all = s_keeping.ToArray();
        foreach (var c in all)
            try { StopKeeping(c); } catch { /* the exit goes on */ }
    }

    private static void Rebind(HMController controller, Usbip.UsbipBackendHandle handle, Keeper keeper)
    {
        lock (keeper.Gate)
        {
            if (keeper.Stopped || keeper.Phase is BindPhase.None or BindPhase.Refused) return;
            if (keeper.Phase == BindPhase.Binding)
            {
                keeper.Missed = true;
                return;
            }
        }
        try
        {
            var result = BindKept(controller, handle, keeper, rebind: true);
            DeviceOrchestrator.LogDiag($"    winusb: binding after a re-import: {result}");
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"    winusb: binding again after a re-import failed: {ex.GetType().Name}: {ex.Message}");
            // The device came back somewhere this bind did not reach. A hold
            // on a devnode that is not the import's any more goes, so the
            // devnode is put back once nothing holds it.
            string? left;
            lock (keeper.Gate)
            {
                left = keeper.InstanceId;
                if (left == null || ImportIsOn(handle, left)) return;
                keeper.InstanceId = null;
            }
            try { RegistryLedger.ReleaseWinUsb(left, controller.Index); }
            catch (Exception rex) { DeviceOrchestrator.LogDiag($"    winusb: releasing {left} failed: {rex.Message}"); }
        }
    }

    /// <summary>Bind, then release the controller's hold on a devnode its
    /// device left, with no lock held.</summary>
    private static HMWinUsbBinding BindKept(HMController controller, Usbip.UsbipBackendHandle handle, Keeper keeper, bool rebind)
    {
        var result = BindDevnode(controller, handle, keeper, rebind, out string? left);
        if (left != null) RegistryLedger.ReleaseWinUsb(left, controller.Index);
        return result;
    }

    /// <summary>Bind WinUSB to the devnode of the controller's import.
    /// <paramref name="left"/> is the devnode the controller held before,
    /// when the bind found another, so the caller releases it.
    ///
    /// <para>No driver or class changes on a devnode that is leaving or gone.
    /// Under the devnode lock, before the claim, the devnode must be the
    /// import's (<see cref="IsImportsDevnode"/>): present and not being
    /// removed, settled on its driver, the only devnode at its address, and
    /// still the one at the address the import's location gives. After the
    /// claim and the driver list, right before <c>DiInstallDevice</c>, it
    /// must still be the import's, on the same arrival. A devnode that fails
    /// either check is looked for again, with the claim taken back, until the
    /// device's devnode is found or 10 s pass, counted across every look,
    /// plus the waits for one devnode to settle and for the locks. A re-bind
    /// (<paramref name="rebind"/>) gives up when the program has called
    /// BindWinUsb again or its call threw.</para></summary>
    private static HMWinUsbBinding BindDevnode(HMController controller, Usbip.UsbipBackendHandle handle, Keeper keeper,
                                               bool rebind, out string? left)
    {
        left = null;
        var d = handle.Device.Descriptors;
        var package = PackageFor(controller.Profile.Inner, d)
            ?? throw new InvalidOperationException(
                $"Profile '{controller.Profile.Id}' declares no usbConfiguration.winUsb, so its device takes no WinUSB binding.");
        string? published = null;
        var until = Environment.TickCount64 + TimeoutScale.Apply(10_000);
        string instanceId;
        while (true)
        {
            int remaining = (int)Math.Max(0, until - Environment.TickCount64);
            instanceId = WaitForDevnode(handle, d.VendorId, d.ProductId, remaining, keeper)
                ?? throw new InvalidOperationException(
                    $"The device at 127.0.0.1:{handle.ServerPort} bus id {handle.Device.BusId} did not appear within 10 s.");
            WaitInstalled(instanceId, TimeoutScale.Apply(10_000), keeper);

            bool needPackage = false, retry = false;
            using (var devnodes = AcquireUnlessStopped(DevnodeMutexObject, 120_000, keeper,
                       "Another program held HIDMaestro's machine-wide WinUSB lock for two minutes."))
            {
                try
                {
                    if (!IsImportsDevnode(handle, instanceId, d.VendorId, d.ProductId))
                    {
                        if (Environment.TickCount64 >= until)
                            throw new InvalidOperationException(
                                $"The device at 127.0.0.1:{handle.ServerPort} bus id {handle.Device.BusId} left {instanceId} and was not found again within 10 s.");
                        retry = true;
                    }
                    else
                    {
                        var (service, inf) = DriverOf(instanceId, phantom: false);
                        HMWinUsbBinding? done = null;
                        bool ours = false;
                        if (IsLibusbService(service)) { done = HMWinUsbBinding.AlreadyBound; ours = IsOurPackage(inf) == true; }
                        else if (!IsInboxOrNone(inf)) done = HMWinUsbBinding.OtherDriver;
                        else if (published == null || !StillPublished(package, published))
                        {
                            // A package a cleanup removed since it was added
                            // (or since this process cached it) is added
                            // again. Checked under the devnode lock, which
                            // the removal holds too.
                            published = null;
                            needPackage = true;
                        }
                        if (!needPackage)
                        {
                            lock (keeper.Gate)
                            {
                                ThrowIfStopped(keeper);
                                if (rebind && keeper.Phase != BindPhase.Kept)
                                    throw new OperationCanceledException("The program's BindWinUsb call is running again or threw.");
                                long? arrival = ArrivalOf(instanceId);
                                if (done == null && PresenceOf(instanceId) != Presence.Present)
                                {
                                    // Left since the check above: looked for
                                    // again, and nothing changes on it.
                                    retry = true;
                                }
                                else if (done == null)
                                {
                                    // Recorded before the devnode changes, so a
                                    // crash between the two leaves a record whose
                                    // restore finds the devnode as it was.
                                    RegistryLedger.ClaimWinUsb(instanceId, service, inf, controller.Index, ClaimTimeoutMs);
                                    if (!InstallOn(instanceId, published!,
                                            () => ArrivalOf(instanceId) == arrival
                                                  && IsImportsDevnode(handle, instanceId, d.VendorId, d.ProductId)))
                                    {
                                        // It left between the claim and the
                                        // install, and nothing changed on it.
                                        RegistryLedger.UnclaimWinUsb(instanceId, controller.Index, ClaimTimeoutMs);
                                        retry = true;
                                    }
                                }
                                else if (ours)
                                {
                                    // On one of HIDMaestro's own packages, which a
                                    // killed program's binding leaves until a sweep
                                    // puts the devnode back. This controller holds
                                    // it from here, so no sweep moves it while the
                                    // controller lives.
                                    RegistryLedger.ClaimWinUsb(instanceId, service, inf, controller.Index, ClaimTimeoutMs);
                                }
                                string? held = retry ? null : keeper.InstanceId;
                                bool holds = !retry && done is null or HMWinUsbBinding.AlreadyBound;
                                if (holds) keeper.InstanceId = instanceId;
                                if (held != null && !held.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    left = held;
                                    if (!holds) keeper.InstanceId = null;
                                }
                            }
                            if (done != null) return done.Value;
                        }
                    }
                }
                finally { devnodes.ReleaseMutex(); }
            }
            if (retry)
            {
                if (Environment.TickCount64 >= until)
                    throw new InvalidOperationException(
                        $"The device at 127.0.0.1:{handle.ServerPort} bus id {handle.Device.BusId} did not stay on {instanceId} long enough to bind within 10 s.");
                Thread.Sleep(50);
                continue;
            }
            if (!needPackage) break;
            // The package goes into the driver store under the deploy lock
            // with the devnode lock released, since the first add takes
            // seconds. The devnode is found and checked again after.
            published = EnsurePackage(package, keeper);
        }

        // WinUSB is bound once the devnode reports its service and the
        // interface libusb opens.
        var started = Environment.TickCount64 + TimeoutScale.Apply(10_000);
        while (Environment.TickCount64 < started)
        {
            var (s, _) = DriverOf(instanceId, phantom: false);
            if (string.Equals(s, "WINUSB", StringComparison.OrdinalIgnoreCase) && IsStarted(instanceId))
                return HMWinUsbBinding.Bound;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException($"WinUSB did not start on {instanceId} within 10 s.");
    }

    // ── Presence ─────────────────────────────────────────────────────────
    //
    // Whether a devnode is there, staying, or gone is decided by the checks
    // in this section and nowhere else. A bind asks IsImportsDevnode, which
    // asks PresenceOf. A restore asks HasLeft only. A better signal of a
    // devnode having finished leaving goes into HasLeft.

    /// <summary>True when <paramref name="instanceId"/> is the devnode of
    /// the controller's import now: present and not being removed, settled
    /// on its driver, the one devnode at the address of the vhci port the
    /// import's location is on, and that location still on the port.</summary>
    private static bool IsImportsDevnode(Usbip.UsbipBackendHandle handle, string instanceId, ushort vendorId, ushort productId)
    {
        int port = Usbip.VhciClient.FindLocation("127.0.0.1", handle.ServerPort, handle.Device.BusId);
        return port > 0
            && PresenceOf(instanceId) == Presence.Present
            && IsSettled(instanceId)
            && string.Equals(FindDevnode(port, vendorId, productId), instanceId, StringComparison.OrdinalIgnoreCase)
            && Usbip.VhciClient.FindLocation("127.0.0.1", handle.ServerPort, handle.Device.BusId) == port;
    }

    /// <summary>True when the controller's import is on
    /// <paramref name="instanceId"/>.</summary>
    private static bool ImportIsOn(Usbip.UsbipBackendHandle handle, string instanceId)
    {
        int port = Usbip.VhciClient.FindLocation("127.0.0.1", handle.ServerPort, handle.Device.BusId);
        var d = handle.Device.Descriptors;
        return port > 0 && string.Equals(FindDevnode(port, d.VendorId, d.ProductId), instanceId, StringComparison.OrdinalIgnoreCase);
    }

    private enum Presence { Absent, Leaving, Present }

    /// <summary>Whether a devnode is present and not on its way out, for a
    /// bind, which pairs it with the import's own location
    /// (<see cref="IsImportsDevnode"/>). Leaving: the PnP manager marks it for
    /// removal (DN_WILL_BE_REMOVED, problem 21 or 24 in cfg.h), or it hangs
    /// off usbip-win2's root hub and no import holds the port at its address,
    /// or another present devnode there shares the address. usbip-win2 frees
    /// the port before it plugs the device out (drivers/ude/device.cpp,
    /// detach: reclaim_roothub_port, then plugout_and_delete), and the
    /// plug-out can lag (its FIXME there). Another import can take the freed
    /// port, so this says nothing about whose device the devnode is. A
    /// restore does not use it.</summary>
    private static Presence PresenceOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return Presence.Absent;
        if (CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) != CR_SUCCESS) return Presence.Leaving;
        if ((status & DN_WILL_BE_REMOVED) != 0) return Presence.Leaving;
        if ((status & DN_HAS_PROBLEM) != 0 && problem is CM_PROB_WILL_BE_REMOVED or CM_PROB_DEVICE_NOT_THERE) return Presence.Leaving;
        if (!OnUsbipRootHub(dev)) return Presence.Present;
        if (ReadDword(dev, CM_DRP_ADDRESS) is not uint address) return Presence.Leaving;
        if (!Usbip.VhciClient.GetImportedDevices().Any(r => r.Port == (int)address)) return Presence.Leaving;
        return UsbipDevnodesAt(address) == 1 ? Presence.Present : Presence.Leaving;
    }

    private static bool OnUsbipRootHub(uint dev) =>
        CM_Get_Parent(out uint hub, dev, 0) == CR_SUCCESS
        && CM_Get_Parent(out uint host, hub, 0) == CR_SUCCESS
        && ReadMultiSz(host, CM_DRP_HARDWAREID).Any(h => h.Contains("USBIP_WIN2", StringComparison.OrdinalIgnoreCase));

    /// <summary>How many present devnodes on usbip-win2's root hub have this
    /// address.</summary>
    private static int UsbipDevnodesAt(uint address)
    {
        int n = 0;
        foreach (string id in PresentDeviceIds("USB"))
        {
            if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
            if (ReadDword(dev, CM_DRP_ADDRESS) == address && OnUsbipRootHub(dev)) n++;
        }
        return n;
    }

    /// <summary>True when the devnode has left: Microsoft Learn's test for
    /// an unplugged device's phantom devnode ("Determining Whether a Device
    /// is Plugged In", step 4), CM_Get_DevNode_Status on it returning
    /// CR_NO_SUCH_DEVINST. A devnode that is present, or has no record at
    /// all, has not.</summary>
    internal static bool HasLeft(string instanceId)
    {
        if (CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS) return false;
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return false;
        return CM_Get_DevNode_Status(out _, out _, dev, 0) == CR_NO_SUCH_DEVINST;
    }

    /// <summary>When the present devnode last arrived
    /// (DEVPKEY_Device_LastArrivalDate, devpkey.h, a FILETIME), or null when
    /// it is not present or has no arrival recorded.</summary>
    private static long? ArrivalOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;
        var key = DEVPKEY_Device_LastArrivalDate;
        var buf = new byte[8];
        uint len = 8;
        return CM_Get_DevNode_PropertyW(dev, ref key, out uint type, buf, ref len, 0) == CR_SUCCESS
               && type == DEVPROP_TYPE_FILETIME && len == 8
            ? BitConverter.ToInt64(buf) : null;
    }

    /// <summary>Wait up to <paramref name="waitGoneMs"/> for a devnode whose
    /// device was just detached to leave. No lock is held.</summary>
    internal static void WaitGone(string instanceId, int waitGoneMs)
    {
        var until = Environment.TickCount64 + waitGoneMs;
        while (waitGoneMs > 0 && Environment.TickCount64 < until
               && CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
            Thread.Sleep(20);
    }

    // ── Putting a devnode back ───────────────────────────────────────────

    /// <summary>Put a devnode the ledger recorded back on the driver Windows
    /// picks by rank, once it is gone. True when the entry is done, false
    /// when it stays for a later sweep. The caller holds the devnode lock
    /// (<see cref="AcquireDevnodeLock"/>).
    ///
    /// <para>A devnode on a driver other than HIDMaestro's WinUSB packages is
    /// someone else's and is left alone. When its driver cannot be told, as
    /// when pnputil fails, nothing changes and the entry stays.</para>
    ///
    /// <para>A devnode that has left (<see cref="HasLeft"/>) gets
    /// <c>CONFIGFLAG_REINSTALL</c>, which makes Plug and Play install it
    /// again, by rank, the next time it is enumerated (Microsoft Learn,
    /// "Determining Whether a Device is Plugged In", steps 4 and 5: the flag
    /// "causes the device to be reinstalled the next time that the device is
    /// reenumerated").</para>
    ///
    /// <para>Nothing is ever installed on a present devnode, whoever's device
    /// it is: the device bound, on its way out, or another that arrived on
    /// the instance since. Another program's dispose, a kill or a dropped
    /// import can start its removal at any moment, and nothing the restore
    /// holds stops it, so no check made before <c>DiInstallDevice</c> could
    /// hold while it runs. A present devnode keeps its driver and its entry
    /// until a sweep finds it gone, and never gets the flag while
    /// present.</para></summary>
    internal static bool RestoreLocked(string instanceId)
    {
        if (CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
        {
            // Present: nothing is written, so its driver is read without
            // pnputil. One that ships with Windows is no package of
            // HIDMaestro's and the entry is done. Any other keeps it, and so
            // does a driver that cannot be read, as when the devnode left
            // between the two reads: the entry stays for the look that
            // finds it gone and flags it.
            var (_, current) = DriverOf(instanceId, phantom: false);
            return current != null && IsInboxOrNone(current);
        }
        var (_, inf) = DriverOf(instanceId, phantom: true);
        switch (IsOurPackage(inf))
        {
            case false:
                return true;
            case null:
                DeviceOrchestrator.LogDiag($"    winusb restore of {instanceId}: the driver store could not be read");
                return false;
        }
        // Read after the package check, which can run pnputil for seconds.
        return HasLeft(instanceId) && FlagForNextArrival(instanceId);
    }

    /// <summary>True when Windows has a record of the devnode, present or
    /// not.</summary>
    internal static bool HasRecord(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS;

    /// <summary>The devnode lock when no one holds it, or null, with no
    /// wait.</summary>
    internal static Mutex? TryAcquireDevnodeLock()
    {
        var m = new Mutex(false, AdminNamespace.Name(DevnodeMutexObject));
        try
        {
            if (m.WaitOne(0)) return m;
        }
        catch (AbandonedMutexException) { return m; }
        catch
        {
            m.Dispose();
            throw;
        }
        m.Dispose();
        return null;
    }

    /// <summary>Set <c>CONFIGFLAG_REINSTALL</c> on a devnode
    /// <see cref="HasLeft"/> found gone, then look again. A devnode that
    /// arrived in between gets its old flags back. True when the flag is on
    /// a gone devnode.</summary>
    private static bool FlagForNextArrival(string instanceId)
    {
        // A failed read writes nothing: a 0 written back would drop every
        // other flag, CONFIGFLAG_DISABLED among them. The entry stays.
        if (ConfigFlagsOf(instanceId) is not uint before) return false;
        if ((before & CONFIGFLAG_REINSTALL) == 0) WriteConfigFlags(instanceId, before | CONFIGFLAG_REINSTALL);
        if (HasLeft(instanceId)) return true;
        // It arrived in between. Only the bit this set is taken back, from
        // the flags as they are now, since the arrival's install can have
        // written them since.
        if ((before & CONFIGFLAG_REINSTALL) == 0 && ConfigFlagsOf(instanceId) is uint now && (now & CONFIGFLAG_REINSTALL) != 0)
        {
            try { WriteConfigFlags(instanceId, now & ~CONFIGFLAG_REINSTALL); }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    winusb: the reinstall flag of {instanceId} not taken back: {ex.Message}"); }
        }
        return false;
    }

    /// <summary>The uninstall-grade cleanup: the packages leave the driver
    /// store, except one a devnode still uses
    /// (<see cref="RegistryLedger.NoteWinUsbPackagesLeft"/>). No devnode is
    /// removed or installed. A devnode on a package that has left
    /// (<see cref="HasLeft"/>) gets the reinstall flag, which installs it by
    /// rank at its next arrival, and its package stays, since a devnode that
    /// is not present keeps a package in use. Removing that devnode instead
    /// would race a real adapter of that instance arriving and starting its
    /// install after the check, which nothing here can hold back. Nothing is
    /// written to a devnode still present, which can be a device on its way
    /// out. Nothing else is touched: a package's provider and original name
    /// identify it. The caller holds the deploy lock.
    ///
    /// <para>The <paramref name="deferred"/> run, once the last WinUSB entry
    /// is done, removes the packages that cleanup's note names, the same
    /// way. It reads the note again here under the deploy and devnode
    /// locks, since a program that adds a package takes its name out of the
    /// note under the deploy lock. A package a gone devnode keeps stays in
    /// the driver store, ranked below the driver Windows gives the device,
    /// until a cleanup, or the first sweep of a later program with no
    /// WinUSB entry, finds no devnode on it.</para></summary>
    internal static void RemovePackage(bool deferred = false)
    {
        using var devnodes = AcquireDevnodeLock();
        try { RemoveUnderLocks(deferred); }
        finally { devnodes.ReleaseMutex(); }
    }

    /// <summary><see cref="RemovePackage"/>'s work, under its locks: the
    /// note it reads, the listing, the walk, the flags, the deletes and the
    /// note it writes. <paramref name="readNoted"/>, <paramref name="list"/>,
    /// <paramref name="walk"/>, <paramref name="hasLeft"/>,
    /// <paramref name="flag"/> and <paramref name="delete"/> stand in for
    /// the ledger's note, pnputil's listing, <see cref="DevnodesByPackage"/>,
    /// <see cref="HasLeft"/>, <see cref="FlagForNextArrival"/> and pnputil's
    /// plain delete in registry_ownership_check's offline part, which checks
    /// what this deletes, flags and notes. RemovePackage passes none.</summary>
    internal static void RemoveUnderLocks(bool deferred,
        Func<string[]>? readNoted = null,
        Func<List<PnputilHelper.DriverRecord>?>? list = null,
        Func<List<PnputilHelper.DriverRecord>, Dictionary<string, List<string>>?>? walk = null,
        Func<string, bool>? hasLeft = null,
        Action<string>? flag = null,
        Func<string, (bool Deleted, string Error)>? delete = null)
    {
        string[] noted = Array.Empty<string>();
        if (deferred)
        {
            noted = (readNoted ?? RegistryLedger.WinUsbPackagesLeftToRemove)();
            if (noted.Length == 0) return;
        }
        var (decided, undecided) = DecideRemoval((list ?? (() => PnputilHelper.TryEnumerateDrivers()))(), deferred, noted, walk);
        if (decided is not { } plan)
        {
            DeviceOrchestrator.LogDiag("    winusb cleanup: the driver store or its USB devnodes could not be listed; the packages stay");
            if (undecided is { } note) RegistryLedger.NoteWinUsbPackagesLeft(note);
            return;
        }
        foreach (var (published, ids) in plan.On)
            foreach (string id in ids)
            {
                try
                {
                    if ((hasLeft ?? HasLeft)(id)) (flag ?? (i => FlagForNextArrival(i)))(id);
                    DeviceOrchestrator.LogDiag($"    winusb cleanup: {id} keeps its package {published}");
                }
                catch (Exception ex) { DeviceOrchestrator.LogDiag($"    winusb cleanup of {id} threw {ex.Message}"); }
            }
        var kept = new List<string>(plan.Keep);
        foreach (var p in plan.Delete)
        {
            var (deleted, err) = (delete ?? (published => DeletePlain(published)))(p.PublishedName);
            if (!deleted)
            {
                DeviceOrchestrator.LogDiag($"    winusb package {p.PublishedName} not deleted: {err}");
                kept.Add(p.OriginalName);
            }
            else lock (s_lock) s_published.Remove(p.OriginalName);
        }
        // A package kept for a devnode goes once the restore of the last
        // entry is done (RegistryLedger.RestoreWinUsbEntries), or at the
        // first sweep of a later program with no WinUSB entry
        // (RegistryLedger.PruneDead), if no devnode keeps it then.
        RegistryLedger.NoteWinUsbPackagesLeft(NoteAfterRemoval(deferred, noted, kept));
    }

    /// <summary>pnputil's plain delete of one package and its answer:
    /// deleted only on exit code 0 with no failure in its output
    /// (<see cref="PnputilHelper.PlainDeleteDone"/>). <paramref name="run"/>
    /// stands in for pnputil in registry_ownership_check's offline part,
    /// which checks the command and how each answer reads.</summary>
    internal static (bool Deleted, string Error) DeletePlain(string published,
        Func<string, (int ExitCode, string Output)>? run = null)
    {
        var (rc, output) = (run ?? (args => PnputilHelper.Run(args, timeoutMs: 30_000)))($"/delete-driver {published}");
        return (PnputilHelper.PlainDeleteDone(rc, output), output.Trim());
    }

    /// <summary>A removal's decision from the driver store's listing and one
    /// walk of the USB devnodes: HIDMaestro's WinUSB packages in the store,
    /// the ones to delete, which no devnode uses, and the ones to keep. The
    /// deferred run looks only at the packages it <paramref name="noted"/>.
    /// Null when the <paramref name="store"/> could not be listed, or the
    /// devnodes could not all be read, since neither is an empty one:
    /// nothing is removed, and <see cref="DecideRemoval"/> says what the
    /// note becomes.
    ///
    /// <para>A package goes to delete only when the whole walk found no
    /// devnode on it, so the plain delete is never asked to take a package a
    /// devnode uses. Learn documents that check for the setup API's delete,
    /// not for pnputil's: without its force flag SetupUninstallOEMInf keeps
    /// an INF a device was installed with, and "A device does not need to
    /// be present to be detected as using the .inf file"
    /// (SetupUninstallOEMInfW, SUOI_FORCEDELETE). PnPUtil's page says only
    /// that /force deletes a package "even when it is in use by devices",
    /// and maps the old -d [-f] to /delete-driver [/force]. That
    /// /delete-driver applies the same check to a devnode that is not
    /// present is read from that mapping, not documented, so nothing here
    /// relies on it.</para>
    ///
    /// <para><paramref name="walk"/> stands in for
    /// <see cref="DevnodesByPackage"/> in registry_ownership_check's
    /// offline part, which checks the decision. RemovePackage passes
    /// none.</para></summary>
    internal static RemovalPlan? PlanRemoval(List<PnputilHelper.DriverRecord>? store, bool deferred, string[] noted,
                                             Func<List<PnputilHelper.DriverRecord>, Dictionary<string, List<string>>?>? walk = null)
    {
        if (store == null) return null;
        var ours = Ours(store);
        if (deferred)
            ours = ours.Where(p => noted.Contains(p.OriginalName, StringComparer.OrdinalIgnoreCase)).ToList();
        // One walk of the USB devnodes for every package the run looks at:
        // the cleanup all of them, the deferred run the noted ones.
        if ((walk ?? (p => DevnodesByPackage(p)))(ours) is not { } on) return null;
        var delete = new List<PnputilHelper.DriverRecord>();
        var keep = new List<string>();
        foreach (var p in ours)
        {
            if (on.ContainsKey(p.PublishedName)) keep.Add(p.OriginalName);
            else delete.Add(p);
        }
        return new RemovalPlan(delete, keep.ToArray(), on);
    }

    /// <summary>RemovePackage's decision: the plan, or when there is none,
    /// the note change it makes instead. A whole listing whose devnode walk
    /// failed leaves the cleanup undecided on every package it listed, so it
    /// notes them all, and a later sweep's deferred run plans again with a
    /// walk of its own (<see cref="LeftRemovalCanChange"/>). A failed
    /// listing changes no note, nor does the deferred run, whose note already
    /// names the packages it looked at. registry_ownership_check's offline
    /// part checks it, <paramref name="walk"/> standing in for
    /// <see cref="DevnodesByPackage"/>.</summary>
    internal static (RemovalPlan? Plan, RegistryLedger.NoteChange? Undecided) DecideRemoval(
        List<PnputilHelper.DriverRecord>? store, bool deferred, string[] noted,
        Func<List<PnputilHelper.DriverRecord>, Dictionary<string, List<string>>?>? walk = null)
    {
        if (PlanRemoval(store, deferred, noted, walk) is { } plan) return (plan, null);
        if (deferred || store == null) return (null, null);
        return (null, RegistryLedger.NoteChange.Replace(Ours(store).Select(p => p.OriginalName)));
    }

    /// <summary>What <see cref="PlanRemoval"/> decided: the packages to
    /// delete, the INF names of those to keep, and the devnodes on each
    /// package it looked at, by published name.</summary>
    internal readonly record struct RemovalPlan(List<PnputilHelper.DriverRecord> Delete, string[] Keep,
                                                Dictionary<string, List<string>> On);

    /// <summary>True when a deferred run would change the driver store or
    /// the note: a noted package that no devnode uses, which its delete can
    /// remove, or a noted name no longer in the store, which comes out of
    /// the note. False while every noted package is kept by a devnode, the
    /// lasting case, or when the store cannot be listed. Read with no lock
    /// of the deploy, so a sweep takes the deploy lock, which every UMDF2
    /// create waits on, only for a run that can do something. The run reads
    /// all of it again under the locks.</summary>
    internal static bool LeftRemovalCanChange(string[] noted) =>
        noted.Length > 0 && LeftRemovalWouldChange(noted, PnputilHelper.TryEnumerateDrivers());

    /// <summary><see cref="LeftRemovalCanChange"/>'s decision from the
    /// <paramref name="store"/>'s listing and one walk of the USB devnodes
    /// on the noted packages. A noted name with no package in the store
    /// comes out of the note, so the run would change it. That is counted
    /// by INF name, since one name can have two packages in the store, as
    /// from two builds whose INF text differs. A listing or a walk that
    /// failed answers false, and the next program's first sweep looks
    /// again. <paramref name="walk"/> stands in for
    /// <see cref="DevnodesByPackage"/> in registry_ownership_check's
    /// offline part, which checks the decision.</summary>
    internal static bool LeftRemovalWouldChange(string[] noted, List<PnputilHelper.DriverRecord>? store,
        Func<List<PnputilHelper.DriverRecord>, Dictionary<string, List<string>>?>? walk = null)
    {
        if (noted.Length == 0 || store == null) return false;
        var noteds = Ours(store).Where(p => noted.Contains(p.OriginalName, StringComparer.OrdinalIgnoreCase)).ToList();
        int inStore = noteds.Select(p => p.OriginalName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (inStore < noted.Distinct(StringComparer.OrdinalIgnoreCase).Count()) return true;
        if ((walk ?? (p => DevnodesByPackage(p)))(noteds) is not { } used) return false;
        return noteds.Any(p => !used.ContainsKey(p.PublishedName));
    }

    /// <summary>The note change a removal makes. The cleanup's note becomes
    /// the packages it <paramref name="kept"/>: its whole listing was taken
    /// under the deploy lock that every note change takes, so a noted name
    /// it did not list is no longer in the store. The deferred run takes out
    /// every name it <paramref name="noted"/>, gone from the store or
    /// removed now, and puts back the ones it kept. registry_ownership_check's
    /// offline part checks it.</summary>
    internal static RegistryLedger.NoteChange NoteAfterRemoval(bool deferred, string[] noted, IEnumerable<string> kept) =>
        deferred ? RegistryLedger.NoteChange.Cleanup(noted, kept) : RegistryLedger.NoteChange.Replace(kept);

    // ── The packages ─────────────────────────────────────────────────────

    /// <summary>Stage, catalog, sign and add one profile's package to the
    /// driver store, once per process. Returns its published name.
    ///
    /// <para>Under the deploy lock. Two programs of one build stage into one
    /// directory, and the catalog step deletes and writes every catalog in
    /// it, so their adds take turns. The test certificate's check and create
    /// is under the same lock (<see cref="DriverBuilder.EnsureTestCertificate"/>),
    /// and so is <see cref="RemovePackage"/>.</para></summary>
    private static string EnsurePackage(Package p, Keeper keeper)
    {
        if (Published(p) is string known) return known;
        // The deploy lock, given up once the controller is disposed.
        using var deploy = AcquireUnlessStopped(DriverDeploy.MutexObject, 600_000, keeper,
            "Another program's HIDMaestro driver deploy held the machine-wide deploy lock for ten minutes.");
        try
        {
            // Another thread of this process may have added it while this
            // one waited.
            return Published(p) ?? AddPackage(p);
        }
        finally { deploy.ReleaseMutex(); }
    }

    /// <summary>True when <paramref name="published"/> is still this
    /// package's INF in the driver store. One that is gone is dropped from
    /// this process's cache.</summary>
    private static bool StillPublished(Package p, string published)
    {
        if (File.Exists(Path.Combine(InfDirectory, published))) return true;
        lock (s_lock) s_published.Remove(p.InfName);
        return false;
    }

    private static string? Published(Package p)
    {
        string? known;
        lock (s_lock)
            if (!s_published.TryGetValue(p.InfName, out known)) return null;
        return File.Exists(Path.Combine(InfDirectory, known)) ? known : null;
    }

    /// <summary>The add itself. The caller holds the deploy lock.</summary>
    private static string AddPackage(Package p)
    {
        string dir = ProtectedStaging.DirFor(p.StagingOperation);
        // UTF-16 LE with its byte-order mark, the Unicode form an INF may
        // take, so a product string outside ASCII reaches Device Manager
        // intact.
        byte[] inf = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(InfText(p))];
        using (var ms = new MemoryStream(inf))
            ProtectedStaging.StageStream(dir, p.InfName, ms, Sha256Hex(inf));

        // Held from the check of the INF through pnputil's add, so the INF
        // the catalog signs is the one checked and the package pnputil adds
        // is the one signed.
        int rc;
        string output;
        string infPath = Path.Combine(dir, p.InfName);
        using (var pin = ProtectedStaging.PinFile(infPath, Sha256Hex(inf)))
        {
            DriverBuilder.CatalogAndSignPackage(dir, pin);
            (rc, output) = PnputilHelper.AddDriver(infPath);
        }
        return RecordAdd(p.InfName, OurPackages(), rc, output);
    }

    /// <summary>A package add's outcome from the driver store listed after
    /// it: the package's published name, kept for this process, and the
    /// note change, which takes the add's own INF name out of the cleanup's
    /// note. A package a program adds again is not an uninstall's leftover,
    /// and the note keeps every other package it names. registry_ownership_check's
    /// offline part checks it.</summary>
    internal static string RecordAdd(string infName, List<PnputilHelper.DriverRecord> ours, int rc, string output)
    {
        string? published = ours
            .Where(r => r.OriginalName.Equals(infName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.PublishedName)
            .FirstOrDefault();
        if (published == null)
            throw new InvalidOperationException($"pnputil did not add {infName} (exit {rc}): {output.Trim()}");
        lock (s_lock) s_published[infName] = published;
        RegistryLedger.NoteWinUsbPackageAdded(infName);
        return published;
    }

    private static string InfDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    /// <summary>HIDMaestro's WinUSB packages in the driver store.</summary>
    private static List<PnputilHelper.DriverRecord> OurPackages() => TryOurPackages() ?? new List<PnputilHelper.DriverRecord>();

    /// <summary>The same, or null when the driver store could not be listed
    /// (<see cref="PnputilHelper.TryEnumerateDrivers"/>).</summary>
    private static List<PnputilHelper.DriverRecord>? TryOurPackages()
        => PnputilHelper.TryEnumerateDrivers() is { } store ? Ours(store) : null;

    /// <summary>HIDMaestro's WinUSB packages in a listing: its provider and
    /// the INF name's prefix.</summary>
    private static List<PnputilHelper.DriverRecord> Ours(List<PnputilHelper.DriverRecord> store)
        => store.Where(r => r.ProviderName.Equals(Provider, StringComparison.OrdinalIgnoreCase)
                         && r.OriginalName.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

    /// <summary>True when <paramref name="inf"/> is one of HIDMaestro's
    /// WinUSB packages, null when that cannot be told: the driver store
    /// listed nothing, though <paramref name="inf"/> is a third-party package
    /// it holds, as when pnputil fails or times out.</summary>
    private static bool? IsOurPackage(string? inf)
    {
        if (string.IsNullOrEmpty(inf) || IsInboxOrNone(inf)) return false;
        string file = Path.GetFileName(inf);
        lock (s_lock)
            foreach (var known in s_published.Values)
                if (string.Equals(file, known, StringComparison.OrdinalIgnoreCase)) return true;
        var all = PnputilHelper.EnumerateDrivers();
        if (all.Count == 0) return null;
        return all.Any(r => r.ProviderName.Equals(Provider, StringComparison.OrdinalIgnoreCase)
                         && r.OriginalName.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(file, r.PublishedName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    // ── Finding the devnode ──────────────────────────────────────────────

    /// <summary>The present devnode with this VID and PID on usbip-win2's
    /// host controller at the given vhci port. For a full-speed or
    /// high-speed device the vhci plugs the device in at the root hub port
    /// equal to the vhci port (usbip-win2 drivers/ude/vhci_ioctl.cpp:232-233),
    /// which is the devnode's address. usbip-win2 frees a port before the
    /// device on it is plugged out (drivers/ude/device.cpp, detach), so a new
    /// import can share the address with a devnode still leaving. Two
    /// devnodes there give none, and the caller looks again.</summary>
    internal static string? FindDevnode(int vhciPort, ushort vendorId, ushort productId)
    {
        string prefix = $@"USB\VID_{vendorId:X4}&PID_{productId:X4}\";
        string? found = null;
        foreach (string id in PresentDeviceIds("USB"))
        {
            if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
            if (ReadDword(dev, CM_DRP_ADDRESS) != (uint)vhciPort) continue;
            if (!OnUsbipRootHub(dev)) continue;
            if (found != null) return null;
            found = id;
        }
        return found;
    }

    /// <summary>The devnode of the controller's own import. A vhci port
    /// number names whatever device holds the port now, so the port comes
    /// from the import's location, 127.0.0.1, the server's port and the bus
    /// id, and the location must still be on that port once the devnode is
    /// found: another program's device of the same VID and PID can take a
    /// port the controller's import has just left.</summary>
    private static string? WaitForDevnode(Usbip.UsbipBackendHandle handle, ushort vendorId, ushort productId, int timeoutMs, Keeper keeper)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ThrowIfStopped(keeper);
            int port = Usbip.VhciClient.FindLocation("127.0.0.1", handle.ServerPort, handle.Device.BusId);
            if (port > 0
                && FindDevnode(port, vendorId, productId) is { } id
                && Usbip.VhciClient.FindLocation("127.0.0.1", handle.ServerPort, handle.Device.BusId) == port)
                return id;
            if (Environment.TickCount64 >= until) return null;
            Thread.Sleep(20);
        }
    }

    /// <summary>Wait until Windows has settled the devnode's driver. A
    /// devnode that arrives carrying the reinstall flag a binding left on it
    /// still names its old driver while it waits, with problem 18, for the
    /// reinstall, and a new devnode is visible before its install starts. So
    /// the driver is read only once the PnP manager has no installation
    /// pending (CM_WaitNoPendingInstallEvents, Microsoft Learn) and the
    /// devnode is either started, which a pending reinstall never is, or
    /// stopped on a decided driver: the flag clear, and an INF recorded or
    /// the install failed for want of one (problem 28).</summary>
    private static void WaitInstalled(string instanceId, int timeoutMs, Keeper keeper)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ThrowIfStopped(keeper);
            uint left = (uint)Math.Clamp(until - Environment.TickCount64, 0, 250);
            bool idle = CM_WaitNoPendingInstallEvents(left) == WAIT_OBJECT_0;
            if (idle && IsSettled(instanceId)) return;
            if (Environment.TickCount64 >= until)
                throw new InvalidOperationException(
                    $"Windows was still installing a driver on {instanceId} after {timeoutMs / 1000} s.");
            Thread.Sleep(20);
        }
    }

    /// <summary>The devnode is present and its driver decided: started, or
    /// stopped on a decided driver with the reinstall flag clear (see
    /// <see cref="WaitInstalled"/>).</summary>
    private static bool IsSettled(string instanceId) => ProblemOf(instanceId) switch
    {
        null => false,
        0 => IsStarted(instanceId),
        CM_PROB_REINSTALL => false,
        CM_PROB_FAILED_INSTALL => ((ConfigFlagsOf(instanceId) ?? 0) & CONFIGFLAG_REINSTALL) == 0,
        _ => ((ConfigFlagsOf(instanceId) ?? 0) & CONFIGFLAG_REINSTALL) == 0
             && DriverOf(instanceId, phantom: false).Inf != null,
    };

    /// <summary>Every USB devnode, present or not, whose driver is one of
    /// <paramref name="packages"/>, by the package's published name, in one
    /// walk of the USB devnodes. A package no devnode uses has no key. Null
    /// when the walk is not whole: the devnode list could not be read, a
    /// devnode in it could not be located, or its driver INF could not be
    /// read. A package that reads as unused then has a devnode the walk
    /// missed, so the caller removes nothing.
    ///
    /// <para>Each devnode is located with CM_LOCATE_DEVNODE_PHANTOM alone,
    /// which finds it present or not, so one that leaves during the walk is
    /// still read. Its driver INF is DEVPKEY_Device_DriverInfPath, and
    /// <see cref="InfSize"/> reads the size query's answer. Learn's page for
    /// CM_Get_DevNode_PropertyW names no code for a property that is not
    /// set, so that was measured, read-only, on one Windows 11 machine on
    /// 2026-10-15. Each of 125 USB devnodes, present and not, located with
    /// the phantom flag. 118 answered CR_BUFFER_SMALL. 7 answered
    /// CR_NO_SUCH_VALUE: present raw PDOs with no Driver value, no service
    /// and no class (a receiver's LampArray children). Across every
    /// enumerator, each devnode with no Driver value, present or not,
    /// answered CR_NO_SUCH_VALUE, and each devnode with a Driver value
    /// answered CR_BUFFER_SMALL.
    /// registry_ownership_check's offline part walks the machine it runs on
    /// with this code and fails when the walk is not whole.
    /// <paramref name="listIds"/> and <paramref name="readInf"/> stand in for
    /// the configuration manager in that probe's other checks.</para></summary>
    internal static Dictionary<string, List<string>>? DevnodesByPackage(List<PnputilHelper.DriverRecord> packages,
        Func<List<string>?>? listIds = null, Func<string, (bool Read, string? Inf)>? readInf = null)
    {
        var on = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (packages.Count == 0) return on;
        var published = new HashSet<string>(packages.Select(p => p.PublishedName), StringComparer.OrdinalIgnoreCase);
        if ((listIds ?? (() => TryDeviceIds("USB", CM_GETIDLIST_FILTER_ENUMERATOR)))() is not { } all) return null;
        foreach (string id in all)
        {
            var (read, inf) = (readInf ?? DriverInfOf)(id);
            if (!read) return null;
            if (inf == null) continue;
            string name = Path.GetFileName(inf);
            if (!published.Contains(name)) continue;
            if (!on.TryGetValue(name, out var ids)) on[name] = ids = new List<string>();
            ids.Add(id);
        }
        return on;
    }

    /// <summary>The INF of a devnode's driver, present or not. Read false
    /// when it could not be read, a null INF for a devnode with no driver
    /// installed (see <see cref="DevnodesByPackage"/>).</summary>
    private static (bool Read, string? Inf) DriverInfOf(string instanceId) => DriverInfFrom(
        () => (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM), dev),
        (dev, buf) =>
        {
            var key = DEVPKEY_Device_DriverInfPath;
            uint len = (uint)(buf?.Length ?? 0);
            int cr = CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref len, 0);
            return (cr, len);
        });

    /// <summary><see cref="DriverInfOf"/>'s reads, with the locate and the
    /// property query passed in: the query takes no buffer for the size and
    /// one of that size for the INF, and answers its code and length. A
    /// locate or a read that fails is a failed read, never "no driver".
    /// registry_ownership_check's offline part answers for them.</summary>
    internal static (bool Read, string? Inf) DriverInfFrom(Func<(int Cr, uint Dev)> locate, Func<uint, byte[]?, (int Cr, uint Len)> property)
    {
        var (locateCr, dev) = locate();
        if (locateCr != CR_SUCCESS) return (false, null);
        var (sizeCr, size) = property(dev, null);
        switch (InfSize(sizeCr, size))
        {
            case InfAnswer.NoDriver: return (true, null);
            case InfAnswer.Failed: return (false, null);
        }
        var buf = new byte[size];
        var (readCr, len) = property(dev, buf);
        if (readCr != CR_SUCCESS) return (false, null);
        // The property is a null-terminated string, and its length counts
        // the terminator.
        return (true, Encoding.Unicode.GetString(buf, 0, (int)Math.Min(len, size)).TrimEnd('\0'));
    }

    /// <summary>What the size query of a devnode's driver INF answered.</summary>
    internal enum InfAnswer { NoDriver, Read, Failed }

    /// <summary>The size query's answer: CR_NO_SUCH_VALUE is a devnode with
    /// no driver installed, CR_BUFFER_SMALL with a length is an INF to read,
    /// and every other answer, a zero length included, is a failed read (see
    /// <see cref="DevnodesByPackage"/>). registry_ownership_check's offline
    /// part checks it.</summary>
    internal static InfAnswer InfSize(int cr, uint len) =>
        cr == CR_NO_SUCH_VALUE ? InfAnswer.NoDriver
        : cr == CR_BUFFER_SMALL && len > 0 ? InfAnswer.Read
        : InfAnswer.Failed;

    // ── Drivers ──────────────────────────────────────────────────────────

    private static bool IsLibusbService(string? service) =>
        service != null && (service.Equals("WINUSB", StringComparison.OrdinalIgnoreCase)
                         || service.Equals("libusbK", StringComparison.OrdinalIgnoreCase)
                         || service.Equals("libusb0", StringComparison.OrdinalIgnoreCase));

    /// <summary>No driver, or one that ships with Windows. The driver store
    /// publishes every third-party INF under an oem name, so an INF with any
    /// other name is an inbox one (Microsoft Learn, "Driver Store").</summary>
    private static bool IsInboxOrNone(string? inf) =>
        string.IsNullOrEmpty(inf)
        || !Path.GetFileName(inf).StartsWith("oem", StringComparison.OrdinalIgnoreCase);

    /// <summary>The devnode's service and the INF its driver came from.</summary>
    internal static (string? Service, string? Inf) DriverOf(string instanceId, bool phantom)
    {
        uint flags = phantom ? CM_LOCATE_DEVNODE_PHANTOM : CM_LOCATE_DEVNODE_NORMAL;
        if (CM_Locate_DevNodeW(out uint dev, instanceId, flags) != CR_SUCCESS) return (null, null);
        string? service = ReadString(dev, CM_DRP_SERVICE);
        string? inf = ReadStringProperty(dev, DEVPKEY_Device_DriverInfPath);
        return (service, inf);
    }

    private static uint? ProblemOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;
        if (CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) != CR_SUCCESS) return null;
        return (status & DN_HAS_PROBLEM) != 0 ? problem : 0;
    }

    private static bool IsStarted(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return false;
        if (CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) != CR_SUCCESS) return false;
        return (status & DN_STARTED) != 0 && problem == 0;
    }

    /// <summary>Install HIDMaestro's package on one devnode, once the
    /// devnode's driver list shows another driver ranked above it. A lower
    /// rank is the better match (Microsoft Learn, "How Windows ranks drivers"),
    /// and only that other driver keeps Windows from choosing the package for
    /// a real device of the same kind. <paramref name="still"/> runs after
    /// the driver list is built and the package chosen, right before
    /// <c>DiInstallDevice</c>, and when it returns false nothing is installed
    /// and this returns false.</summary>
    private static bool InstallOn(string instanceId, string publishedInf, Func<bool> still)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException($"SetupDiCreateDeviceInfoList failed ({Marshal.GetLastWin32Error()}).");
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev))
                throw new InvalidOperationException($"SetupDiOpenDeviceInfo({instanceId}) failed ({Marshal.GetLastWin32Error()}).");
            if (!SetupDiBuildDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER))
                throw new InvalidOperationException($"SetupDiBuildDriverInfoList failed ({Marshal.GetLastWin32Error()}).");
            try
            {
                SP_DRVINFO_DATA_W ours = default;
                bool found = false;
                uint ourRank = uint.MaxValue, bestOther = uint.MaxValue;
                string? bestOtherInf = null;
                for (uint i = 0; ; i++)
                {
                    var drv = new SP_DRVINFO_DATA_W { cbSize = (uint)Marshal.SizeOf<SP_DRVINFO_DATA_W>() };
                    if (!SetupDiEnumDriverInfoW(set, ref dev, SPDIT_COMPATDRIVER, i, ref drv)) break;
                    string? infFile = DriverInfFile(set, ref dev, ref drv);
                    var prm = new SP_DRVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf<SP_DRVINSTALL_PARAMS>() };
                    if (!SetupDiGetDriverInstallParamsW(set, ref dev, ref drv, ref prm)) continue;
                    if (infFile != null && Path.GetFileName(infFile).Equals(publishedInf, StringComparison.OrdinalIgnoreCase))
                    {
                        ours = drv;
                        ourRank = prm.Rank;
                        found = true;
                    }
                    else if (prm.Rank < bestOther)
                    {
                        bestOther = prm.Rank;
                        bestOtherInf = infFile;
                    }
                }
                if (!found)
                    throw new InvalidOperationException($"{publishedInf} is not among the compatible drivers of {instanceId}.");
                if (bestOther >= ourRank)
                    throw new InvalidOperationException(
                        $"No driver ranks above HIDMaestro's WinUSB package (rank 0x{ourRank:X8}) on {instanceId}, so " +
                        "Windows would choose the package for every real device of this kind. Nothing was bound.");
                DeviceOrchestrator.LogDiag(
                    $"    winusb: {publishedInf} ranks 0x{ourRank:X8} on {instanceId}, below {Path.GetFileName(bestOtherInf ?? "?")} at 0x{bestOther:X8}");
                // SetupDiOpenDeviceInfo opens a devnode that is not present
                // too, so the devnode is checked here, last.
                if (!still())
                {
                    DeviceOrchestrator.LogDiag($"    winusb: {instanceId} left before the install, nothing installed");
                    return false;
                }
                if (!DiInstallDevice(IntPtr.Zero, set, ref dev, ref ours, 0, out bool reboot))
                    throw new InvalidOperationException($"DiInstallDevice on {instanceId} failed ({Marshal.GetLastWin32Error()}).");
                if (reboot)
                    DeviceOrchestrator.LogDiag($"    winusb: DiInstallDevice on {instanceId} asked for a restart");
                return true;
            }
            finally { SetupDiDestroyDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static unsafe string? DriverInfFile(IntPtr set, ref SP_DEVINFO_DATA dev, ref SP_DRVINFO_DATA_W drv)
    {
        var buf = new byte[8192];
        fixed (byte* p = buf)
        {
            ((SP_DRVINFO_DETAIL_DATA_W*)p)->cbSize = (uint)sizeof(SP_DRVINFO_DETAIL_DATA_W);
            if (!SetupDiGetDriverInfoDetailW(set, ref dev, ref drv, (IntPtr)p, (uint)buf.Length, out _))
                return null;
            // A fixed buffer reads as a pointer to its first character, and
            // the name is null-terminated within its 260.
            return new string(((SP_DRVINFO_DETAIL_DATA_W*)p)->InfFileName);
        }
    }

    /// <summary>Write a devnode's ConfigFlags. Works on a devnode that is
    /// not present.</summary>
    private static void WriteConfigFlags(string instanceId, uint flags)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            throw new InvalidOperationException($"{instanceId} is not a devnode.");
        var bytes = BitConverter.GetBytes(flags);
        int cr = CM_Set_DevNode_Registry_PropertyW(dev, CM_DRP_CONFIGFLAGS, bytes, (uint)bytes.Length, 0);
        if (cr != CR_SUCCESS)
            throw new InvalidOperationException($"Setting ConfigFlags on {instanceId} failed (CR {cr}).");
    }

    /// <summary>The devnode's ConfigFlags. A devnode with no ConfigFlags
    /// value has none set and reads 0 (CR_NO_SUCH_VALUE, cfgmgr32.h). Null
    /// when the devnode or the value cannot be read.</summary>
    internal static uint? ConfigFlagsOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return null;
        var buf = new byte[4];
        uint len = 4;
        int cr = CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_CONFIGFLAGS, out _, buf, ref len, 0);
        if (cr == CR_NO_SUCH_VALUE) return 0;
        return cr == CR_SUCCESS && len >= 4 ? BitConverter.ToUInt32(buf) : null;
    }

    // ── cfgmgr32 reads ───────────────────────────────────────────────────

    /// <summary>The present devnodes of an enumerator, none when the list
    /// cannot be read. Its callers look for one devnode, and finding none
    /// makes them look again or leave the devnode alone.</summary>
    private static List<string> PresentDeviceIds(string enumerator)
        => TryDeviceIds(enumerator, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT) ?? new List<string>();

    /// <summary>An enumerator's devnodes, or null when the list cannot be
    /// read: a call failed, or the list grew three times between the size
    /// and the read, as a burst of arrivals makes it.</summary>
    private static List<string>? TryDeviceIds(string enumerator, uint flags) =>
        ReadIdList(() =>
        {
            int cr = CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags);
            return (cr, len);
        }, buf => CM_Get_Device_ID_ListW(enumerator, buf, (uint)buf.Length, flags));

    /// <summary><see cref="TryDeviceIds"/>'s reads, with the two
    /// configuration manager calls passed in, so registry_ownership_check's
    /// offline part can answer for them.</summary>
    internal static List<string>? ReadIdList(Func<(int Cr, uint Len)> size, Func<char[], int> list)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (sizeCr, len) = size();
            if (sizeCr != CR_SUCCESS) return null;
            // A size of 0 or 1 holds no id, only the final terminator.
            if (len <= 1) return new List<string>();
            var buf = new char[len];
            int cr = list(buf);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) return null;
            var result = new List<string>();
            foreach (var s in new string(buf).Split('\0'))
                if (s.Length > 0) result.Add(s);
            return result;
        }
        return null;
    }

    private static uint? ReadDword(uint dev, uint property)
    {
        var buf = new byte[4];
        uint len = 4;
        return CM_Get_DevNode_Registry_PropertyW(dev, property, out _, buf, ref len, 0) == CR_SUCCESS && len >= 4
            ? BitConverter.ToUInt32(buf) : null;
    }

    private static string? ReadString(uint dev, uint property)
    {
        uint len = 0;
        CM_Get_DevNode_Registry_PropertyW(dev, property, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(dev, property, out _, buf, ref len, 0) != CR_SUCCESS) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
    }

    private static string[] ReadMultiSz(uint dev, uint property)
    {
        string? s = ReadString(dev, property);
        return s == null ? Array.Empty<string>() : s.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? ReadStringProperty(uint dev, DEVPROPKEY key)
    {
        uint len = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new byte[len];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref len, 0) != CR_SUCCESS) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
    }

    // ── Interop ──────────────────────────────────────────────────────────

    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x1A;
    private const int CR_NO_SUCH_DEVINST = 0x0D;
    private const int CR_NO_SUCH_VALUE = 0x25;
    private const uint DEVPROP_TYPE_FILETIME = 0x10;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 1;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CM_DRP_HARDWAREID = 0x02;
    private const uint CM_DRP_SERVICE = 0x05;
    private const uint CM_DRP_CONFIGFLAGS = 0x0B;
    private const uint CM_DRP_ADDRESS = 0x1D;
    private const uint CONFIGFLAG_REINSTALL = 0x00000020;
    private const uint DN_STARTED = 0x00000008;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint DN_WILL_BE_REMOVED = 0x00040000;
    private const uint CM_PROB_WILL_BE_REMOVED = 0x15;
    private const uint CM_PROB_DEVICE_NOT_THERE = 0x18;
    private const uint CM_PROB_REINSTALL = 18;
    private const uint CM_PROB_FAILED_INSTALL = 28;
    private const uint SPDIT_COMPATDRIVER = 2;
    private const uint WAIT_OBJECT_0 = 0;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // {83da6326-97a6-4088-9453-a1923f573b29}, 102 (devpkey.h)
    private static readonly DEVPROPKEY DEVPKEY_Device_LastArrivalDate = new()
    {
        fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
        pid = 102,
    };

    // {a8b865dd-2e3d-4094-ad97-e593a70c75d6}, 5
    private static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath = new()
    {
        fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"),
        pid = 5,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    // SP_DRVINFO_DATA_V2_W (setupapi.h). FILETIME is two DWORDs.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SP_DRVINFO_DATA_W
    {
        public uint cbSize;
        public uint DriverType;
        public IntPtr Reserved;
        public fixed char Description[256];
        public fixed char MfgName[256];
        public fixed char ProviderName[256];
        public uint DriverDateLow;
        public uint DriverDateHigh;
        public ulong DriverVersion;
    }

    // SP_DRVINFO_DETAIL_DATA_W (setupapi.h). cbSize is the size of this
    // fixed part, HardwareID's first character included.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SP_DRVINFO_DETAIL_DATA_W
    {
        public uint cbSize;
        public uint InfDateLow;
        public uint InfDateHigh;
        public uint CompatIDsOffset;
        public uint CompatIDsLength;
        public IntPtr Reserved;
        public fixed char SectionName[256];
        public fixed char InfFileName[260];
        public fixed char DrvDescription[256];
        public fixed char HardwareID[1];
    }

    // SP_DRVINSTALL_PARAMS (setupapi.h): Rank is the driver's rank for this
    // device, lower being the better match.
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DRVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Rank;
        public uint Flags;
        public UIntPtr PrivateData;
        public uint Reserved;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint dnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType,
        byte[]? Buffer, ref uint pulLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Set_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, byte[] Buffer, uint ulLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    // cfgmgr32.h: #define CM_WaitNoPendingInstallEvents CMP_WaitNoPendingInstallEvents.
    // The DLL exports only the CMP_ name.
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CMP_WaitNoPendingInstallEvents")]
    private static partial uint CM_WaitNoPendingInstallEvents(uint dwTimeout);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string pszFilter, [Out] char[] Buffer, uint BufferLen, uint ulFlags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent,
        uint OpenFlags, ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiBuildDriverInfoList(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDriverInfoList(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDriverInfoW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType,
        uint MemberIndex, ref SP_DRVINFO_DATA_W DriverInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDriverInfoDetailW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DRVINFO_DATA_W DriverInfoData, IntPtr DriverInfoDetailData, uint DriverInfoDetailDataSize, out uint RequiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDriverInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DRVINFO_DATA_W DriverInfoData, ref SP_DRVINSTALL_PARAMS DriverInstallParams);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDevice(IntPtr hwndParent, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DRVINFO_DATA_W DriverInfoData, uint Flags, [MarshalAs(UnmanagedType.Bool)] out bool NeedReboot);
}
