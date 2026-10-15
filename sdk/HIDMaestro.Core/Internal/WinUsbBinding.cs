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
/// the device reports changed. docs/design/v1.13.0.md has the measurements
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
/// puts the devnode back when the last holder lets go: a present devnode is
/// installed again by rank, and a gone one gets <c>CONFIGFLAG_REINSTALL</c>,
/// so its next arrival, a real device's included, installs again by rank.
/// After a crash the next program's reclaim does the same.</item>
/// </list>
/// <para>The bound devnode itself carries no reinstall flag. libusb reads
/// the flag as install state 1 and skips the device as one whose driver
/// "is reporting an issue" (windows_winusb.c, the DEV and EXT passes of
/// winusb_get_device_list), so no libusb host could open it. A program
/// killed while bound therefore leaves its instance on WinUSB, as Zadig
/// would, until the next HIDMaestro program reclaims its hold.</para></summary>
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

    private static readonly object s_lock = new();
    // Published name by original INF name, for the packages this process
    // added. Guarded by s_lock.
    private static readonly Dictionary<string, string> s_published = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bind WinUSB to the controller's virtual device.</summary>
    internal static HMWinUsbBinding Bind(HMController controller)
    {
        var handle = controller.UsbipHandle
            ?? throw new InvalidOperationException(
                $"Profile '{controller.Profile.Id}' runs on the UMDF2 driver. A WinUSB binding is for a USB/IP device.");
        var d = handle.Device.Descriptors;
        var package = PackageFor(controller.Profile.Inner, d)
            ?? throw new InvalidOperationException(
                $"Profile '{controller.Profile.Id}' declares no usbConfiguration.winUsb, so its device takes no WinUSB binding.");
        string instanceId = WaitForDevnode(handle, d.VendorId, d.ProductId, TimeoutScale.Apply(10_000))
            ?? throw new InvalidOperationException(
                $"The device at 127.0.0.1:{handle.ServerPort} bus id {handle.Device.BusId} did not appear within 10 s.");
        WaitInstalled(instanceId, TimeoutScale.Apply(10_000));

        // The package goes into the driver store first, under this lock
        // alone, since adding it the first time takes seconds.
        string? published = null;
        lock (s_lock)
        {
            var (service, inf) = DriverOf(instanceId, phantom: false);
            if (IsLibusbService(service))
            {
                if (!IsOurPackage(inf)) return HMWinUsbBinding.AlreadyBound;
            }
            else
            {
                if (!IsInboxOrNone(inf)) return HMWinUsbBinding.OtherDriver;
                published = EnsurePackage(package);
            }
        }

        // The ledger's machine-wide lock, then this one: the order a
        // restore takes them in (RegistryLedger.Sweep, then Restore). The
        // other order deadlocked a bind against a restore on another
        // thread, and held every program's ledger work with them.
        using (var ledger = RegistryLedger.AcquireLock())
        {
            try
            {
                lock (s_lock)
                {
                    // Read again: a restore may have run between the two locks.
                    var (service, inf) = DriverOf(instanceId, phantom: false);
                    if (IsLibusbService(service))
                    {
                        // On one of HIDMaestro's own packages, which a killed
                        // program's binding leaves until a sweep puts the
                        // devnode back. This controller holds it from here,
                        // so no sweep moves it while the controller lives.
                        if (IsOurPackage(inf))
                            RegistryLedger.ClaimWinUsb(instanceId, service, inf, controller.Index);
                        return HMWinUsbBinding.AlreadyBound;
                    }
                    if (!IsInboxOrNone(inf)) return HMWinUsbBinding.OtherDriver;
                    published ??= EnsurePackage(package);

                    // Recorded before the devnode changes, so a crash between
                    // the two leaves a record whose restore finds the devnode
                    // as it was.
                    RegistryLedger.ClaimWinUsb(instanceId, service, inf, controller.Index);
                    InstallOn(instanceId, published);
                }
            }
            finally { ledger.ReleaseMutex(); }
        }

        // WinUSB is bound once the devnode reports its service and the
        // interface libusb opens.
        var until = Environment.TickCount64 + TimeoutScale.Apply(10_000);
        while (Environment.TickCount64 < until)
        {
            var (s, _) = DriverOf(instanceId, phantom: false);
            if (string.Equals(s, "WINUSB", StringComparison.OrdinalIgnoreCase) && IsStarted(instanceId))
                return HMWinUsbBinding.Bound;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException($"WinUSB did not start on {instanceId} within 10 s.");
    }

    /// <summary>Put a devnode the ledger recorded back on the driver Windows
    /// picks by rank. A present devnode is installed again now. A gone one
    /// gets the reinstall flag, which installs it again by rank when it next
    /// arrives, and so does a present one whose reinstall failed. A devnode
    /// on any driver but one of HIDMaestro's WinUSB packages is someone
    /// else's and is left alone.
    /// <paramref name="waitGoneMs"/> gives a devnode whose device was just
    /// detached that long to leave first, since a gone devnode needs nothing
    /// more.</summary>
    internal static void Restore(string instanceId, int waitGoneMs = 0)
    {
        var until = Environment.TickCount64 + waitGoneMs;
        while (waitGoneMs > 0 && Environment.TickCount64 < until
               && CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
            Thread.Sleep(20);

        lock (s_lock)
        {
            bool present = CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
            var (_, inf) = DriverOf(instanceId, phantom: !present);
            if (!IsOurPackage(inf)) return;
            if (present)
            {
                try { InstallBestDriver(instanceId); return; }
                catch (Exception ex) { DeviceOrchestrator.LogDiag($"    winusb restore of {instanceId} threw {ex.Message}"); }
            }
            try { SetReinstallFlag(instanceId); } catch { }
        }
    }

    /// <summary>The uninstall-grade cleanup: every devnode still on one of
    /// the packages goes back to its driver by rank, present ones now, and
    /// gone ones are removed, since a non-present devnode keeps a package in
    /// use. Then the packages leave the driver store. Nothing else is
    /// touched: a package's provider and original name identify it.</summary>
    internal static void RemovePackage()
    {
        lock (s_lock)
        {
            var packages = OurPackages();
            if (packages.Count == 0) return;
            foreach (string id in DevnodesOn(packages))
            {
                bool present = CM_Locate_DevNodeW(out _, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
                try
                {
                    if (present) InstallBestDriver(id);
                    else DeviceManager.RemoveDevice(id, timeoutMs: 30_000, forceFallbacks: false);
                }
                catch (Exception ex) { DeviceOrchestrator.LogDiag($"    winusb cleanup of {id} threw {ex.Message}"); }
            }
            foreach (var p in packages)
            {
                if (!PnputilHelper.DeletePackagePlain(p.PublishedName, out string err))
                    DeviceOrchestrator.LogDiag($"    winusb package {p.PublishedName} not deleted: {err}");
            }
            s_published.Clear();
        }
    }

    // ── The packages ─────────────────────────────────────────────────────

    /// <summary>Stage, catalog, sign and add one profile's package to the
    /// driver store, once per process. Returns its published name. Caller
    /// holds s_lock.</summary>
    private static string EnsurePackage(Package p)
    {
        if (s_published.TryGetValue(p.InfName, out var known) && File.Exists(Path.Combine(InfDirectory, known)))
            return known;

        string dir = ProtectedStaging.DirFor(p.StagingOperation);
        // UTF-16 LE with its byte-order mark, the Unicode form an INF may
        // take, so a product string outside ASCII reaches Device Manager
        // intact.
        byte[] inf = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(InfText(p))];
        using (var ms = new MemoryStream(inf))
            ProtectedStaging.StageStream(dir, p.InfName, ms, Sha256Hex(inf));
        DriverBuilder.CatalogAndSignPackage(dir);

        var (rc, output) = PnputilHelper.AddDriver(Path.Combine(dir, p.InfName));
        string? published = OurPackages()
            .Where(r => r.OriginalName.Equals(p.InfName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.PublishedName)
            .FirstOrDefault();
        if (published == null)
            throw new InvalidOperationException($"pnputil did not add {p.InfName} (exit {rc}): {output.Trim()}");
        s_published[p.InfName] = published;
        return published;
    }

    private static string InfDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    /// <summary>HIDMaestro's WinUSB packages in the driver store.</summary>
    private static List<PnputilHelper.DriverRecord> OurPackages()
        => PnputilHelper.EnumerateDrivers()
            .Where(r => r.ProviderName.Equals(Provider, StringComparison.OrdinalIgnoreCase)
                     && r.OriginalName.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>True when <paramref name="inf"/> is one of HIDMaestro's
    /// WinUSB packages. Caller holds s_lock.</summary>
    private static bool IsOurPackage(string? inf)
    {
        if (string.IsNullOrEmpty(inf)) return false;
        string file = Path.GetFileName(inf);
        foreach (var known in s_published.Values)
            if (string.Equals(file, known, StringComparison.OrdinalIgnoreCase)) return true;
        return OurPackages().Any(r => string.Equals(file, r.PublishedName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sha256Hex(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    // ── Finding the devnode ──────────────────────────────────────────────

    /// <summary>The present devnode with this VID and PID on usbip-win2's
    /// host controller at the given vhci port. For a full-speed or
    /// high-speed device the vhci plugs the device in at the root hub port
    /// equal to the vhci port (usbip-win2 drivers/ude/vhci_ioctl.cpp:232-233),
    /// which is the devnode's address.</summary>
    internal static string? FindDevnode(int vhciPort, ushort vendorId, ushort productId)
    {
        string prefix = $@"USB\VID_{vendorId:X4}&PID_{productId:X4}\";
        foreach (string id in PresentDeviceIds("USB"))
        {
            if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
            if (ReadDword(dev, CM_DRP_ADDRESS) != (uint)vhciPort) continue;
            if (CM_Get_Parent(out uint hub, dev, 0) != CR_SUCCESS) continue;
            if (CM_Get_Parent(out uint host, hub, 0) != CR_SUCCESS) continue;
            if (ReadMultiSz(host, CM_DRP_HARDWAREID).Any(h => h.Contains("USBIP_WIN2", StringComparison.OrdinalIgnoreCase)))
                return id;
        }
        return null;
    }

    /// <summary>The devnode of the controller's own import. A vhci port
    /// number names whatever device holds the port now, so the port comes
    /// from the import's location, 127.0.0.1, the server's port and the bus
    /// id, and the location must still be on that port once the devnode is
    /// found: another program's device of the same VID and PID can take a
    /// port the controller's import has just left.</summary>
    private static string? WaitForDevnode(Usbip.UsbipBackendHandle handle, ushort vendorId, ushort productId, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
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
    private static void WaitInstalled(string instanceId, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            uint left = (uint)Math.Max(0, until - Environment.TickCount64);
            bool idle = CM_WaitNoPendingInstallEvents(left) == WAIT_OBJECT_0;
            uint? problem = ProblemOf(instanceId);
            bool settled = problem switch
            {
                null => false,
                0 => IsStarted(instanceId),
                CM_PROB_REINSTALL => false,
                CM_PROB_FAILED_INSTALL => ((ConfigFlagsOf(instanceId) ?? 0) & CONFIGFLAG_REINSTALL) == 0,
                _ => ((ConfigFlagsOf(instanceId) ?? 0) & CONFIGFLAG_REINSTALL) == 0
                     && DriverOf(instanceId, phantom: false).Inf != null,
            };
            if (idle && settled) return;
            if (Environment.TickCount64 >= until)
                throw new InvalidOperationException(
                    $"Windows was still installing a driver on {instanceId} after {timeoutMs / 1000} s.");
            Thread.Sleep(20);
        }
    }

    /// <summary>Every USB devnode, present or not, whose driver is one of
    /// <paramref name="packages"/>.</summary>
    private static List<string> DevnodesOn(List<PnputilHelper.DriverRecord> packages)
    {
        var list = new List<string>();
        var published = new HashSet<string>(packages.Select(p => p.PublishedName), StringComparer.OrdinalIgnoreCase);
        foreach (string id in AllDeviceIds("USB"))
        {
            bool present = CM_Locate_DevNodeW(out _, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
            var (_, inf) = DriverOf(id, phantom: !present);
            if (inf != null && published.Contains(Path.GetFileName(inf)))
                list.Add(id);
        }
        return list;
    }

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
    /// a real device of the same kind.</summary>
    private static void InstallOn(string instanceId, string publishedInf)
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
                if (!DiInstallDevice(IntPtr.Zero, set, ref dev, ref ours, 0, out bool reboot))
                    throw new InvalidOperationException($"DiInstallDevice on {instanceId} failed ({Marshal.GetLastWin32Error()}).");
                if (reboot)
                    DeviceOrchestrator.LogDiag($"    winusb: DiInstallDevice on {instanceId} asked for a restart");
            }
            finally { SetupDiDestroyDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Install the driver Windows ranks best on one devnode,
    /// which <c>DiInstallDevice</c> does when no driver is named.</summary>
    private static void InstallBestDriver(string instanceId)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException($"SetupDiCreateDeviceInfoList failed ({Marshal.GetLastWin32Error()}).");
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev))
                throw new InvalidOperationException($"SetupDiOpenDeviceInfo({instanceId}) failed ({Marshal.GetLastWin32Error()}).");
            if (!DiInstallDeviceBest(IntPtr.Zero, set, ref dev, IntPtr.Zero, 0, out bool reboot))
                throw new InvalidOperationException($"DiInstallDevice on {instanceId} failed ({Marshal.GetLastWin32Error()}).");
            if (reboot)
                DeviceOrchestrator.LogDiag($"    winusb: restoring {instanceId} asked for a restart");
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

    /// <summary>Set <c>CONFIGFLAG_REINSTALL</c>, keeping every other flag.
    /// Works on a devnode that is not present.</summary>
    internal static void SetReinstallFlag(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            throw new InvalidOperationException($"{instanceId} is not a devnode.");
        uint flags = ReadDword(dev, CM_DRP_CONFIGFLAGS) ?? 0;
        flags |= CONFIGFLAG_REINSTALL;
        var bytes = BitConverter.GetBytes(flags);
        int cr = CM_Set_DevNode_Registry_PropertyW(dev, CM_DRP_CONFIGFLAGS, bytes, (uint)bytes.Length, 0);
        if (cr != CR_SUCCESS)
            throw new InvalidOperationException($"Setting ConfigFlags on {instanceId} failed (CR {cr}).");
    }

    /// <summary>The devnode's ConfigFlags, or null when it has none.</summary>
    internal static uint? ConfigFlagsOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return null;
        return ReadDword(dev, CM_DRP_CONFIGFLAGS);
    }

    // ── cfgmgr32 reads ───────────────────────────────────────────────────

    private static List<string> PresentDeviceIds(string enumerator)
        => DeviceIds(enumerator, CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT);

    private static List<string> AllDeviceIds(string enumerator)
        => DeviceIds(enumerator, CM_GETIDLIST_FILTER_ENUMERATOR);

    private static List<string> DeviceIds(string enumerator, uint flags)
    {
        var result = new List<string>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != CR_SUCCESS || len == 0) return result;
            var buf = new char[len];
            int cr = CM_Get_Device_ID_ListW(enumerator, buf, len, flags);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) return result;
            foreach (var s in new string(buf).Split('\0'))
                if (s.Length > 0) result.Add(s);
            return result;
        }
        return result;
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
    private const uint CM_PROB_REINSTALL = 18;
    private const uint CM_PROB_FAILED_INSTALL = 28;
    private const uint SPDIT_COMPATDRIVER = 2;
    private const uint WAIT_OBJECT_0 = 0;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

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

    [LibraryImport("newdev.dll", EntryPoint = "DiInstallDevice", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDeviceBest(IntPtr hwndParent, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        IntPtr DriverInfoData, uint Flags, [MarshalAs(UnmanagedType.Bool)] out bool NeedReboot);
}
