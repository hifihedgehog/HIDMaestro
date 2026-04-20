using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Crash-safe registry state machine for joy.cpl / DirectInput OEM-name
/// overrides.
///
/// <para>Empirical result on Windows 11: the label shown in
/// <c>joy.cpl</c> and the one returned by DirectInput's
/// <c>DIPROP_PRODUCTNAME</c> are sourced from three distinct registry
/// paths, and Windows pre-populates at least one of them
/// (<c>HKCU\...\Joystick\OEM\...</c>) for common clone VID:PIDs. To
/// reliably override the label, all three paths must be written:</para>
///
/// <list type="number">
///   <item>
///     <c>HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_####&amp;PID_####\OEM\"OEM Name"</c>
///     <br/>(value name has a space; read by DirectInput consumers)
///   </item>
///   <item>
///     <c>HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_####&amp;PID_####\"OEMName"</c>
///     <br/>(value name has NO space; read by the MME joy.cpl applet)
///   </item>
///   <item>
///     <c>HKCU\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_####&amp;PID_####\"OEMName"</c>
///     <br/>(per-user copy; takes precedence over the HKLM Joystick path
///     and is what Windows preloads with clone labels like
///     "PC TWIN SHOCK Gamepad" for VID_0079&amp;PID_0006)
///   </item>
/// </list>
///
/// <para>Every claim captures the pre-override value for each of the
/// three targets into a single HIDMaestro-owned record at
/// <c>HKLM\SOFTWARE\HIDMaestroOemOverrides\VID_xxxx&amp;PID_xxxx</c>
/// BEFORE any of the target keys is mutated. That record drives
/// three-way restore on <see cref="Release"/> and
/// <see cref="RecoverOrphans"/>.</para>
///
/// <para>Backward compatibility: pending records written by an earlier
/// build of this store (which captured only the DirectInput target)
/// are still replayed correctly — missing Joystick fields are treated
/// as "no restore needed" for that target, which is the safe default
/// since no new Joystick write happened under that old record.</para>
///
/// <para>All methods require admin (HKLM write). The HKCU write
/// targets the CALLING user's hive. Multi-user systems: the override
/// is user-scoped for joy.cpl purposes but system-wide for DirectInput.
/// Single-user workstations (the common case) see consistent behavior
/// across both UIs.</para>
/// </summary>
internal static class OemNameOverrideStore
{
    // ── Target paths ────────────────────────────────────────────────
    private const string DirectInputRoot =
        @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput";
    private const string JoystickRoot =
        @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

    private const string DirectInputOemSubkey = "OEM";
    private const string DirectInputOemNameValue = "OEM Name";   // space
    private const string JoystickOemNameValue = "OEMName";       // no space

    // ── Pending-record hive (sibling of HKLM\SOFTWARE\HIDMaestro;
    //    deliberately NOT under it — see DeviceOrchestrator cleanup)
    private const string PendingRoot = @"SOFTWARE\HIDMaestroOemOverrides";
    private const string MutexName = @"Global\HIDMaestro-OEM-Recovery";

    // ── Pending-record value names ─────────────────────────────────
    // DirectInput (v1 fields — preserved for backward compat)
    private const string DI_OriginalName    = "OriginalOemName";
    private const string DI_OriginalExisted = "OriginalKeyExisted";
    // Joystick HKLM (v2 fields)
    private const string JM_OriginalName    = "OriginalJoystickOemName_HKLM";
    private const string JM_OriginalExisted = "OriginalJoystickKeyExisted_HKLM";
    // Joystick HKCU (v2 fields)
    private const string JU_OriginalName    = "OriginalJoystickOemName_HKCU";
    private const string JU_OriginalExisted = "OriginalJoystickKeyExisted_HKCU";
    private const string V_ClaimedAt        = "ClaimedAtFileTime";

    // ────────────────────────────────────────────────────────────────
    //  Public entry points
    // ────────────────────────────────────────────────────────────────

