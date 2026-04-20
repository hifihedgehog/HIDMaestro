using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Crash-safe registry state machine for DirectInput OEM-name overrides.
///
/// Windows ships a pre-populated table at
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_####&amp;PID_####\OEM\OEM Name</c>
/// which joy.cpl (a DirectInput UI) reads in preference to the device's
/// HID-reported iProduct string. For some VID:PIDs that label is
/// unhelpful (e.g. VID 0x0079 PID 0x0006 is pre-labeled "PC TWIN SHOCK
/// Gamepad" even though the device's iProduct may say something else).
///
/// Consumers that want joy.cpl to show a specific label override that
/// registry value. To keep the override safe under crash, power loss,
/// force-kill, or missed uninstaller cleanup, this store:
///
/// <list type="number">
///   <item>Records the prior value in a HIDMaestro-owned registry hive
///         (<c>HKLM\SOFTWARE\HIDMaestroOemOverrides\VID_xxxx&amp;PID_xxxx</c>)
///         BEFORE mutating the DirectInput key. If we crash before the
///         mutation, the record's target value is already in place and
///         restore is a no-op. If we crash after, the record drives the
///         restore.</item>
///   <item>Acquires a global mutex around every claim, release, and
///         recovery sweep so concurrent SDK consumers serialize access.</item>
///   <item>Provides <see cref="RecoverOrphans"/> that replays every
///         pending record from a prior process and restores the DirectInput
///         key. Consumers call this once at startup.</item>
/// </list>
///
/// All methods require admin (HKLM write). Callers that lack admin should
/// catch <see cref="UnauthorizedAccessException"/> and decide how to
/// surface the failure.
/// </summary>
internal static class OemNameOverrideStore
{
    private const string DirectInputRoot =
        @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput";
    // NOTE: deliberately NOT under HKLM\SOFTWARE\HIDMaestro. That subtree is
    // wiped wholesale by DeviceOrchestrator.RemoveAllVirtualControllers as
    // part of normal cleanup, which would stomp our pending records on every
    // SDK restart. This hive is a sibling path, orthogonal to the driver's
    // lifecycle.
    private const string PendingRoot = @"SOFTWARE\HIDMaestroOemOverrides";
    private const string MutexName = @"Global\HIDMaestro-OEM-Recovery";
    private const string OemSubkey = "OEM";
    private const string OemNameValue = "OEM Name";
    private const string StoreOriginalName = "OriginalOemName";
    private const string StoreOriginalExisted = "OriginalKeyExisted";
    private const string StoreClaimedAt = "ClaimedAtFileTime";

