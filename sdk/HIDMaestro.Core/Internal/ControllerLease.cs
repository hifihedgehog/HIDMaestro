using System;
using System.Collections.Generic;
using System.Linq;
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
/// the lowest free index otherwise. An index is free when its claim can be
/// taken, no v1.12.0 program holds its owner pipe, no present device records
/// it, no USB/IP import of a program without an owner record uses it, and
/// none of its named sections and events was made by a program that is not
/// elevated. Whether its input section exists says nothing: the driver host
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

    private ControllerLease(int transportIndex, DeviceIdentity identity, SafeFileHandle indexClaim, SafeFileHandle identityClaim)
    {
        TransportIndex = transportIndex;
        Identity = identity;
        _indexClaim = indexClaim;
        _identityClaim = identityClaim;
    }

    /// <summary>Take a transport index and an identity for a controller at
    /// <paramref name="slot"/>, and record it.</summary>
    /// <exception cref="HMResourceInUseException">A live program holds the
    /// identity, or every candidate index is held.</exception>
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
                identityClaim = ClaimIdentity(keyedIdentity, identityKey!.Trim(), census);
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

            int squattedCount = 0;
            string? firstSquatted = null;
            foreach (int n in Candidates(reclaimed >= 0 ? reclaimed : slot))
            {
                if (used.Contains(n)) continue;
                SafeFileHandle? indexClaim = MachineClaims.TryClaim(MachineClaims.IndexPipeName(n));
                if (indexClaim == null) continue;
                bool keep = false;
                try
                {
                    if (MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(n))) continue;
                    if (SharedMemoryIO.ForeignObjectAt(n) is { } squatted)
                    {
                        squattedCount++;
                        firstSquatted ??= squatted;
                        DeviceOrchestrator.LogDiag($"    transport index {n} skipped: {squatted} is owned by neither SYSTEM nor Administrators");
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
                        identityClaim = MachineClaims.TryClaim(MachineClaims.IdentityPipeName(identity.Token));
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
            string blocked = squattedCount == 0
                ? "No transport index is free: every index up to 4096 is held by a running program or a present device."
                : $"No transport index is free: of the indices up to 4096, {squattedCount} carry a Global\\ object owned by neither SYSTEM nor Administrators (such as {firstSquatted}), which another program created, and the rest are held by a running program or a present device. Close the program that created it, or create the controller again once it is gone.";
            throw new HMResourceInUseException(blocked, "transport index", null);
        }
        finally
        {
            identityClaim?.Dispose();
        }
    }

    /// <summary>The preferred index first, the slot or a reclaimed
    /// identity's, so a lone program keeps its numbers, then upward.</summary>
    private static IEnumerable<int> Candidates(int first)
    {
        yield return first;
        for (int n = 0; n < 4096; n++)
            if (n != first) yield return n;
    }

    /// <summary>A v1.12.0 program takes no identity claim. Its device of
    /// this token is present and its index's owner pipe is held.</summary>
    private static bool LiveLegacyHolds(OwnerCensus census, string token) =>
        census.Devices.Any(g => string.Equals(g.Key, token, StringComparison.OrdinalIgnoreCase)
                                && g.Owner == OwnerCensus.Owner.LiveLegacy);

    /// <summary>Claim a keyed identity, or say who holds it.</summary>
    private static SafeFileHandle ClaimIdentity(DeviceIdentity identity, string key, OwnerCensus census)
    {
        SafeFileHandle? claim = MachineClaims.TryClaim(MachineClaims.IdentityPipeName(identity.Token));
        if (claim == null)
        {
            var holder = MachineClaims.LiveHolderOf(identity.Token);
            string who = holder == null ? "a running program"
                : holder.Self ? "a live controller of this process"
                : MachineClaims.Describe(holder);
            throw new HMResourceInUseException(
                $"Identity key '{key}' is in use by {who}. Dispose that controller first, or create this one with another key.",
                $"identity key '{key}'", holder);
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
    }
}
