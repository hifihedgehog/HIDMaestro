using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// The sweeps that remove devices only on proof that their owner is gone
/// (<see cref="OwnerCensus"/>). The uninstall-grade sweep that removes
/// everything regardless of owner is
/// <see cref="DeviceOrchestrator.RemoveEverything"/>.
/// </summary>
internal static class OwnerSweep
{
    /// <summary>This process's creates hold it for reading, the background
    /// phantom cleanup for writing, so the cleanup never removes the record
    /// of a devnode this process is in the middle of creating.</summary>
    internal static readonly ReaderWriterLockSlim CreateGate = new(LockRecursionPolicy.SupportsRecursion);

    private static int s_sessionSwept;

    /// <summary>Remove what programs that have exited left behind: the
    /// devices and USB/IP imports their records list, the present devices
    /// of v1.12.0 programs whose owner pipe has closed, and their registry
    /// ledger holds. Cheap when there is nothing to do: one census, no
    /// enumerator walk.</summary>
    internal static void ReclaimDeadOwners()
    {
        // A process that is not elevated can take no claim and remove
        // nothing, and its InstallDriver may still find the driver current.
        if (!MachineClaims.TryEnsureOwner()) return;
        var sw = Stopwatch.StartNew();
        var census = OwnerCensus.Take();
        if (!census.NothingToReclaim)
            Reclaim(census, includeSelf: false);
        DeviceOrchestrator.LogDiag($"    reclaim dead owners: {sw.ElapsedMilliseconds}ms (records {census.Records.Count}, devices {census.Devices.Count})");
        StartPhantomCleanupOnce();
    }

    /// <summary>Once per process, ahead of the first create, for programs
    /// that create without calling InstallDriver first.</summary>
    internal static void SessionSweepOnce()
    {
        if (Interlocked.Exchange(ref s_sessionSwept, 1) != 0) return;
        ReclaimDeadOwners();
    }