    /// <summary>Claim an OEM-name override for (vid, pid). If a prior override
    /// already exists from this process or another one, it is replaced; the
    /// original-value record is preserved (not overwritten to point at our
    /// replacement value).</summary>
    public static void Claim(ushort vid, ushort pid, string label)
    {
        if (label is null) throw new ArgumentNullException(nameof(label));

        string vidPid = FormatVidPid(vid, pid);
        using var mutex = AcquireMutex();
        try
        {
            // Only capture the "original" value the FIRST time we touch this
            // VID:PID. If our own pending record already exists, we're replacing
            // our own prior claim; the captured-original from that first claim
            // is still the correct thing to restore to on release.
            using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
            {
                if (pending is null)
                {
                    CaptureOriginalToPendingStore(vidPid);
                }
            }

            WriteDirectInputOemName(vidPid, label);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Release an OEM-name override for (vid, pid). Restores whatever
    /// was in the DirectInput key when the first <see cref="Claim"/> for this
    /// VID:PID happened — either writes the original string back, or deletes
    /// the OEM subkey if it didn't exist before. If no pending record exists,
    /// this is a no-op.</summary>
    public static void Release(ushort vid, ushort pid)
    {
        string vidPid = FormatVidPid(vid, pid);
        using var mutex = AcquireMutex();
        try
        {
            RestoreFromPendingStore(vidPid);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Replay every pending record from a prior process and restore
    /// the corresponding DirectInput OEM key. Call once at consumer startup,
    /// before creating any HIDMaestro virtuals, to clean up after a crash
    /// or missed cleanup. Returns the number of overrides restored.</summary>
    public static int RecoverOrphans()
    {
        using var mutex = AcquireMutex();
        try
        {
            using var pendingRoot = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: false);
            if (pendingRoot is null) return 0;

            int restored = 0;
            foreach (string vidPid in pendingRoot.GetSubKeyNames())
            {
                RestoreFromPendingStore(vidPid);
                restored++;
            }
            return restored;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Enumerate all currently-active overrides owned by this store.
    /// Returns a snapshot; entries may disappear between enumeration and read
    /// in the concurrent case.</summary>
    public static IReadOnlyList<(string VidPid, string? OriginalOemName, bool OriginalExisted)> ListActive()
    {
        var result = new List<(string, string?, bool)>();
        using var pendingRoot = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: false);
        if (pendingRoot is null) return result;

        foreach (string vidPid in pendingRoot.GetSubKeyNames())
        {
            using var entry = pendingRoot.OpenSubKey(vidPid, writable: false);
            if (entry is null) continue;
            string? original = entry.GetValue(StoreOriginalName) as string;
            bool existed = (entry.GetValue(StoreOriginalExisted) as int?) == 1;
            result.Add((vidPid, original, existed));
        }
        return result;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Internals
    // ────────────────────────────────────────────────────────────────────

    private static Mutex AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // A prior owner crashed while holding the mutex. We now own it —
            // safe to proceed; state integrity is guarded by the two-phase
            // write ordering, not the mutex lifetime.
        }
        return mutex;
    }

    private static void CaptureOriginalToPendingStore(string vidPid)
    {
        string? original;
        bool existed;
        using (var oem = Registry.LocalMachine.OpenSubKey(DirectInputOemPath(vidPid), writable: false))
        {
            if (oem is null)
            {
                original = null;
                existed = false;
            }
            else
            {
                original = oem.GetValue(OemNameValue) as string;
                existed = true;
            }
        }

        using var pending = Registry.LocalMachine.CreateSubKey(PendingEntryPath(vidPid), writable: true)!;
        if (original is not null)
            pending.SetValue(StoreOriginalName, original, RegistryValueKind.String);
        else
            pending.DeleteValue(StoreOriginalName, throwOnMissingValue: false);

        pending.SetValue(StoreOriginalExisted, existed ? 1 : 0, RegistryValueKind.DWord);
        pending.SetValue(StoreClaimedAt, DateTime.UtcNow.ToFileTimeUtc(), RegistryValueKind.QWord);
    }

    private static void RestoreFromPendingStore(string vidPid)
    {
        // Read pending record
        string? original;
        bool existed;
        using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
        {
            if (pending is null)
            {
                // No pending record — nothing to restore. Idempotent.
                return;
            }
            original = pending.GetValue(StoreOriginalName) as string;
            existed = (pending.GetValue(StoreOriginalExisted) as int?) == 1;
        }

        if (existed)
        {
            // Original OEM subkey existed. Restore the value (or null-out by
            // removing the OEM Name value if the original had no such value).
            using var oem = Registry.LocalMachine.CreateSubKey(DirectInputOemPath(vidPid), writable: true)!;
            if (original is not null)
                oem.SetValue(OemNameValue, original, RegistryValueKind.String);
            else
                oem.DeleteValue(OemNameValue, throwOnMissingValue: false);
        }
        else
        {
            // Original OEM subkey did not exist — we created it. Remove it.
            using var parent = Registry.LocalMachine.OpenSubKey(
                DirectInputVidPidPath(vidPid), writable: true);
            parent?.DeleteSubKeyTree(OemSubkey, throwOnMissingSubKey: false);
        }

        // Drop our pending record
        using var pendingRoot = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: true);
        pendingRoot?.DeleteSubKeyTree(vidPid, throwOnMissingSubKey: false);
    }

    private static void WriteDirectInputOemName(string vidPid, string label)
    {
        using var oem = Registry.LocalMachine.CreateSubKey(DirectInputOemPath(vidPid), writable: true)!;
        oem.SetValue(OemNameValue, label, RegistryValueKind.String);
    }

    private static string FormatVidPid(ushort vid, ushort pid) =>
        $"VID_{vid:X4}&PID_{pid:X4}";

    private static string DirectInputVidPidPath(string vidPid) =>
        $@"{DirectInputRoot}\{vidPid}";

    private static string DirectInputOemPath(string vidPid) =>
        $@"{DirectInputRoot}\{vidPid}\{OemSubkey}";

    private static string PendingEntryPath(string vidPid) =>
        $@"{PendingRoot}\{vidPid}";
}
