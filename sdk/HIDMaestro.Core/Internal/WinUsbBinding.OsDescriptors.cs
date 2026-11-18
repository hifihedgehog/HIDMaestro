using System;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>WinUSB on a machine that refuses HIDMaestro's package (2.3.0).
///
/// <para>Windows on ARM64, and Windows in S mode, install no package signed
/// as HIDMaestro's driver is, the WinUSB binding's package among them. The
/// device itself can ask for WinUSB instead, as a device built for WinUSB
/// does: it serves the Microsoft OS 1.0 string, the extended compatible ID
/// <c>WINUSB</c> and the <c>DeviceInterfaceGUID</c> property, and Windows
/// binds its own winusb.inf by the compatible ID <c>USB\MS_COMP_WINUSB</c>
/// it builds from them, with no INF of HIDMaestro's (Microsoft Learn,
/// "WinUSB Device"). The Switch 2 Pro persona's vendor interface binds the
/// same way. The real adapter serves none of these, so on such a machine the
/// device differs from it from the bind on. Where the package
/// installs, the package path is taken, and nothing the device reports
/// changes.</para>
///
/// <para>Windows asks a device for the string once per VID, PID and
/// revision and keeps the answer under <c>usbflags</c>. The controller holds
/// that key's claim from its create (<see cref="RegistryLedger.ClaimUsbFlags"/>),
/// so the bind clears the key, which makes Windows ask again, and the claim
/// puts it back as it was when the last controller of the identity goes.
/// The device is then plugged in again: the hub reads its descriptors anew,
/// and a devnode with a driver from the first arrival is flagged to install
/// again by rank, which picks WinUSB by the new compatible ID. The ledger's
/// WinUSB entry of the devnode puts it back: once the device has left, its
/// restore flags the devnode on winusb.inf again, so the next arrival, which
/// serves no descriptors until its own bind, gets the driver Windows gives
/// the real adapter (<see cref="RestoreLocked"/>).</para></summary>
internal static partial class WinUsbBinding
{
    /// <summary>Set through reflection by a probe to take the Microsoft OS
    /// descriptor path on a machine that installs the package, so x64 can
    /// run the path a refusing machine takes. False outside the
    /// probe.</summary>
    internal static bool OsDescriptorsForProbe;

    /// <summary>True when the binding goes by the device's Microsoft OS
    /// descriptors: Windows here installs no package signed as HIDMaestro's
    /// driver is, and the driver store holds none of HIDMaestro's WinUSB
    /// packages from before (the condition under which
    /// <see cref="AddPackage"/> refuses).</summary>
    internal static bool BindsByOsDescriptors() =>
        OsDescriptorsForProbe || (DriverDeploy.SigningStopsDeploy() && OurPackages().Count == 0);