    /// <summary>The cleanup the overloads without a scope run: the
    /// controllers of programs that have exited. A running program's
    /// controllers, this one's included, stay. The packages and
    /// <c>SOFTWARE\HIDMaestro</c> go only with <paramref name="preserveInstall"/>
    /// false, only when no device of any program remains and no other
    /// program runs, and under the deploy lock, so no deploy or create of
    /// another program is between its check and its install.</summary>
    internal static void RemoveOrphaned(bool preserveInstall)
    {
        // A process that is not elevated can take no claim and remove
        // nothing.
        if (!MachineClaims.TryEnsureOwner()) return;
        var census = OwnerCensus.Take();
        Reclaim(census, includeSelf: false);

        try { RegistryLedger.PruneDead(); } catch { }
        try { DeviceManager.RemoveAccumulatedHmPhantoms(); } catch { }
        try { DeviceOrchestrator.RemoveOwnedInterfaceEntries(phantomOnly: true); } catch { }

        if (!preserveInstall)
        {
            using var mutex = DriverDeploy.Acquire();
            try
            {
                if (DriverDeploy.PackagesUnused(out string holders))
                {
                    try { PnputilHelper.RemoveAllHidMaestroPackages(); } catch { }
                    try { WinUsbBinding.RemovePackage(); } catch { }
                    try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\HIDMaestro", false); } catch { }
                }
                else
                {
                    DeviceOrchestrator.LogDiag($"    cleanup kept the driver packages: {holders}");
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static int s_exitHookRegistered;

    /// <summary>Remove this process's devices when it exits without having
    /// disposed its controllers. Registered with the owner claim. A killed
    /// process runs no handler, and the next sweep of any program removes
    /// its devices through its record instead.</summary>
    internal static void RegisterExitCleanup()
    {
        if (Interlocked.Exchange(ref s_exitHookRegistered, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { RemoveOwnDevicesAtExit(); } catch { }
        };
    }

    private static void RemoveOwnDevicesAtExit()
    {
        var census = OwnerCensus.Take();
        var own = census.Records.FirstOrDefault(r => r.Self);
        if (own == null) return;
        if (own.Controllers.Count == 0)
        {
            MachineClaims.DeleteOwnRecord();
            return;
        }
        // The composites first: their emulated devices read sections this
        // process is about to release.
        Usbip.UsbipBackend.DetachOwn();
        var groups = census.Devices.Where(d => d.Owner == OwnerCensus.Owner.Self).ToList();
        if (groups.Count > 0)
        {
            DeviceOrchestrator.SignalStopEvents(groups.Where(g => g.ControllerIndex >= 0).Select(g => g.ControllerIndex).ToHashSet());
            foreach (var g in groups)
            {
                RemoveGroup(g);
                if (g.ControllerIndex >= 0) DeviceOrchestrator.DeleteInstanceConfig(g.ControllerIndex);
            }
            try { DeviceManager.RemoveOrphanHidChildren(); } catch { }
        }
        foreach (var c in own.Controllers)
            if (c.TransportIndex >= 0) try { RegistryLedger.Release(c.TransportIndex); } catch { }
        MachineClaims.DeleteRecord(own.OwnerId);
    }

    /// <summary>Remove every device group of a dead owner (and, for the
    /// explicit cleanup, of this process), detach dead owners' USB/IP
    /// imports, and drop their records.</summary>
    private static void Reclaim(OwnerCensus census, bool includeSelf)
    {
        var targets = census.Devices
            .Where(d => d.Owner is OwnerCensus.Owner.Dead or OwnerCensus.Owner.DeadLegacy
                        || (includeSelf && d.Owner == OwnerCensus.Owner.Self))
            .ToList();
        var liveIndices = new HashSet<int>(census.Devices
            .Where(d => !targets.Contains(d) && d.ControllerIndex >= 0).Select(d => d.ControllerIndex));
        foreach (var r in census.Records)
            if (r.Live && !(includeSelf && r.Self))
                foreach (var c in r.Controllers) liveIndices.Add(c.TransportIndex);

        if (targets.Count > 0)
        {
            DeviceOrchestrator.SignalStopEvents(targets.Where(t => t.ControllerIndex >= 0).Select(t => t.ControllerIndex).ToHashSet());
            foreach (var group in targets)
            {
                DeviceOrchestrator.LogDiag($"    reclaim {group.Owner} {group.Key} index {group.ControllerIndex}: {string.Join(", ", group.InstanceIds)}");
                RemoveGroup(group);
                if (group.ControllerIndex >= 0 && !liveIndices.Contains(group.ControllerIndex))
                    DeviceOrchestrator.DeleteInstanceConfig(group.ControllerIndex);
            }
            try { DeviceManager.RemoveOrphanHidChildren(); } catch { }
        }

        var usbip = new Usbip.UsbipBackend.SweepView();
        foreach (var rec in census.DeadRecords)
        {
            bool settled = true;
            foreach (var c in rec.Controllers)
                if (string.Equals(c.Backend, "usbip", StringComparison.OrdinalIgnoreCase))
                    settled &= Usbip.UsbipBackend.DetachDeadImport(rec.UsbipPort, c.TransportIndex, usbip);
            // A composite whose attempt was left, because a listener on its
            // port did not say what it exports, is named by this record
            // alone, so the record stays for the next sweep.
            if (settled) MachineClaims.DeleteRecord(rec.OwnerId);
        }
        Usbip.UsbipBackend.DetachImportsOfDeadServers(usbip);

        if (targets.Count > 0 || census.DeadRecords.Any())
            try { RegistryLedger.PruneDead(); } catch { }
    }

    /// <summary>One controller's devnodes, the main node first so its
    /// children cascade, then whatever companion is left.</summary>
    private static void RemoveGroup(OwnerCensus.DeviceGroup group)
    {
        foreach (string id in group.InstanceIds.OrderBy(id => id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            try { DeviceManager.RemoveDevice(id, timeoutMs: 120_000, forceFallbacks: true); }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    reclaim RemoveDevice({id}) threw {ex.GetType().Name}"); }
        }
    }

    private static int s_phantomCleanupStarted;

    /// <summary>The phantom-record cleanup, once per process, off the
    /// create path. It takes <see cref="CreateGate"/> for writing, so it
    /// waits for this process's creates in flight and holds the next ones
    /// back until it is done. A phantom whose identity claim another
    /// program holds is that program's create in flight, and is left.</summary>
    internal static void StartPhantomCleanupOnce()
    {
        if (Interlocked.Exchange(ref s_phantomCleanupStarted, 1) != 0) return;
        var t = new Thread(() =>
        {
            CreateGate.EnterWriteLock();
            try
            {
                var sw = Stopwatch.StartNew();
                int phantoms = 0;
                try { phantoms = DeviceManager.RemoveAccumulatedHmPhantoms(); } catch { }
                try { DeviceOrchestrator.RemoveGhostXusbInterfaces(); } catch { }
                try { DeviceOrchestrator.RemoveOwnedInterfaceEntries(phantomOnly: true); } catch { }
                DeviceOrchestrator.LogDiag($"    phantom cleanup: {phantoms} removed in {sw.ElapsedMilliseconds}ms");
            }
            finally { CreateGate.ExitWriteLock(); }
        })
        { IsBackground = true, Name = "HIDMaestro phantom cleanup", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }
}