    public static void Claim(ushort vid, ushort pid, string label)
    {
        if (label is null) throw new ArgumentNullException(nameof(label));

        string vidPid = FormatVidPid(vid, pid);
        using var mutex = AcquireMutex();
        try
        {
            // Capture prior values ONLY on the first Set for this VID:PID.
            // Subsequent Sets replace the label but leave the original-state
            // record alone, so Clear always restores to the true pre-HIDMaestro
            // state, not the prior Set's label.
            bool recordExists;
            using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
            {
                recordExists = pending is not null;
            }
            if (!recordExists)
                CaptureAllOriginalsToPendingStore(vidPid);

            WriteAllTargets(vidPid, label);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static void Release(ushort vid, ushort pid)
    {
        string vidPid = FormatVidPid(vid, pid);
        using var mutex = AcquireMutex();
        try { RestoreFromPendingStore(vidPid); }
        finally { mutex.ReleaseMutex(); }
    }

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
        finally { mutex.ReleaseMutex(); }
    }

    public static IReadOnlyList<(string VidPid, string? OriginalOemName, bool OriginalExisted)> ListActive()
    {
        var result = new List<(string, string?, bool)>();
        using var pendingRoot = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: false);
        if (pendingRoot is null) return result;

        foreach (string vidPid in pendingRoot.GetSubKeyNames())
        {
            using var entry = pendingRoot.OpenSubKey(vidPid, writable: false);
            if (entry is null) continue;
            // ListActive surfaces the DirectInput original for backward compat
            // with the v1 record shape. The joystick fields are also captured
            // but are implementation detail of restore.
            string? original = entry.GetValue(DI_OriginalName) as string;
            bool existed = (entry.GetValue(DI_OriginalExisted) as int?) == 1;
            result.Add((vidPid, original, existed));
        }
        return result;
    }

    // ────────────────────────────────────────────────────────────────
    //  Write pass — mutate all three targets
    // ────────────────────────────────────────────────────────────────

