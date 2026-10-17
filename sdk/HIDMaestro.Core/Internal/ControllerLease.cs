using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// What one controller holds machine-wide while it lives: a transport index,
/// an identity, the claim on each, and its entry in this process's owner
/// record. Taken before any PnP work, released after the teardown.
///
/// <para>The transport index names every section, event, pipe and registry
/// key of the controller, and is the <c>ControllerIndex</c> the driver
/// reads. It is the controller's slot number when that index is free on the
/// machine, so a program running alone gets the numbers it always got, and
/// the lowest free index otherwise. A keyed create whose identity has a
/// device a dead or unknown owner left tries that device's index first,
/// when it is at or above the floor and no one else uses it. While a v1.12.0
/// program runs, whose next create takes the lowest index free among its
/// own controllers, the floor is <see cref="LegacyClearance"/>: the search
/// tries it plus the slot first, then goes upward from it, then below it.
/// An index is free when its claim can be taken, no v1.12.0 program holds
/// its owner pipe, no present device records it, no USB/IP import of a
/// program without an owner record uses it, and none of its named sections and events was made by a program that
/// is not elevated. Whether its input section exists says nothing: the driver host
/// of a removed device can hold a section after the removal, until a
/// process that called XInput on the device polls XInput again or exits.</para>
/// </summary>
internal sealed class ControllerLease : IDisposable
{
    public int TransportIndex { get; }
    public DeviceIdentity Identity { get; }
    private SafeFileHandle? _indexClaim;
    private SafeFileHandle? _identityClaim;
    private int _released;

    // Every lease until its Dispose. The claims are SafeFileHandles, which
    // garbage collection closes. Only the controller holds its lease, and a
    // controller whose output mapping failed has no thread that keeps it
    // reachable, so a program that drops its context without disposing it
    // would lose the claims while the devices and the record entry stay,
    // and another program could take the identity and remove them. So a
    // context dropped without Dispose keeps its controllers' claims until the
    // process exits, and a create of the same identity key in this process
    // throws HMResourceInUseException until then.
    private static readonly HashSet<ControllerLease> s_live = new();

    private ControllerLease(int transportIndex, DeviceIdentity identity, SafeFileHandle indexClaim, SafeFileHandle identityClaim)
    {
        TransportIndex = transportIndex;
        Identity = identity;
        _indexClaim = indexClaim;
        _identityClaim = identityClaim;
        lock (s_live) s_live.Add(this);
    }

