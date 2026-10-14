using System;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// What one controller holds machine-wide while it lives: a transport index,
/// the claim on it, and its entry in this process's owner record. Taken
/// before any PnP work, released after the teardown.
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
    private int _released;

    private ControllerLease(int transportIndex, DeviceIdentity identity, SafeFileHandle indexClaim)
    {
        TransportIndex = transportIndex;
        Identity = identity;
        _indexClaim = indexClaim;
    }

    /// <summary>Take a transport index for a controller at
    /// <paramref name="slot"/>, and record it.</summary>
    /// <exception cref="HMResourceInUseException">Every candidate index is held.</exception>
    public static ControllerLease Acquire(int slot, string? identityKey, string backend, string profileId)
    {
        MachineClaims.EnsureOwner();
        // The slot is only a placeholder here: a keyed identity does not
        // depend on where it lands.
        DeviceIdentity? keyedIdentity = string.IsNullOrWhiteSpace(identityKey)
            ? null : DeviceIdentity.ForKey(identityKey!, slot);

        OwnerCensus census = OwnerCensus.Take();
        HashSet<int> used = census.IndicesInUse();
        // A device of this identity that a dead or unknown owner left is
        // about to be reclaimed by this create, so its index does not block.
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

                DeviceIdentity identity = keyedIdentity != null
                    ? DeviceIdentity.ForKey(identityKey!, n)
                    : DeviceIdentity.ForIndex(n);
                MachineClaims.AddController(identity.Token, n, backend, profileId);
                keep = true;
                return new ControllerLease(n, identity, indexClaim);
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

    /// <summary>Slot first, so a lone program keeps its numbers, then upward.</summary>
    private static IEnumerable<int> Candidates(int slot)
    {
        yield return slot;
        for (int n = 0; n < 4096; n++)
            if (n != slot) yield return n;
    }

    /// <summary>The entry goes, then the claim. Called after the
    /// controller's devices are gone.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _released, 1) != 0) return;
        try { MachineClaims.RemoveController(Identity.Token); } catch { }
        _indexClaim?.Dispose();
        _indexClaim = null;
    }
}
