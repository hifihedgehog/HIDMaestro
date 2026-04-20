using System;
using System.Collections.Generic;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// Override the OEM-name label shown by <c>joy.cpl</c> and DirectInput
/// consumers for a given USB VID:PID.
///
/// <para>The label surfaces from three registry locations, and Windows
/// pre-populates at least one of them for common clone PIDs. To reliably
/// override, all three must be written:</para>
///
/// <list type="bullet">
///   <item><c>HKLM\...\DirectInput\VID_####&amp;PID_####\OEM\"OEM Name"</c>
///         (value name has a space; read by DirectInput consumers).</item>
///   <item><c>HKLM\...\Joystick\OEM\VID_####&amp;PID_####\"OEMName"</c>
///         (value name has NO space; MME joy.cpl reads here).</item>
///   <item><c>HKCU\...\Joystick\OEM\VID_####&amp;PID_####\"OEMName"</c>
///         (per-user; takes precedence over HKLM Joystick and is what
///         Windows preloads with clone labels such as
///         "PC TWIN SHOCK Gamepad" for VID_0079&amp;PID_0006).</item>
/// </list>
///
/// <para>
/// <see cref="Set"/> writes all three paths in a single transaction under a
/// global mutex and captures the prior value of each target to a
/// HIDMaestro-owned pending record at
/// <c>HKLM\SOFTWARE\HIDMaestroOemOverrides\VID_xxxx&amp;PID_xxxx</c>
/// BEFORE any target is mutated. <see cref="Clear"/> and
/// <see cref="RecoverOrphans"/> replay the record to restore all three
/// targets independently.</para>
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
///   <item>The HKLM DirectInput and HKLM Joystick targets are system-wide
///         per VID:PID. If a real device with that VID:PID is connected
///         while your override is active, DirectInput consumers see the
///         override label for it too.</item>
///   <item>The HKCU Joystick target is per-calling-user. On a single-user
///         workstation that matches the DirectInput scope visually; on a
///         multi-user machine, only the user who called <see cref="Set"/>
///         sees the joy.cpl label change — the HKLM paths still carry the
///         override for DirectInput consumers regardless of user.</item>
///   <item>DirectInput and joy.cpl both cache OEM names per-process on first
///         enumeration. A joy.cpl window that was already open when the
///         override changes keeps showing the stale label until it is
///         closed and re-opened. Games that were already running behave
///         the same way. Set before opening joy.cpl or launching games.</item>
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