    private static void WriteAllTargets(string vidPid, string label)
    {
        // 1. DirectInput (HKLM)
        using (var key = Registry.LocalMachine.CreateSubKey($@"{DirectInputRoot}\{vidPid}\{DirectInputOemSubkey}", writable: true)!)
        {
            key.SetValue(DirectInputOemNameValue, label, RegistryValueKind.String);
        }

        // 2. Joystick (HKLM)
        using (var key = Registry.LocalMachine.CreateSubKey($@"{JoystickRoot}\{vidPid}", writable: true)!)
        {
            key.SetValue(JoystickOemNameValue, label, RegistryValueKind.String);
        }

        // 3. Joystick (HKCU) — wins over HKLM, what joy.cpl actually shows
        using (var key = Registry.CurrentUser.CreateSubKey($@"{JoystickRoot}\{vidPid}", writable: true)!)
        {
            key.SetValue(JoystickOemNameValue, label, RegistryValueKind.String);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Capture pass — save originals for all three targets
    // ────────────────────────────────────────────────────────────────

    private static void CaptureAllOriginalsToPendingStore(string vidPid)
    {
        var (diOrig, diExisted) = ReadOriginal(
            Registry.LocalMachine,
            $@"{DirectInputRoot}\{vidPid}\{DirectInputOemSubkey}",
            DirectInputOemNameValue);

        var (jmOrig, jmExisted) = ReadOriginal(
            Registry.LocalMachine,
            $@"{JoystickRoot}\{vidPid}",
            JoystickOemNameValue);

        var (juOrig, juExisted) = ReadOriginal(
            Registry.CurrentUser,
            $@"{JoystickRoot}\{vidPid}",
            JoystickOemNameValue);

        using var pending = Registry.LocalMachine.CreateSubKey(PendingEntryPath(vidPid), writable: true)!;
        WriteOriginalPair(pending, DI_OriginalName, DI_OriginalExisted, diOrig, diExisted);
        WriteOriginalPair(pending, JM_OriginalName, JM_OriginalExisted, jmOrig, jmExisted);
        WriteOriginalPair(pending, JU_OriginalName, JU_OriginalExisted, juOrig, juExisted);
        pending.SetValue(V_ClaimedAt, DateTime.UtcNow.ToFileTimeUtc(), RegistryValueKind.QWord);
    }

    private static (string? val, bool existed) ReadOriginal(RegistryKey baseKey, string path, string valueName)
    {
        using var k = baseKey.OpenSubKey(path, writable: false);
        if (k is null) return (null, false);
        return (k.GetValue(valueName) as string, true);
    }

    private static void WriteOriginalPair(RegistryKey pending, string nameKey, string existedKey, string? original, bool existed)
    {
        if (original is not null)
            pending.SetValue(nameKey, original, RegistryValueKind.String);
        else
            pending.DeleteValue(nameKey, throwOnMissingValue: false);
        pending.SetValue(existedKey, existed ? 1 : 0, RegistryValueKind.DWord);
    }

    // ────────────────────────────────────────────────────────────────
    //  Restore pass — replay record, delete pending entry
    // ────────────────────────────────────────────────────────────────

    private static void RestoreFromPendingStore(string vidPid)
    {
        string? diOrig; bool? diExisted;
        string? jmOrig; bool? jmExisted;
        string? juOrig; bool? juExisted;

        using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
        {
            if (pending is null) return; // nothing to restore; idempotent

            diOrig    = pending.GetValue(DI_OriginalName) as string;
            diExisted = ReadExistedFlag(pending, DI_OriginalExisted);
            jmOrig    = pending.GetValue(JM_OriginalName) as string;
            jmExisted = ReadExistedFlag(pending, JM_OriginalExisted);
            juOrig    = pending.GetValue(JU_OriginalName) as string;
            juExisted = ReadExistedFlag(pending, JU_OriginalExisted);
        }

        // DirectInput target
        if (diExisted == true)
            RestoreValue(Registry.LocalMachine, $@"{DirectInputRoot}\{vidPid}\{DirectInputOemSubkey}",
                         DirectInputOemNameValue, diOrig);
        else if (diExisted == false)
            DeleteSubkey(Registry.LocalMachine, $@"{DirectInputRoot}\{vidPid}", DirectInputOemSubkey);
        // diExisted == null → record lacks DirectInput fields; skip

        // Joystick HKLM target
        if (jmExisted == true)
            RestoreValue(Registry.LocalMachine, $@"{JoystickRoot}\{vidPid}",
                         JoystickOemNameValue, jmOrig);
        else if (jmExisted == false)
            DeleteSubkeyFromParent(Registry.LocalMachine, JoystickRoot, vidPid);
        // jmExisted == null → v1 record; no joystick write happened, nothing to restore

        // Joystick HKCU target
        if (juExisted == true)
            RestoreValue(Registry.CurrentUser, $@"{JoystickRoot}\{vidPid}",
                         JoystickOemNameValue, juOrig);
        else if (juExisted == false)
            DeleteSubkeyFromParent(Registry.CurrentUser, JoystickRoot, vidPid);
        // juExisted == null → v1 record; nothing to restore

        // Drop the pending entry
        using var pendingRoot = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: true);
        pendingRoot?.DeleteSubKeyTree(vidPid, throwOnMissingSubKey: false);
    }

    private static bool? ReadExistedFlag(RegistryKey key, string valueName)
    {
        var v = key.GetValue(valueName);
        if (v is null) return null;
        return ((int)v) == 1;
    }

    private static void RestoreValue(RegistryKey baseKey, string path, string valueName, string? original)
    {
        using var k = baseKey.CreateSubKey(path, writable: true)!;
        if (original is not null)
            k.SetValue(valueName, original, RegistryValueKind.String);
        else
            k.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static void DeleteSubkey(RegistryKey baseKey, string parentPath, string subkey)
    {
        using var parent = baseKey.OpenSubKey(parentPath, writable: true);
        parent?.DeleteSubKeyTree(subkey, throwOnMissingSubKey: false);
    }

    private static void DeleteSubkeyFromParent(RegistryKey baseKey, string parentPath, string subkey) =>
        DeleteSubkey(baseKey, parentPath, subkey);

    // ────────────────────────────────────────────────────────────────
    //  Plumbing
    // ────────────────────────────────────────────────────────────────

    private static Mutex AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* safe: two-phase ordering guards state */ }
        return mutex;
    }

    private static string FormatVidPid(ushort vid, ushort pid) =>
        $"VID_{vid:X4}&PID_{pid:X4}";

    private static string PendingEntryPath(string vidPid) =>
        $@"{PendingRoot}\{vidPid}";
}
