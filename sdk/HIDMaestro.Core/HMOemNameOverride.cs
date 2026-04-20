using System;
using System.Collections.Generic;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// Override the DirectInput OEM-name label shown by <c>joy.cpl</c> and any
/// other DirectInput UI for a given USB VID:PID.
///
/// <para>Windows maintains a pre-populated OEM-name table for a number of
/// well-known VID:PIDs. DirectInput reads that table in preference to the
/// device's HID iProduct string when reporting <c>DIPROP_PRODUCTNAME</c>.
/// For example, any device claiming VID 0x0079 / PID 0x0006 is labeled
/// "PC TWIN SHOCK Gamepad" regardless of what iProduct says, because that
/// is the canonical DragonRise clone entry in the table.</para>
///
/// <para>Consumers that want joy.cpl to show a specific label for a
/// HIDMaestro virtual must overwrite that table entry. This class wraps the
/// write in a crash-safe state machine that records the prior value in a
/// HIDMaestro-owned registry hive before mutating DirectInput's key, so a
/// crashed or force-killed consumer can restore the original label on next
/// startup via <see cref="RecoverOrphans"/>.</para>
///
/// <para><b>Usage pattern:</b></para>
/// <code>
/// // Once at consumer startup, before creating any virtuals:
/// int restored = HMOemNameOverride.RecoverOrphans();
///
/// // For each virtual where you want a custom joy.cpl label:
/// HMOemNameOverride.Set(0x0079, 0x0006, "My Custom Gamepad");
/// // ... create controller, hand out to user, etc.
///
/// // At virtual teardown:
/// HMOemNameOverride.Clear(0x0079, 0x0006);
/// </code>
///
/// <para><b>Requirements and caveats:</b></para>
/// <list type="bullet">
///   <item>All methods require admin (HKLM write access). Throws
///         <see cref="UnauthorizedAccessException"/> if the caller is not
///         elevated.</item>
///   <item>The override is global per VID:PID. If a real device with that
///         same VID:PID is connected while your override is active, it also
///         displays the override label. That is usually fine because the
///         VID:PID is the reason both devices share a label.</item>
///   <item>DirectInput caches OEM names per-process on first enumeration.
///         Games that were already running when the override changes may
///         keep showing the old label until they re-enumerate. Set the
///         override before games launch.</item>
///   <item>Consumers that lock to single-instance execution can simply call
///         <see cref="RecoverOrphans"/> at startup and trust that nothing
///         else is racing them.</item>
/// </list>
/// </summary>
public static class HMOemNameOverride
{
    /// <summary>Set the DirectInput OEM-name label for (vid, pid). Captures
    /// the prior value so <see cref="Clear"/> or <see cref="RecoverOrphans"/>
    /// can restore it. If a prior Set for this VID:PID is already active,
    /// this replaces the label but keeps the originally-captured value for
    /// restore purposes.</summary>
    /// <param name="vid">USB Vendor ID.</param>
    /// <param name="pid">USB Product ID.</param>
    /// <param name="label">The label to show in joy.cpl / DirectInput UIs.
    /// Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is null.</exception>
    /// <exception cref="UnauthorizedAccessException">Caller lacks admin.</exception>
    public static void Set(ushort vid, ushort pid, string label)
    {
        OemNameOverrideStore.Claim(vid, pid, label);
    }

    /// <summary>Restore the pre-Set DirectInput OEM-name label for (vid, pid).
    /// If no prior Set is tracked for this VID:PID, this is a no-op.</summary>
    /// <param name="vid">USB Vendor ID.</param>
    /// <param name="pid">USB Product ID.</param>
    /// <exception cref="UnauthorizedAccessException">Caller lacks admin.</exception>
    public static void Clear(ushort vid, ushort pid)
    {
        OemNameOverrideStore.Release(vid, pid);
    }

    /// <summary>Scan the HIDMaestro pending-override hive and restore every
    /// prior override left behind by a crashed, force-killed, or otherwise
    /// uncleanly-exited consumer. Safe to call on every startup. Returns the
    /// number of overrides restored.</summary>
    /// <exception cref="UnauthorizedAccessException">Caller lacks admin.</exception>
    public static int RecoverOrphans() =>
        OemNameOverrideStore.RecoverOrphans();

    /// <summary>Enumerate every override currently tracked by the pending
    /// hive. Useful for diagnostics or to show a user which virtuals are
    /// currently overriding their joy.cpl label.</summary>
    public static IReadOnlyList<HMOemNameOverrideEntry> ListActive()
    {
        var raw = OemNameOverrideStore.ListActive();
        var result = new List<HMOemNameOverrideEntry>(raw.Count);
        foreach (var (vidPid, original, existed) in raw)
        {
            result.Add(new HMOemNameOverrideEntry(vidPid, original, existed));
        }
        return result;
    }
}

/// <summary>One entry in the active-override list. See
/// <see cref="HMOemNameOverride.ListActive"/>.</summary>
public sealed class HMOemNameOverrideEntry
{
    /// <summary>The VID:PID in canonical registry form, e.g. "VID_0079&amp;PID_0006".</summary>
    public string VidPid { get; }

    /// <summary>The label that was in DirectInput's OEM table when the
    /// override was first claimed. Null if the OEM key existed but had no
    /// OEM Name value. Used to restore the pre-override state.</summary>
    public string? OriginalOemName { get; }

    /// <summary>True if the DirectInput OEM subkey existed when the override
    /// was first claimed. If false, the subkey will be deleted on restore
    /// instead of having its value reset.</summary>
    public bool OriginalKeyExisted { get; }

    internal HMOemNameOverrideEntry(string vidPid, string? original, bool existed)
    {
        VidPid = vidPid;
        OriginalOemName = original;
        OriginalKeyExisted = existed;
    }
}
