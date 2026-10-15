using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// The sweeps that remove devices only on proof that their owner is gone
/// (<see cref="OwnerCensus"/>). The uninstall-grade sweep that removes
/// everything regardless of owner is
/// <see cref="DeviceOrchestrator.RemoveEverything"/>.
/// </summary>
internal static class OwnerSweep
{
    /// <summary>This process's creates hold it for reading from their lease
    /// to their last record write, and the background phantom cleanup and
    /// the exit handler for writing. So the cleanup never removes the record
    /// of a devnode this process is in the middle of creating, and the exit
    /// handler never deletes the record of a create in flight.</summary>
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
            Reclaim(census);
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
        Reclaim(census);

        try { RegistryLedger.PruneDead(); } catch { }
        // The GameInput tables HIDMaestro wrote through v1.11.0, which v1.12.0's
        // cleanup removed, go where no running program uses them. A program on
        // v1.12.0 or later holds a table it uses through the ledger, and a
        // create of its removes one it does not use. A v1.11.0 program writes
        // the table at its first create of an identity and reads through it
        // while that device lives, so a table stays while a present device or
        // a configuration carries its identity, and every table stays while a
        // create of unknown identity is in flight. The census runs under the
        // ledger lock, right before the delete.
        try { RegistryLedger.RemoveLegacyTables(() => OwnerCensus.GameInputKeysInUse(OwnerCensus.Take())); }
        catch { }
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

    /// <summary>How long the exit handler waits for this process's creates
    /// in flight, so an exit never waits on a create held in PnP or in a
    /// driver deploy.</summary>
    private const int ExitCreateWaitMs = 10_000;

    /// <summary>The runtime raises ProcessExit while the process's other
    /// threads still run: under .NET on the finalizer thread, under
    /// NativeAOT on the thread that called Environment.Exit or returned from
    /// Main. So a create can be in flight here. The handler waits for it through
    /// <see cref="CreateGate"/>, and holds back the next one until it is
    /// done. A create that does not finish in the bound keeps the record:
    /// its entry names the device it may still make, and the next sweep of
    /// any program removes that device through the dead record.</summary>
    private static void RemoveOwnDevicesAtExit()
    {
        bool gated;
        // TryEnterWriteLock throws LockRecursionException on a thread that
        // holds the read lock: under NativeAOT, an exit started inside a
        // create. Under .NET this handler runs on the finalizer thread, and
        // such an exit waits for the creating thread's read side until the
        // bound.
        try { gated = CreateGate.TryEnterWriteLock(TimeoutScale.Apply(ExitCreateWaitMs)); }
        catch (LockRecursionException) { gated = false; }
        if (!gated)
        {
            DeviceOrchestrator.LogDiag("    exit: a create is still in flight, so the record stays for the next sweep");
            return;
        }
        try { RemoveOwnDevicesAtExitGated(); }
        finally { CreateGate.ExitWriteLock(); }
    }

    private static void RemoveOwnDevicesAtExitGated()
    {
        var census = OwnerCensus.Take();
        var own = census.Records.FirstOrDefault(r => r.Self);
        if (own == null) return;
        if (own.Controllers.Count == 0)
        {
            MachineClaims.DeleteOwnRecord();
            return;
        }
        // No WinUSB re-bind of this process is inside its claim and install
        // when the devices leave (WinUsbBinding.StopKeepingAll). Then the
        // composites: their emulated devices read sections this process is
        // about to release.
        WinUsbBinding.StopKeepingAll();
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
        // Every controller's holds go in one release, which takes the ledger
        // lock once, so a squatted legacy mutex costs this exit one wait, not
        // one per controller. Its WinUSB devnodes are put back after the
        // lock is let go: that work takes the WinUSB lock, which comes before
        // the ledger's, and runs no driver install on a present devnode.
        var released = own.Controllers.Where(c => c.TransportIndex >= 0).Select(c => c.TransportIndex).ToList();
        if (released.Count > 0) try { RegistryLedger.Release(released); } catch { }
        // DeleteOwnRecord, so a create after this handler, on another thread
        // or in a handler that runs after it, writes the record's header
        // again.
        MachineClaims.DeleteOwnRecord();
    }

    /// <summary>Called once <see cref="Reclaim"/> holds the claims of the
    /// device groups it is about to remove, before it signals or removes
    /// anything, with the number of groups. Coexistence probe section W sets
    /// it through reflection to hold the sweep there while another program
    /// creates: the window between a census and its removals is too short to
    /// hit any other way. Null outside the probe.</summary>
    internal static Action<int>? BeforeReclaimActs;