    /// <summary>The bind on a machine that refuses the package. The devnode
    /// the device is on now is recorded in the ledger with its driver, as a
    /// package bind records it. Then the device serves the descriptors, the
    /// identity's usbflags key is cleared under the controller's claim, the
    /// devnode is flagged to install again once it has left, and the device
    /// is plugged in again. Bound once the devnode it comes back on runs
    /// WinUSB and is started.</summary>
    private static HMWinUsbBinding BindByOsDescriptors(HMController controller, Usbip.UsbipBackendHandle handle, Keeper keeper)
    {
        var d = handle.Device.Descriptors;
        var package = PackageFor(controller.Profile.Inner, d, controller.Backend)
            ?? throw new InvalidOperationException(
                $"Profile '{controller.Profile.Id}' declares no usbConfiguration.winUsb, so its device takes no WinUSB binding.");
        int waitMs = TimeoutScale.Apply(10_000);
        string instanceId = WaitForDevnode(handle, d.VendorId, d.ProductId, waitMs, keeper)
            ?? throw new InvalidOperationException(
                $"The device at 127.0.0.1:{handle.ServerPort} bus id {handle.Device.BusId} did not appear within 10 s.");
        handle.ResolveRecord(instanceId);
        WaitInstalled(instanceId, waitMs, keeper);
        var (service, inf) = DriverOf(instanceId, phantom: false);
        if (IsLibusbService(service)) return HMWinUsbBinding.AlreadyBound;
        if (!IsInboxOrNone(inf)) return HMWinUsbBinding.OtherDriver;

        if (!d.ServesWinUsb)
        {
            // Under the devnode lock, which every claim and restore takes,
            // the devnode and the driver it has go into the ledger before
            // anything changes, as the package path records them.
            using (var devnodes = AcquireUnlessStopped(DevnodeMutexObject, 120_000, keeper,
                       "Another program held HIDMaestro's machine-wide WinUSB lock for two minutes."))
            {
                try
                {
                    lock (keeper.Gate)
                    {
                        ThrowIfStopped(keeper);
                        RegistryLedger.ClaimWinUsb(instanceId, service, inf, controller.Index, ClaimTimeoutMs);
                        keeper.InstanceId = instanceId;
                    }
                }
                finally { devnodes.ReleaseMutex(); }
            }
            d.ServeWinUsb(package.InterfaceGuid);
            if (!RegistryLedger.ClearHeldUsbFlags(d.VendorId, d.ProductId, d.BcdDevice, controller.Index))
                throw new InvalidOperationException(
                    $"The usbflags key {RegistryLedger.UsbFlagsName(d.VendorId, d.ProductId, d.BcdDevice)} is not this controller's to change: " +
                    "its claim, taken at the create, is not in the registry ledger.");
            ThrowIfStopped(keeper);
            // The devnode's driver from its first arrival stays unless it is
            // flagged: Windows installs a returning devnode again only when
            // asked to. Flagged while it is away, so the arrival installs by
            // rank.
            string away = instanceId;
            handle.Replug(away, waitMs, () =>
            {
                var until = Environment.TickCount64 + waitMs;
                while (!HasLeft(away) && Environment.TickCount64 < until) Thread.Sleep(20);
                if (!FlagForNextArrival(away))
                    DeviceOrchestrator.LogDiag($"    winusb: {away} was not flagged before its re-plug");
            });
            string again = WaitForDevnode(handle, d.VendorId, d.ProductId, waitMs, keeper)
                ?? throw new InvalidOperationException(
                    $"The device at 127.0.0.1:{handle.ServerPort} bus id {handle.Device.BusId} did not come back within 10 s of its re-plug.");
            handle.ResolveRecord(again);
            if (!again.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            {
                using var devnodes = AcquireUnlessStopped(DevnodeMutexObject, 120_000, keeper,
                    "Another program held HIDMaestro's machine-wide WinUSB lock for two minutes.");
                try
                {
                    var (s2, i2) = DriverOf(again, phantom: false);
                    lock (keeper.Gate)
                    {
                        ThrowIfStopped(keeper);
                        RegistryLedger.ClaimWinUsb(again, s2, i2, controller.Index, ClaimTimeoutMs);
                        keeper.InstanceId = again;
                    }
                }
                finally { devnodes.ReleaseMutex(); }
                RegistryLedger.ReleaseWinUsb(instanceId, controller.Index);
            }
            instanceId = again;
        }

        var started = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < started)
        {
            ThrowIfStopped(keeper);
            var (s, _) = DriverOf(instanceId, phantom: false);
            if (string.Equals(s, "WINUSB", StringComparison.OrdinalIgnoreCase) && IsStarted(instanceId))
                return HMWinUsbBinding.Bound;
            Thread.Sleep(20);
        }
        var (last, lastInf) = DriverOf(instanceId, phantom: false);
        throw new InvalidOperationException(
            $"WinUSB did not start on {instanceId} within 10 s of its re-plug with Microsoft OS descriptors: it runs {last ?? "no driver"} ({lastInf ?? "no INF"}).");
    }
}
