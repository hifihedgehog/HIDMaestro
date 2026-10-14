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
/// it, and no USB/IP import of a program without an owner record uses it.
/// Whether its input section exists says nothing: the driver host of a
/// removed device can hold a section after the removal, until a process
/// that called XInput on the device polls XInput again or exits.</para>
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
            // block.
            if (keyedIdentity != null)
                foreach (var g in census.Devices)
                    if (string.Equals(g.Key, keyedIdentity.Token, StringComparison.OrdinalIgnoreCase)
                        && g.Owner is not (OwnerCensus.Owner.Live or OwnerCensus.Owner.LiveLegacy or OwnerCensus.Owner.Self))
                        used.Remove(g.ControllerIndex);

            foreach (int n in Candidates(slot))
            {
                if (used.Contains(n)) continue;
                SafeFileHandle? indexClaim = MachineClaims.TryClaim(MachineClaims.IndexPipeName(n));
                if (indexClaim == null) continue;
                bool keep = false;
                try
                {
                    if (MachineClaims.IsHeld(MachineClaims.LegacyOwnerPipeName(n))) continue;

                    DeviceIdentity identity;
                    SafeFileHandle? idClaim;
                    if (keyedIdentity != null)
                    {
                        identity = DeviceIdentity.ForKey(identityKey!, n);
                        idClaim = identityClaim;
                    }
                    else
                    {
                        identity = DeviceIdentity.ForIndex(n);
                        idClaim = MachineClaims.TryClaim(MachineClaims.IdentityPipeName(identity.Token));
                        if (idClaim == null) continue;   // someone passed "index:N" explicitly
                        if (LiveLegacyHolds(census, identity.Token)) { idClaim.Dispose(); continue; }
                    }

                    MachineClaims.AddController(identity.Token, n, backend, profileId);
                    keep = true;
                    identityClaim = null;   // owned by the lease from here
                    return new ControllerLease(n, identity, indexClaim, idClaim!);
                }
                finally
                {
                    if (!keep) indexClaim.Dispose();
                }
            }
            throw new HMResourceInUseException(
                "No transport index is free: every index up to 4096 is held by a running program or a present device.",
                "transport index", null);
        }
        finally
        {
            identityClaim?.Dispose();
        }
    }

    /// <summary>Slot first, so a lone program keeps its numbers, then upward.</summary>
    private static IEnumerable<int> Candidates(int slot)
    {
        yield return slot;
        for (int n = 0; n < 4096; n++)
            if (n != slot) yield return n;
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