    /// <summary>Remove every device group of a dead owner, detach dead
    /// owners' USB/IP imports, and drop their records.
    ///
    /// <para>The census is at least 500 ms old by the time a removal runs,
    /// since the stop events drain first, and each removal adds to it.
    /// In that time a program restarted after a crash, or another program,
    /// can create on the same index or identity, and the devnode it makes
    /// has the instance id of the dead one. So the sweep takes each group's
    /// index and identity claims before it acts on the group, and keeps them
    /// until the group's devices and its index's configuration are gone. A
    /// create on this release takes both claims before it writes the index's
    /// configuration or makes a devnode (ControllerLease), so no create can
    /// land on a group the sweep holds, and a group whose claims another
    /// program holds is skipped. A v1.12.0 program takes no claim: its owner
    /// pipe is checked again right before each removal and each
    /// configuration delete.</para></summary>
    private static void Reclaim(OwnerCensus census)
    {
        var candidates = census.Devices
            .Where(d => d.Owner is OwnerCensus.Owner.Dead or OwnerCensus.Owner.DeadLegacy)
            .ToList();
        var liveIndices = new HashSet<int>(census.Devices
            .Where(d => !candidates.Contains(d) && d.ControllerIndex >= 0).Select(d => d.ControllerIndex));
        foreach (var r in census.Records)
            if (r.Live)
                foreach (var c in r.Controllers) liveIndices.Add(c.TransportIndex);

        var claims = new GroupClaims();
        var targets = new List<OwnerCensus.DeviceGroup>();
        try
        {
            foreach (var group in candidates)
            {
                if (claims.TryHold(group)) targets.Add(group);
                else DeviceOrchestrator.LogDiag($"    reclaim skipped {group.Key} index {group.ControllerIndex}: a running program holds it now");
            }
            BeforeReclaimActs?.Invoke(targets.Count);
            if (targets.Count > 0)
            {
                DeviceOrchestrator.SignalStopEvents(targets.Where(t => t.ControllerIndex >= 0).Select(t => t.ControllerIndex).ToHashSet());
                foreach (var group in targets)
                {
                    ReclaimGroup(group, liveIndices);
                    claims.Release(group);
                }
                try { DeviceManager.RemoveOrphanHidChildren(); } catch { }
            }
        }
        finally { claims.ReleaseAll(); }

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

    /// <summary>A dead owner's group, under its claims: its devices, then
    /// its index's configuration when no other present device and no live
    /// record uses the index. A v1.12.0 program that started on the index
    /// since the claims were taken holds its owner pipe, and keeps both.</summary>
    private static void ReclaimGroup(OwnerCensus.DeviceGroup group, HashSet<int> liveIndices)
    {
        int index = group.ControllerIndex;
        if (OwnerCensus.LegacyOwnerRuns(index, group.InstanceIds))
        {
            DeviceOrchestrator.LogDiag($"    reclaim skipped {group.Key} index {index}: a v1.12.0 program holds the index now");
            return;
        }
        DeviceOrchestrator.LogDiag($"    reclaim {group.Owner} {group.Key} index {index}: {string.Join(", ", group.InstanceIds)}");
        RemoveGroup(group);
        if (index >= 0 && !liveIndices.Contains(index) && !OwnerCensus.LegacyOwnerRuns(index, group.InstanceIds))
            DeviceOrchestrator.DeleteInstanceConfig(index);
    }

    /// <summary>The index and identity claims a sweep holds on the groups
    /// it removes. Two groups can share an index, so a claim stays until the
    /// last group that needs it is released.</summary>
    private sealed class GroupClaims
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (SafeFileHandle Handle, int Groups)> _held = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Take the group's claims, or none of them when another
        /// program holds one now or a v1.12.0 program holds the index's
        /// owner pipe.</summary>
        public bool TryHold(OwnerCensus.DeviceGroup group)
        {
            lock (_lock)
            {
                var taken = new List<string>(2);
                foreach (string name in Names(group))
                {
                    if (_held.TryGetValue(name, out var held))
                    {
                        _held[name] = (held.Handle, held.Groups + 1);
                        taken.Add(name);
                        continue;
                    }
                    SafeFileHandle? claim = MachineClaims.TryClaim(name);
                    if (claim == null)
                    {
                        foreach (string t in taken) ReleaseLocked(t);
                        return false;
                    }
                    _held[name] = (claim, 1);
                    taken.Add(name);
                }
                if (OwnerCensus.LegacyOwnerRuns(group.ControllerIndex, group.InstanceIds))
                {
                    foreach (string t in taken) ReleaseLocked(t);
                    return false;
                }
                return true;
            }
        }

        public void Release(OwnerCensus.DeviceGroup group)
        {
            lock (_lock)
                foreach (string name in Names(group)) ReleaseLocked(name);
        }

        public void ReleaseAll()
        {
            lock (_lock)
            {
                foreach (var held in _held.Values) held.Handle.Dispose();
                _held.Clear();
            }
        }

        private void ReleaseLocked(string name)
        {
            if (!_held.TryGetValue(name, out var held)) return;
            if (held.Groups > 1) { _held[name] = (held.Handle, held.Groups - 1); return; }
            held.Handle.Dispose();
            _held.Remove(name);
        }

        /// <summary>A group of a device older than identity tokens is keyed
        /// by its index and has no identity claim.</summary>
        private static IEnumerable<string> Names(OwnerCensus.DeviceGroup group)
        {
            if (!group.Key.StartsWith('#')) yield return MachineClaims.IdentityPipeName(group.Key);
            if (group.ControllerIndex >= 0) yield return MachineClaims.IndexPipeName(group.ControllerIndex);
        }
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