    /// <summary>Take a transport index and an identity for a controller at
    /// <paramref name="slot"/>, and record it.</summary>
    /// <exception cref="HMResourceInUseException">A live program holds the
    /// identity, a running v1.12.0 program's present device has it, the key
    /// is <c>index:&lt;N&gt;</c> with N below the floor a running v1.12.0
    /// program sets (a device a closed one left sets none), or every
    /// candidate index is held.</exception>
    public static ControllerLease Acquire(int slot, string? identityKey, string backend, string profileId)
    {
        MachineClaims.EnsureOwner();
        bool keyed = !string.IsNullOrWhiteSpace(identityKey);
        DeviceIdentity? keyedIdentity = null;
        SafeFileHandle? identityClaim = null;
        try
        {
            OwnerCensus census = OwnerCensus.Take();
            if (keyed)
            {
                // The slot is only a placeholder here: a keyed identity
                // does not depend on where it lands.
                keyedIdentity = DeviceIdentity.ForKey(identityKey!, slot);
                // Refused before the claim, so a refused create never first
                // waits for a sweep that holds the identity.
                RefuseDefaultBelowFloor(keyedIdentity, identityKey!, Floor(census));
                identityClaim = ClaimIdentity(keyedIdentity, identityKey!.Trim(), ref census);
            }

            HashSet<int> used = census.IndicesInUse();
            // A device of this identity that a dead or unknown owner left
            // is about to be reclaimed by this create, so its index does not
            // block, unless another device or a live program has that index
            // too. The create lands on it first: the identity's companion
            // and sections then keep the index they had.
            int reclaimed = -1;
            if (keyedIdentity != null)
                foreach (var g in census.Devices)
                {
                    if (!string.Equals(g.Key, keyedIdentity.Token, StringComparison.OrdinalIgnoreCase)
                        || g.Owner is OwnerCensus.Owner.Live or OwnerCensus.Owner.LiveLegacy or OwnerCensus.Owner.Self
                        || g.ControllerIndex < 0)
                        continue;
                    int k = g.ControllerIndex;
                    bool shared = census.Devices.Any(o => !ReferenceEquals(o, g) && o.ControllerIndex == k)
                        || census.ForeignImportIndices.Contains(k)
                        || census.Records.Any(r => r.Live && r.Controllers.Any(c => c.TransportIndex == k));
                    if (shared) continue;
                    used.Remove(k);
                    reclaimed = k;
                }

            // A running v1.12.0 program's next create takes the lowest index
            // free among its own controllers, whatever else runs, and
            // replaces a devnode of that index's default identity as its own
            // prior life (b4d29d9 HMContext.CreateController and
            // DeviceNodeCreator). While one runs, this create starts at
            // LegacyClearance, above every index that program reaches with
            // fewer controllers than that. Starting lower lets that program's
            // next create replace this one's devnode. The cost is everything
            // derived from the index: a keyless controller's token, paths and
            // container id, a keyless composite's USB serial and USB instance
            // id (DeviceIdentity.SerialVariant), and for every controller the
            // HID serial HM-CTL-<index>, the DualSense and Switch Pro MAC
            // bytes, a byte of the XUSB capabilities and the USB/IP bus id. A program
            // restarted after a crash takes its old index back only when it
            // is at least LegacyClearance.
            int floor = Floor(census);
            if (floor > 0)
                DeviceOrchestrator.LogDiag($"    a v1.12.0 program runs: transport indices from {floor}");
            // Again, for the census a wait for a sweep took again.
            if (keyedIdentity != null) RefuseDefaultBelowFloor(keyedIdentity, identityKey!, floor);
            // Indices skipped for an object this release did not create, and
            // for an object nothing about whose maker was read.
            int squattedCount = 0, unreadCount = 0;
            string? firstOwnerRead = null, firstNotOurs = null, firstUnread = null;
            int floorAtStart = floor;
            foreach (int n in Candidates(reclaimed >= floor ? reclaimed : floor + slot, floor))
            {
                if (used.Contains(n)) continue;
                // A v1.12.0 program seen only after a wait below raised the
                // floor: no index under it is taken from then on.
                if (floor > floorAtStart && n < floor) continue;
                SafeFileHandle? indexClaim = MachineClaims.TryClaim(MachineClaims.IndexPipeName(n));
                if (indexClaim == null) continue;
                bool keep = false;
                try
                {
                    if (MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(n))) continue;
                    if (SharedMemoryIO.ForeignObjectAt(n) is { } squatted)
                    {
                        switch (squatted.Finding)
                        {
                            case SharedMemoryIO.Finding.OwnerRead: squattedCount++; firstOwnerRead ??= squatted.Description; break;
                            case SharedMemoryIO.Finding.NotOurs: squattedCount++; firstNotOurs ??= squatted.Description; break;
                            default: unreadCount++; firstUnread ??= squatted.Description; break;
                        }
                        DeviceOrchestrator.LogDiag($"    transport index {n} skipped: {squatted.Description}");
                        continue;
                    }

                    DeviceIdentity identity;
                    if (keyedIdentity != null)
                    {
                        identity = DeviceIdentity.ForKey(identityKey!, n);
                    }
                    else
                    {
                        identity = DeviceIdentity.ForIndex(n);
                        // Held in identityClaim like a keyed one from here,
                        // so the finally below releases it on every exit
                        // that does not hand it to the lease.
                        string identityName = MachineClaims.IdentityPipeName(identity.Token);
                        identityClaim = MachineClaims.TryClaim(identityName);
                        // A sweep holds this claim while it removes what the
                        // identity left (a dead program's group, a phantom,
                        // an orphaned HID child), and no live record lists
                        // the token then. That claim is waited for, as a keyed
                        // create waits for it, rather than moving to the next
                        // index: the index decides a keyless controller's
                        // identity, so moving on would give it another one.
                        if (identityClaim == null && (identityClaim = ClaimAfterSweep(identity.Token, identityName)) != null)
                        {
                            // The wait can last 120 s, so what index n was
                            // checked against above is read again: a v1.12.0
                            // program that started on n in the meantime, before
                            // its device appeared, holds n's owner pipe, and
                            // one anywhere raises the floor.
                            census = OwnerCensus.Take();
                            floor = Math.Max(floor, Floor(census));
                            if ((n < floor && floor > floorAtStart)
                                || MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(n))
                                || SharedMemoryIO.ForeignObjectAt(n) != null)
                            {
                                DeviceOrchestrator.LogDiag($"    transport index {n} skipped after the wait for a sweep: a v1.12.0 program or another object holds it now");
                                identityClaim.Dispose();
                                identityClaim = null;
                                continue;
                            }
                        }
                        if (identityClaim == null) continue;   // someone passed "index:N" explicitly
                        if (LiveLegacyHolds(census, identity.Token)) { identityClaim.Dispose(); identityClaim = null; continue; }
                    }

                    try { MachineClaims.AddController(identity.Token, n, backend, profileId); }
                    catch
                    {
                        // A write that failed partway leaves part of the
                        // entry. This create holds the identity's claim, so
                        // no live controller has an entry under its token.
                        MachineClaims.RemoveController(identity.Token);
                        throw;
                    }
                    var lease = new ControllerLease(n, identity, indexClaim, identityClaim!);
                    keep = true;
                    identityClaim = null;   // owned by the lease from here
                    return lease;
                }
                finally
                {
                    if (!keep) indexClaim.Dispose();
                }
            }
            string blocked;
            if (squattedCount == 0 && unreadCount == 0)
                blocked = "No transport index is free: every index up to 4096 is held by a running program or a present device.";
            else
            {
                var text = new System.Text.StringBuilder("No transport index is free: of the indices up to 4096, ");
                if (squattedCount > 0)
                {
                    // An object whose owner was read is named first, and
                    // v1.12.0 is offered as a possible maker only when an
                    // owner it could have left was read.
                    text.Append($"{squattedCount} carry a Global\\ object this release did not create ({firstOwnerRead ?? firstNotOurs}). ");
                    text.Append(firstOwnerRead != null
                        ? "Another program made each, such as one that is not elevated or one built on HIDMaestro 1.12.0 or older. "
                        : "Another program made each. ");
                }
                if (unreadCount > 0)
                    text.Append($"{(squattedCount > 0 ? "Of the others, " : "")}{unreadCount} carry a Global\\ name whose object showed nothing about its maker ({firstUnread}). ");
                text.Append("The rest are held by a running program or a present device. Close those programs, or create the controller again once they are gone.");
                blocked = text.ToString();
            }
            throw new HMResourceInUseException(blocked, "transport index", null);
        }
        finally
        {
            identityClaim?.Dispose();
        }
    }

    /// <summary>The preferred index first, the slot or a reclaimed
    /// identity's, so a lone program keeps its numbers, then upward from
    /// <paramref name="floor"/>, then the indices below it.</summary>
    private static IEnumerable<int> Candidates(int first, int floor)
    {
        yield return first;
        for (int n = floor; n < 4096; n++)
            if (n != first) yield return n;
        for (int n = 0; n < floor; n++)
            if (n != first) yield return n;
    }

    /// <summary>The first transport index a create takes while a v1.12.0
    /// program runs. Such a program reaches it only with this many
    /// controllers alive at once.</summary>
    internal const int LegacyClearance = 64;

    private static int Floor(OwnerCensus census) => LegacyProgramRuns(census) ? LegacyClearance : 0;

    /// <summary>A key of the form index:N is index N's default identity
    /// wherever the controller lands (DeviceIdentity.ForKey): the devnode
    /// name a v1.12.0 program gives its controller on index N, which its next
    /// create replaces as its own prior life. The floor cannot move that
    /// name, so below the floor the create is refused.</summary>
    private static void RefuseDefaultBelowFloor(DeviceIdentity identity, string key, int floor)
    {
        if (!identity.IsDefault || identity.Index >= floor) return;
        throw new HMResourceInUseException(
            $"Identity key '{key.Trim()}' is the default identity of transport index {identity.Index}, and a running program " +
            $"built on HIDMaestro 1.12.0 that has a controller present gives that identity to its own controller on index {identity.Index}, " +
            "replacing any device that has it. While that program runs, create this controller with no key or with a key of your own, or close that program first.",
            $"identity key '{key.Trim()}'", null);
    }

    /// <summary>Whether a v1.12.0 program runs: a present device of one, or
    /// a USB/IP import no owner record names whose index's v1.12.0 owner
    /// pipe is held, a composite of one. A v1.12.0 program with no
    /// controller at this moment leaves no sign.
    ///
    /// <para>A v1.12.0 device whose index cannot be read, as a device whose
    /// driver install failed reads in Windows Sandbox, is no LiveLegacy
    /// device: the census cannot tie it to its owner pipe. That holds for a
    /// keyed one and for one older than identity tokens. A keyless one takes
    /// its index from its token and is judged as any other. While such a
    /// device is present, a running v1.12.0 program counts too when its
    /// registry ledger holder names a running process no live record names,
    /// on any index, or when it runs on an index no live record and no
    /// present device's index accounts for
    /// (OwnerCensus.UnplacedLegacyProgram). A device of a program that has
    /// exited leaves the floor alone, whether its index reads or not.</para>
    ///
    /// <para>The holder is the one sign of such a program on an index this
    /// release also holds: there its first create opens this release's
    /// sections and its own owner pipe fails against this release's two
    /// instances, so every named object carries this release's names. That
    /// first create is the case outside every gate
    /// (docs/design/data-driven-devices.md, "One case stays outside every
    /// gate"). A keyboard or mouse profile writes no holder, and neither
    /// does a create whose ledger write failed, so such a program there
    /// stays unseen, and its next create can land on this release's next
    /// index.</para></summary>
    private static bool LegacyProgramRuns(OwnerCensus census) =>
        census.Devices.Any(g => g.Owner == OwnerCensus.Owner.LiveLegacy)
        || census.UnplacedLegacyProgram
        || census.ForeignImportIndices.Any(i => MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(i)));

    /// <summary>A v1.12.0 program takes no identity claim. Its device of
    /// this token is present and its index's owner pipe is held.</summary>
    private static bool LiveLegacyHolds(OwnerCensus census, string token) =>
        census.Devices.Any(g => string.Equals(g.Key, token, StringComparison.OrdinalIgnoreCase)
                                && g.Owner == OwnerCensus.Owner.LiveLegacy);

    /// <summary>Claim a keyed identity, or say who holds it. After a wait
    /// for a sweep, <paramref name="census"/> is taken again: the sweep has
    /// changed what is present.</summary>
    private static SafeFileHandle ClaimIdentity(DeviceIdentity identity, string key, ref OwnerCensus census)
    {
        string name = MachineClaims.IdentityPipeName(identity.Token);
        SafeFileHandle? claim = MachineClaims.TryClaim(name);
        if (claim == null && (claim = ClaimAfterSweep(identity.Token, name)) != null)
            census = OwnerCensus.Take();
        if (claim == null)
        {
            var holder = MachineClaims.LiveHolderOf(identity.Token);
            string message = holder is { Self: true }
                ? $"Identity key '{key}' is in use by a controller of this process. Dispose that controller first. " +
                  "A controller whose context was dropped without Dispose keeps its key until this process exits: " +
                  "create this one with another key, or dispose the context before dropping it."
                : $"Identity key '{key}' is in use by {(holder == null ? "a running program" : MachineClaims.Describe(holder))}. " +
                  "Dispose that controller first, or create this one with another key.";
            throw new HMResourceInUseException(message, $"identity key '{key}'", holder);
        }
        foreach (var g in census.Devices)
        {
            if (!string.Equals(g.Key, identity.Token, StringComparison.OrdinalIgnoreCase)) continue;
            if (g.Owner != OwnerCensus.Owner.LiveLegacy) continue;
            claim.Dispose();
            throw new HMResourceInUseException(
                $"Identity key '{key}' is in use by a running program built on HIDMaestro 1.12.0, " +
                $"whose controller holds transport index {g.ControllerIndex}. Close that program first, or create this controller with another key.",
                $"identity key '{key}'", null);
        }
        return claim;
    }

    /// <summary>A sweep of another program holds an identity's claim while
    /// it removes the device a dead program left under it (OwnerSweep), and
    /// no record lists the identity then. A create holds the claim without
    /// an entry only between its claim and its entry write, milliseconds.
    /// So while no live record lists the token, the claim is waited for, up
    /// to the bound this create's own removal of that devnode would take
    /// (DeviceNodeCreator removes a prior life's devnode with 120 s). Null
    /// when a live record lists it or the bound passes.</summary>
    private static SafeFileHandle? ClaimAfterSweep(string token, string name)
    {
        var sw = Stopwatch.StartNew();
        int bound = TimeoutScale.Apply(120_000);
        while (sw.ElapsedMilliseconds < bound && MachineClaims.LiveHolderOf(token) == null)
        {
            Thread.Sleep(25);
            if (MachineClaims.TryClaim(name) is { } claim)
            {
                DeviceOrchestrator.LogDiag($"    identity {token} claimed after a sweep released it, {sw.ElapsedMilliseconds}ms");
                return claim;
            }
        }
        return null;
    }

    /// <summary>The entry goes, then the claims. Called after the
    /// controller's devices are gone.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _released, 1) != 0) return;
        try { MachineClaims.RemoveController(Identity.Token); } catch { }
        _identityClaim?.Dispose();
        _identityClaim = null;
        _indexClaim?.Dispose();
        _indexClaim = null;
        lock (s_live) s_live.Remove(this);
    }
}
