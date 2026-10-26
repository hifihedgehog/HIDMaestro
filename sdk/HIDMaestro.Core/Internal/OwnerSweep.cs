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
    /// to their last record write, and the background phantom cleanup, the
    /// exit handler and DeviceOrchestrator.RemoveEverything for writing. So
    /// the cleanup never removes the record
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
    /// that create without calling InstallDriver first. A thread that loses
    /// the race still takes the owner claim through the removal gate
    /// (Uninstall.RemovalGate), so a second create at the same moment waits
    /// for a removal another process runs and does not claim during it.
    /// Once the claim is held that costs one field read.</summary>
    internal static void SessionSweepOnce()
    {
        if (Interlocked.Exchange(ref s_sessionSwept, 1) != 0)
        {
            MachineClaims.TryEnsureOwner();
            return;
        }
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
        // The phantom records go with the background cleanup, once per
        // process, as after the first create, and this call does not wait
        // for it to end. A later call starts no cleanup and removes no
        // phantom, and the phantoms this process leaves after the cleanup
        // started go with the next process's first cleanup. When the process
        // is already exiting, this call runs the cleanup itself before it
        // returns (StartPhantomCleanupOnce).
        StartPhantomCleanupOnce();
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

    /// <summary>The exit handler's bound on its waits for work it does not
    /// control: the join of the phantom cleanup, the wait for a create in
    /// flight and every wait of the removal for the PnP lock another program
    /// holds, all within this one budget. A take of the lock that does not
    /// get it in what is left ends the removal and keeps the record, as a
    /// kill does. Only the time spent waiting for another program's hold of
    /// the lock is charged, never the removal's own work under it nor a wait
    /// behind another removal of this process. Once the process is exiting,
    /// the same 10 s, one budget for the whole process, bounds the program's
    /// own takes of the lock on threads with no bound of their own
    /// (DeviceManager.HoldPnpLock). What follows under the lock is waited
    /// out, as Dispose waits it out. Each RemoveDevice of this process's
    /// devices ends within its own bound (up to 120 s an id with its
    /// fallbacks), and so do the orphan HID children's waits. The wait for a
    /// WinUSB bind in progress ends when the bind ends, since its
    /// DiInstallDevice takes no timeout. The ledger
    /// release's WinUSB restores wait for the WinUSB devnode lock and end
    /// when their restores end. The removal's own work has no bound, since
    /// one xinputhid controller takes seconds to remove (HMContext.Dispose)
    /// and a bound there would leave devices present after the program is
    /// gone. A removal queued behind another thread of this process whose
    /// one hold of the lock lasts longer than this scope's whole bound is
    /// refused as if that hold were stuck, and keeps the record
    /// (DeviceManager.TakePnpLock). The phantom cleanup's own
    /// waiting budget is half of this one (<see cref="UninstallWaitBudgetMs"/>),
    /// and once the handler starts the cleanup begins no further uninstall,
    /// so the join outlasts the cleanup unless one uninstall call alone runs
    /// past the rest of the budget.</summary>
    internal const int ExitWaitMs = 10_000;

    /// <summary>Set when the exit handler starts: the phantom cleanup then
    /// begins no further uninstall.</summary>
    private static volatile bool s_exiting;

    /// <summary>The runtime raises ProcessExit while the process's other
    /// threads still run: under .NET on the finalizer thread, under
    /// NativeAOT on the thread that called Environment.Exit or returned from
    /// Main. So a create or the phantom cleanup can be in flight here. The
    /// handler waits for both through <see cref="CreateGate"/> within
    /// <see cref="ExitWaitMs"/>, then removes this process's devices on its
    /// own thread to the end of that removal, and holds back the next create
    /// until it is done. When a create or the cleanup holds the gate past the
    /// bound, the handler removes nothing and keeps the record, which names
    /// every device of the process, the ones a create in flight may still
    /// make included, and the next sweep of any program removes them through
    /// the dead record. A device the removal does not confirm gone keeps the
    /// record the same way.
    ///
    /// <para>Four exits therefore leave the process's devices present until
    /// a program built on this release next sweeps. In the first, a create
    /// still holds the gate's read side at the bound, on another thread,
    /// waiting in ControllerLease for another program's sweep (up to 120 s)
    /// or behind a transport install. In the second, one uninstall of the
    /// phantom cleanup outlasts the bound. In the third, under .NET, a thread
    /// inside a create calls Environment.Exit, and the handler on the
    /// finalizer thread waits for that thread's read side until the bound.
    /// Under NativeAOT that exit runs the handler on the creating thread,
    /// which sees its own read side and gives up at once. In the fourth,
    /// another program holds the PnP lock, which every removal takes, past
    /// what is left of the bound (DeviceManager.BoundPnpLockWaits), and the
    /// removal stops at the group it was on.</para></summary>
    private static void RemoveOwnDevicesAtExit()
    {
        var clock = Stopwatch.StartNew();
        long budget = TimeoutScale.Apply(ExitWaitMs);
        int Remaining() => (int)Math.Max(0, budget - clock.ElapsedMilliseconds);
        s_exiting = true;
        DeviceManager.KeepUnsettledClaims = true;
        // The cleanup holds the gate for writing while it runs, so the wait
        // for a create below would otherwise spend the budget on it. The
        // owner claim registers this handler before a cleanup can start,
        // with one case this handler never sees: a first claim taken inside
        // the program's own ProcessExit handler registers this one while
        // ProcessExit is being raised, and a handler added then does not
        // run. That call runs the cleanup itself, or, under .NET in a
        // program in which no code that refers to the SDK ran or was
        // compiled before exit began, starts the thread that ends with the
        // process (StartPhantomCleanupOnce).
        if (!JoinPhantomCleanup(Remaining())) return;
        bool gated;
        // TryEnterWriteLock throws LockRecursionException on a thread that
        // holds the read lock: under NativeAOT, an exit started inside a
        // create. Under .NET this handler runs on the finalizer thread, and
        // such an exit waits for the creating thread's read side until the
        // bound.
        try { gated = CreateGate.TryEnterWriteLock(Remaining()); }
        catch (LockRecursionException) { gated = false; }
        if (!gated)
        {
            DeviceOrchestrator.LogDiag("    exit: a create is still in flight, so the record and every device stay for the next sweep");
            return;
        }
        try
        {
            // On this thread and to its end, so no removal of this process
            // runs alongside a ProcessExit handler registered after this one,
            // such as a program's own handler that disposes its context.
            // DeleteOwnRecord, so a create after this handler, on another
            // thread or in a handler that runs after it, writes the record's
            // header again.
            bool done;
            using (DeviceManager.BoundPnpLockWaits(Remaining()))
                done = RemoveOwnDevicesAtExitGated();
            if (done) MachineClaims.DeleteOwnRecord();
        }
        finally { CreateGate.ExitWriteLock(); }
    }

    /// <summary>The exit handler's removal. Returns true when the record can
    /// go (<see cref="ExitRecordMayGo"/>).</summary>
    private static bool RemoveOwnDevicesAtExitGated()
    {
        var census = OwnerCensus.Take();
        var own = census.Records.FirstOrDefault(r => r.Self);
        return ExitRecordMayGo(own, () => RemoveOwnDevicesAndRelease(census, own!),
            Registry.LocalMachine, RegistryLedger.Root, RegistryLedger.ReleaseHolds);
    }

    /// <summary>Whether an exit may delete the owner record
    /// <paramref name="own"/>: there is one, the devices of the controllers
    /// it lists are out of the device tree and their holds released
    /// (<paramref name="removeAndRelease"/>, run only when it lists one), and
    /// no hold of this process stands in the ledger at
    /// <paramref name="root"/> of <paramref name="hive"/> once any that does
    /// is released through <paramref name="release"/>. That last check runs
    /// on every exit that would delete the record, one whose controllers
    /// were all disposed before it included: a dispose's release that failed
    /// partway leaves its hold behind and its record with no controller.
    /// A census of another program takes a running process's ledger holder
    /// that no live record names for a v1.12.0 program's
    /// (OwnerCensus.Build), and this process runs on after this handler. So
    /// the record stays while a hold of this process does, until the process
    /// ends, after which the holder reads gone and the record dead. HKLM's
    /// ledger for the exit, a scratch key for the coexistence probe's
    /// OF13.</summary>
    internal static bool ExitRecordMayGo(MachineClaims.OwnerRecord? own, Func<bool> removeAndRelease,
        RegistryKey hive, string root, Action<string[]> release)
    {
        if (own == null) return false;
        if (own.Controllers.Count > 0 && !removeAndRelease()) return false;
        if (RegistryLedger.OwnHoldsSettled(hive, root, release)) return true;
        DeviceOrchestrator.LogDiag("    exit: a ledger hold of this process could not be released, so the record stays until the process ends");
        return false;
    }

    /// <summary>Remove the devices of the controllers <paramref name="own"/>
    /// lists and release their ledger holds. Returns false when a device is
    /// still in the device tree.</summary>
    private static bool RemoveOwnDevicesAndRelease(OwnerCensus census, MachineClaims.OwnerRecord own)
    {
        // No WinUSB re-bind of this process is inside its claim and install
        // when the devices leave (WinUsbBinding.StopKeepingAll), which waits
        // out a bind in progress, as a dispose does. Then the composites:
        // their emulated devices read sections this process is about to
        // release.
        WinUsbBinding.StopKeepingAll();
        Usbip.UsbipBackend.DetachOwn();
        var groups = census.Devices.Where(d => d.Owner == OwnerCensus.Owner.Self).ToList();
        if (groups.Count > 0)
        {
            DeviceOrchestrator.SignalStopEvents(groups.Where(g => g.ControllerIndex >= 0).Select(g => g.ControllerIndex).ToHashSet());
            int unsettled = 0;
            RemoveGroups(groups, g =>
            {
                bool settled = false;
                try
                {
                    settled = RemoveGroup(g);
                    if (settled && g.ControllerIndex >= 0) DeviceOrchestrator.DeleteInstanceConfig(g.ControllerIndex);
                }
                finally { if (!settled) unsettled++; }
            });
            if (!RemoveOrphanChildren()) unsettled++;
            // Once the process is gone, its claims are free, and a create of
            // another program could register an id Windows is still removing.
            // The record keeps the device a dead owner's, which the next
            // sweep removes under its claims.
            if (unsettled > 0)
            {
                DeviceOrchestrator.LogDiag("    exit: a device is still in the device tree, so the record stays for the next sweep");
                return false;
            }
        }
        // Every controller's holds go in one release, which takes the ledger
        // lock once for the GameInput and joy.cpl restores, so a squatted
        // legacy mutex costs those one long wait, not one per controller.
        // The release's N WinUSB entries are put back after that scope ends:
        // that work takes the WinUSB lock, which comes before the ledger's,
        // and runs no driver install on a present devnode. Each entry takes
        // the ledger lock up to twice on its own, to check it and to delete
        // it or move its follow-up's deadline, and the release once more to
        // clear empty keys. Once the long wait has marked the name, each of
        // those (2N+1) acquisitions waits up to the short bound
        // (LedgerLock.LegacyMarkedWaitMs, 500 ms scaled) for v1.12.0's mutex.
        // The wait for the ledger's own mutex has no such bound: this
        // program's WinUSB follow-up threads take it too, one scope at a
        // time, with 250 ms between laps. Each scope holds it for a few
        // registry reads and its own wait for v1.12.0's mutex: the short
        // bound once the name is marked, up to 5 s scaled before that, and,
        // against an owner read as SYSTEM or Administrators, as long as the
        // acquisition's own bound allows. The per-entry acquisitions give up
        // after 30 s scaled (RegistryLedger.RestoreLedgerTimeoutMs) and leave
        // the entry for the next sweep. The one for the empty keys has no
        // bound of its own.
        var released = own.Controllers.Where(c => c.TransportIndex >= 0).Select(c => c.TransportIndex).ToList();
        if (released.Count > 0) try { RegistryLedger.Release(released); } catch { }
        return true;
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
    /// until every group it removes and every orphaned HID child it removes
    /// after them is out of the device tree, or that wait has ended. A
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
        // The dead records of groups whose removal was not confirmed. Each
        // stays, as the exit handler keeps its own, so the device is still a
        // dead owner's to the next sweep: without a record and once its
        // driver lets go of the acknowledgement section, the census reads it
        // as Unknown, which no sweep removes.
        var keepRecords = new HashSet<string>(StringComparer.Ordinal);
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
                int unsettled = 0;
                RemoveGroups(targets, group =>
                {
                    bool settled = false;
                    try { settled = ReclaimGroup(group, liveIndices); }
                    finally
                    {
                        if (!settled)
                        {
                            unsettled++;
                            if (group.Record != null) keepRecords.Add(group.Record.OwnerId);
                        }
                    }
                });
                if (!RemoveOrphanChildren()) unsettled++;
                if (unsettled > 0)
                    DeviceOrchestrator.LogDiag($"    reclaim: {unsettled} removal(s) not confirmed, left to the next sweep");
            }
        }
        // Released only here, after every group's removal and the orphan HID
        // children's: a HID child's id follows from its parent's, so a parent
        // registered again while one of its old children is still being
        // removed brings back that child's id too.
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
            if (settled && !keepRecords.Contains(rec.OwnerId)) MachineClaims.DeleteRecord(rec.OwnerId);
        }
        Usbip.UsbipBackend.DetachImportsOfDeadServers(usbip);

        if (targets.Count > 0 || census.DeadRecords.Any())
            try { RegistryLedger.PruneDead(); } catch { }
    }

    /// <summary>A dead owner's group, under its claims: its devices, then
    /// its index's configuration when they are out of the device tree and no
    /// other present device and no live record uses the index. A v1.12.0
    /// program that started on the index since the claims were taken holds
    /// its owner pipe, and keeps both. The check before the removal only
    /// reads that pipe (WaitNamedPipe), so it can skip a removal and never
    /// starts one. Returns false when a removal was not confirmed.</summary>
    private static bool ReclaimGroup(OwnerCensus.DeviceGroup group, HashSet<int> liveIndices)
    {
        int index = group.ControllerIndex;
        if (OwnerCensus.LegacyOwnerRuns(index, group.InstanceIds))
        {
            DeviceOrchestrator.LogDiag($"    reclaim skipped {group.Key} index {index}: a v1.12.0 program holds the index now");
            return true;
        }
        DeviceOrchestrator.LogDiag($"    reclaim {group.Owner} {group.Key} index {index}: {string.Join(", ", group.InstanceIds)}");
        if (!RemoveGroup(group)) return false;
        if (index >= 0 && !liveIndices.Contains(index) && !OwnerCensus.LegacyOwnerRuns(index, group.InstanceIds))
            DeviceOrchestrator.DeleteInstanceConfig(index);
        return true;
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

    /// <summary>Each group is one controller's own devnodes and index,
    /// removed one group after another, and a group that throws is logged
    /// and does not stop the others. A group the PnP lock's bound refuses
    /// (RemoveGroup) does not stop them either: the next takes the lock
    /// again. During the exit a spent budget refuses it at once unless the
    /// lock is free, and outside it each group waits up to ten
    /// minutes.</summary>
    private static void RemoveGroups(List<OwnerCensus.DeviceGroup> groups, Action<OwnerCensus.DeviceGroup> remove)
    {
        foreach (var g in groups)
        {
            try { remove(g); }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    removing {g.Key} threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>One controller's devnodes, the main node first so its
    /// children cascade, then whatever companion is left. Returns true once
    /// none of them and none of the children they had is in the device
    /// tree.
    ///
    /// <para>A program that refuses the main node's removal query, as one
    /// that keeps its HID child open without watching it does, has the
    /// device failed by its driver through the index's remove event
    /// (DeviceManager.RemoveDevice). The index is this removal's to fail:
    /// a sweep holds its claim, and the exit handler removes its own
    /// controllers.</para></summary>
    private static bool RemoveGroup(OwnerCensus.DeviceGroup group)
    {
        var removed = new List<string>();
        int index = group.ControllerIndex;
        foreach (string id in group.InstanceIds.OrderBy(id => id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            removed.Add(id);
            try
            {
                removed.AddRange(DeviceManager.ChildIdsOf(id));
                bool root = id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase);
                DeviceManager.RemoveDevice(id, timeoutMs: 120_000, forceFallbacks: true,
                    failDevice: root && index >= 0 ? () => SharedMemoryIO.RequestSurpriseRemovalOfIndex(index, TimeoutScale.Apply(2000)) : null);
            }
            catch (DeviceManager.PnpLockBusyException)
            {
                // A take's bound on its wait for the lock ran out: the exit's,
                // or ten minutes outside the exit. The group, and the record
                // that names it, stay for the next sweep.
                DeviceOrchestrator.LogDiag($"    the PnP lock was held past this take's bound, so {id} stays for the next sweep");
                return false;
            }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    reclaim RemoveDevice({id}) threw {ex.GetType().Name}: {ex.Message}"); }
        }
        return AllGone(removed);
    }

    /// <summary>How long a sweep or the exit handler waits for each orphaned
    /// HID child it removed to leave the device tree.</summary>
    private const int OrphanRemovalWaitMs = 10_000;

    /// <summary>The HID orphan sweep, which tests each removal for 2 s under
    /// the PnP lock, then up to 10 s more for any child it removed that is
    /// still there. Returns true once every one of them is gone.</summary>
    private static bool RemoveOrphanChildren()
    {
        var removed = new List<string>();
        try { DeviceManager.RemoveOrphanHidChildren(removed); }
        catch (DeviceManager.PnpLockBusyException)
        {
            DeviceOrchestrator.LogDiag("    the PnP lock was held past this take's bound, so the orphaned HID children stay for the next sweep");
            return false;
        }
        catch { }
        return OrphansGone(removed);
    }

    /// <summary>The wait for each orphaned HID child a sweep removed, then
    /// <see cref="AllGone"/> over them. An orphan is a HID id the sweep
    /// uninstalled, so its record has to go too.</summary>
    internal static bool OrphansGone(List<string> removed)
    {
        foreach (string id in removed)
            DeviceManager.WaitRemovalComplete(id, uninstalls: true, TimeoutScale.Apply(OrphanRemovalWaitMs));
        return AllGone(removed);
    }

    /// <summary>True when every removal has finished, by the SDK's one test
    /// of a finished removal (<see cref="DeviceManager.WaitRemovalComplete"/>),
    /// asked per id with the short budget of a removal that has already run
    /// (DeviceManager.QuickRemovalMs): a devnode still in the tree gets that
    /// long for its REMOVED notification. A software device's record is not
    /// asked for, since SwDeviceClose keeps it.</summary>
    internal static bool AllGone(List<string> ids)
    {
        bool gone = true;
        foreach (string id in ids)
        {
            if (DeviceManager.WaitRemovalComplete(id, uninstalls: !DeviceManager.KeepsRecord(id),
                    TimeoutScale.Apply(DeviceManager.QuickRemovalMs))) continue;
            DeviceOrchestrator.LogDiag($"    {id} is still in the device tree");
            gone = false;
        }
        return gone;
    }

    private static int s_phantomCleanupStarted;
    private static Thread? s_phantomCleanup;

    /// <summary>How long one cleanup run waits in all for the phantoms it
    /// uninstalled to leave Windows' records. One budget for the run rather
    /// than one per id, so a create queued behind the cleanup waits for the
    /// uninstalls and at most this much more. Half the exit's budget, so an
    /// exit that joins the cleanup outlasts it (<see cref="ExitWaitMs"/>).</summary>
    internal const int UninstallWaitBudgetMs = ExitWaitMs / 2;

    /// <summary>What the last cleanup run of this process did, for the
    /// probe that times the first create behind it. Null before one ends.</summary>
    internal static UninstallWait.Report? LastPhantomCleanup { get; private set; }

    /// <summary>The phantom-record cleanup, once per process, off the
    /// create path. It takes <see cref="CreateGate"/> for writing, so it
    /// waits for this process's creates in flight and holds the next ones
    /// back until it is done: a create, and the uninstall-grade cleanup,
    /// wait while it runs, through one pass over the phantoms, their
    /// uninstalls, at most <see cref="UninstallWaitBudgetMs"/> of waiting
    /// in all, for the PnP lock before each uninstall and for Windows after
    /// it, and two interface passes that wait for the lock within the same
    /// budget. After each
    /// uninstall it waits, still holding the gate and the phantom's identity
    /// claim, until Windows no longer knows the id
    /// (<see cref="UninstallWait"/>), so neither this process's next create
    /// nor another program's on this release registers it again while the
    /// uninstall is still going. A phantom whose identity claim another
    /// program holds is that program's create in flight, and is left. The exit handler joins the thread
    /// (<see cref="JoinPhantomCleanup"/>) within its own bound.
    ///
    /// <para>Once per process. The first of InstallDriver, the first create
    /// and RemoveAllVirtualControllers starts it, the call that starts it
    /// returns without waiting for it to end, and every later call removes
    /// no phantom. Each teardown leaves a phantom record
    /// (DeviceManager.RemoveAccumulatedHmPhantoms), visible only with hidden
    /// devices shown, and the ones left after the run started go with the
    /// next process's first cleanup.</para>
    ///
    /// <para>A first call made while the process exits, inside a ProcessExit
    /// handler of the program's own, registers the exit handler too late to
    /// run: AppDomain.OnProcessExit invokes the delegate it read when the
    /// raise began. A thread started then would end with the process,
    /// possibly inside an uninstall. So when <see cref="ProcessExitSignal"/>
    /// says the process exits, the call runs the cleanup on the calling
    /// thread before it returns, as RemoveOrphaned ran its phantom pass
    /// before this release: up to <see cref="ExitWaitMs"/> for the gate, its
    /// uninstalls, and at most <see cref="UninstallWaitBudgetMs"/> of
    /// waiting. A thread that holds the gate's read side, inside a create,
    /// cannot take it and runs none. The one exit the signal misses, under
    /// .NET, is a program in which no code that refers to the SDK ran or was
    /// compiled before exit began, such as one that uses the SDK only inside
    /// its own exit handler: the thread starts as on any other call and ends
    /// with the process. What an uninstall cut off there leaves is not
    /// verified: Learn documents SetupDiRemoveDevice's success only, when
    /// "all traces of the device instance are deleted from the registry". A
    /// record it leaves that still names HIDMaestro is a phantom, which the
    /// next process's first cleanup walks and removes.</para></summary>
    internal static void StartPhantomCleanupOnce()
    {
        if (Interlocked.Exchange(ref s_phantomCleanupStarted, 1) != 0) return;
        if (ProcessExitSignal.Raised)
        {
            DeviceOrchestrator.LogDiag("    phantom cleanup: the process is exiting, so it runs on the calling thread");
            RunPhantomCleanupWithin(TimeoutScale.Apply(ExitWaitMs));
            return;
        }
        var t = new Thread(RunPhantomCleanup)
        { IsBackground = true, Name = "HIDMaestro phantom cleanup", Priority = ThreadPriority.BelowNormal };
        // Published once started: Join throws on a thread not yet started.
        t.Start();
        Volatile.Write(ref s_phantomCleanup, t);
    }

    /// <summary>One cleanup run, under the gate. The probe runs it on a
    /// thread of its own, with the seams set, to watch the gate.</summary>
    internal static void RunPhantomCleanup() => RunPhantomCleanupWithin(Timeout.Infinite);

    /// <summary>The run, waiting at most <paramref name="gateWaitMs"/> for
    /// the gate. A run that does not get it, or runs on a thread that holds
    /// the read side (TryEnterWriteLock throws LockRecursionException
    /// there), removes nothing and leaves the phantoms to the next
    /// process.</summary>
    private static void RunPhantomCleanupWithin(int gateWaitMs)
    {
        bool gated;
        try { gated = CreateGate.TryEnterWriteLock(gateWaitMs); }
        catch (LockRecursionException) { gated = false; }
        if (!gated)
        {
            DeviceOrchestrator.LogDiag("    phantom cleanup: a create of this process holds the gate past the bound, or this thread is inside one, so the phantoms stay for the next process");
            return;
        }
        s_cleanupHasGate = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var wait = new UninstallWait();
            int phantoms = 0;
            try { phantoms = DeviceManager.RemoveAccumulatedHmPhantoms(id => wait.Wait(id), () => s_exiting, wait.Lock); } catch { }
            // The interface entries of real devices: nothing of them stands
            // in for a device under the probe.
            if (DeviceManager.PhantomSource == null)
            {
                try { DeviceOrchestrator.RemoveGhostXusbInterfaces(wait.Lock); } catch { }
                try { DeviceOrchestrator.RemoveOwnedInterfaceEntries(phantomOnly: true, wait.Lock); } catch { }
            }
            LastPhantomCleanup = wait.Summary(phantoms, sw.ElapsedMilliseconds);
            DeviceOrchestrator.LogDiag($"    phantom cleanup: {phantoms} removed in {sw.ElapsedMilliseconds}ms, " +
                $"{wait.WaitedMs}ms waiting for {wait.Ids} uninstall(s), {wait.Unsettled} still known");
        }
        finally
        {
            s_cleanupHasGate = false;
            CreateGate.ExitWriteLock();
        }
    }

    /// <summary>True while the phantom cleanup holds the gate, so the exit
    /// handler's log names what held it: the cleanup's own uninstalls, or a
    /// create of this process that the cleanup was still waiting behind.</summary>
    private static volatile bool s_cleanupHasGate;

    /// <summary>Wait for the phantom cleanup, if this process started one,
    /// up to <paramref name="timeoutMs"/>. Returns false when the bound
    /// passed with the cleanup still running.</summary>
    internal static bool JoinPhantomCleanup(int timeoutMs)
    {
        Thread? t = Volatile.Read(ref s_phantomCleanup);
        if (t == null || t == Thread.CurrentThread) return true;
        // An exit raised inside a create holds the gate for reading, and the
        // cleanup cannot take it, so waiting would only spend the budget.
        // Only NativeAOT runs the handler on that thread. Under .NET it runs
        // on the finalizer thread, which holds no read side, and the join
        // below waits.
        if (CreateGate.IsReadLockHeld)
        {
            DeviceOrchestrator.LogDiag("    exit: raised inside a create, so the record and every device stay for the next sweep");
            return false;
        }
        if (t.Join(timeoutMs)) return true;
        DeviceOrchestrator.LogDiag(s_cleanupHasGate
            ? "    exit: the phantom cleanup still holds the gate in an uninstall at the end of the exit's budget, so the record and every device stay for the next sweep"
            : "    exit: the phantom cleanup is still waiting for a create of this process at the end of the exit's budget, so the record and every device stay for the next sweep");
        return false;
    }

    /// <summary>The waits of one cleanup run, within one budget of
    /// <see cref="UninstallWaitBudgetMs"/>, each through the SDK's one test
    /// of a finished removal (<see cref="DeviceManager.WaitRemovalComplete"/>).</summary>
    internal sealed class UninstallWait
    {
        private readonly long _budget = TimeoutScale.Apply(UninstallWaitBudgetMs);
        private long _waitedTicks;

        public int Ids { get; private set; }
        public int Unsettled { get; private set; }
        public long WaitedMs => _waitedTicks * 1000 / Stopwatch.Frequency;

        /// <summary>A run: phantoms removed, its length, the uninstalls
        /// waited for, the time they took, and how many were still known
        /// when the budget ran out.</summary>
        internal sealed record Report(int Removed, long TotalMs, int Ids, long WaitedMs, int Unsettled);

        public Report Summary(int removed, long totalMs) => new(removed, totalMs, Ids, WaitedMs, Unsettled);

        /// <summary>The PnP lock for one uninstall, which every uninstall of
        /// the SDK holds. Waiting for another program's hold comes out of
        /// the run's budget, and a wait behind this process's own hold does
        /// not. Null when another program held it past what is left, or,
        /// during the exit, when one hold of this process's own lasted
        /// longer than what was left when it asked: the phantom stays for
        /// the next run.</summary>
        public DeviceManager.PnpLockHold? Lock()
        {
            int left = (int)Math.Max(0, _budget - WaitedMs);
            var hold = DeviceManager.TryHoldPnpLock(left, out long waited);
            _waitedTicks += waited;
            if (hold == null)
                DeviceOrchestrator.LogDiag($"    the PnP lock did not come within the {left} ms left of the cleanup's waiting: another program held it that long, or, during the exit, one hold of this process's own outlasted it. A phantom waits for the next run");
            return hold;
        }

        /// <summary>Wait until Windows no longer knows
        /// <paramref name="instanceId"/>, with what is left of the run's
        /// budget. Returns false when it ran out with the id still known.</summary>
        public bool Wait(string instanceId)
        {
            Ids++;
            long start = Stopwatch.GetTimestamp();
            int left = (int)Math.Max(0, _budget - WaitedMs);
            bool done = DeviceManager.WaitRemovalComplete(instanceId, uninstalls: true, left);
            _waitedTicks += Stopwatch.GetTimestamp() - start;
            if (done) return true;
            Unsettled++;
            DeviceOrchestrator.LogDiag($"    {instanceId} is still known after its uninstall, and the cleanup's {_budget} ms of waiting are spent");
            return false;
        }
    }
}
