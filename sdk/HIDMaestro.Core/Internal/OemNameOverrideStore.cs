using System;
using System.Collections.Generic;
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
/// are still replayed correctly: missing Joystick fields are treated
/// as "no restore needed" for that target, which is the safe default
/// since no new Joystick write happened under that old record.</para>
///
/// <para><see cref="Claim"/> needs an elevated process, and so do
/// <see cref="Release"/> and <see cref="RecoverOrphans"/> when a record is
/// pending. <see cref="ListActive"/> only reads. The HKCU write
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
    //    deliberately NOT under it: see DeviceOrchestrator cleanup)
    private const string PendingRoot = @"SOFTWARE\HIDMaestroOemOverrides";
    // The lock is LedgerLock, shared with RegistryLedger, which writes the
    // same HKLM OEMName value for live virtual controllers.

    // ── Pending-record value names ─────────────────────────────────
    // DirectInput (v1 fields: preserved for backward compat)
    private const string DI_OriginalName    = "OriginalOemName";
    private const string DI_OriginalExisted = "OriginalKeyExisted";
    // Joystick HKLM (v2 fields)
    private const string JM_OriginalName    = "OriginalJoystickOemName_HKLM";
    private const string JM_OriginalExisted = "OriginalJoystickKeyExisted_HKLM";
    // Joystick HKCU (v2 fields)
    private const string JU_OriginalName    = "OriginalJoystickOemName_HKCU";
    private const string JU_OriginalExisted = "OriginalJoystickKeyExisted_HKCU";
    private const string V_ClaimedAt        = "ClaimedAtFileTime";
    // The label the last Claim wrote to all three targets (2.0.1).
    private const string V_WrittenLabel     = "WrittenLabel";
    // The SID of the account whose HKCU original the record holds (2.0.1).
    private const string V_UserSid          = "UserSid";
    // The label the last Claim from that account wrote to its HKCU target
    // (2.0.1). A Claim from another account changes WrittenLabel, not this.
    private const string V_WrittenUserLabel = "WrittenUserLabel";

    /// <summary>Whether the record names an account other than the one this
    /// process runs as. Its HKCU original then belongs to that account's
    /// hive, which this process neither writes nor restores.</summary>
    private static bool OtherAccount(string? recordSid) =>
        recordSid != null && !string.Equals(recordSid, CurrentUserSid(), StringComparison.OrdinalIgnoreCase);

    private static string? CurrentUserSid()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return id.User?.Value;
        }
        catch { return null; }
    }

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
            // Capture a target's prior value only when the record does not
            // hold it yet: on the first Set for this VID:PID, and for a
            // target a full removal run from another account already put
            // back and dropped from a record it kept (RestoreForUninstall).
            // Later Sets replace the label but leave each captured original
            // alone, so Clear always restores to the true pre-HIDMaestro
            // state, not the prior Set's label. The account whose HKCU target
            // is captured is recorded with it, and only then: the per-user
            // original belongs to that account's hive. A Set from another
            // account leaves that hive's original and label alone and writes
            // the HKLM targets only, since its own per-user original was
            // never captured.
            string? recordSid;
            using (var existing = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
                recordSid = existing?.GetValue(V_UserSid) as string;
            bool user = !OtherAccount(recordSid);
            bool userCaptured = CaptureMissingOriginals(vidPid);

            // The labels go in the record before the targets, so a full
            // removal restores a target only while it still holds what
            // HIDMaestro wrote (RestoreForUninstall).
            using (var pending = Registry.LocalMachine.CreateSubKey(PendingEntryPath(vidPid), writable: true)!)
            {
                pending.SetValue(V_WrittenLabel, label, RegistryValueKind.String);
                if (user) pending.SetValue(V_WrittenUserLabel, label, RegistryValueKind.String);
                if (userCaptured && CurrentUserSid() is { } sid) pending.SetValue(V_UserSid, sid, RegistryValueKind.String);
            }

            WriteAllTargets(vidPid, label, user);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static void Release(ushort vid, ushort pid)
    {
        string vidPid = FormatVidPid(vid, pid);
        // Nothing recorded for this device is nothing to restore. The read
        // is HKLM-read-only, so this returns without the lock and without
        // elevation, as it did before the lock moved behind an elevation
        // check. Only a device with a pending record reaches the lock, and
        // restoring it needs elevation anyway.
        using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
            if (pending is null) return;
        using var mutex = AcquireMutex();
        try { RestoreFromPendingStore(vidPid); }
        finally { mutex.ReleaseMutex(); }
    }

    public static int RecoverOrphans()
    {
        // No pending records is 0, without the lock and without elevation,
        // as before the lock moved behind an elevation check. A process
        // that calls this at startup before it elevates gets 0, not a throw,
        // when nothing is pending.
        using (var probe = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: false))
            if (probe is null || probe.SubKeyCount == 0) return 0;
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

    /// <summary>The identities with a pending override record, for a full
    /// removal's list. Reads only.</summary>
    internal static List<string> PendingIdentities()
    {
        var list = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: false);
            if (root != null) list.AddRange(root.GetSubKeyNames());
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return list;
    }

    /// <summary>The registry path of an identity's pending record, under
    /// HKLM.</summary>
    internal static string PendingRecordPath(string vidPid) => PendingEntryPath(vidPid);

    /// <summary>What a full removal did with one override record: whether
    /// the record stays for a later run, and why, or which targets it left
    /// because another program changed them.</summary>
    internal readonly record struct UninstallRestore(bool RecordKept, string? Reason);

    /// <summary>A full removal's restore of one identity's override. Each
    /// target gets its captured original back only while it still holds the
    /// label the record says HIDMaestro wrote (WrittenLabel, 2.0.1). A target
    /// someone changed since is left as it is. A record older than 2.0.1
    /// holds no label, so no target can be shown to hold HIDMaestro's, and
    /// the record stays with every target as it is. The HKCU target is
    /// restored only from the account the record names (UserSid), whose hive
    /// Claim wrote. From any other account the HKLM targets are restored,
    /// and the record stays with the HKCU target alone, for a removal run
    /// from that account. The caller has established that no other
    /// HIDMaestro program runs. Needs elevation.</summary>
    internal static UninstallRestore RestoreForUninstall(string vidPid)
    {
        var left = new List<string>();
        using var mutex = AcquireMutex();
        try
        {
            string? written, writtenUser, sid, diOrig, jmOrig, juOrig;
            bool? diExisted, jmExisted, juExisted;
            using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
            {
                if (pending is null) return new UninstallRestore(false, null);
                written   = pending.GetValue(V_WrittenLabel) as string;
                writtenUser = pending.GetValue(V_WrittenUserLabel) as string ?? written;
                sid       = pending.GetValue(V_UserSid) as string;
                diOrig    = pending.GetValue(DI_OriginalName) as string;
                diExisted = ReadExistedFlag(pending, DI_OriginalExisted);
                jmOrig    = pending.GetValue(JM_OriginalName) as string;
                jmExisted = ReadExistedFlag(pending, JM_OriginalExisted);
                juOrig    = pending.GetValue(JU_OriginalName) as string;
                juExisted = ReadExistedFlag(pending, JU_OriginalExisted);
            }
            if (written == null)
                return new UninstallRestore(true,
                    "it was made before 2.0.1, which did not record the label it wrote, so whether each label is still HIDMaestro's cannot be told. The labels stay as they are, and HMOemNameOverride.Clear puts them back");

            string diPath = $@"{DirectInputRoot}\{vidPid}\{DirectInputOemSubkey}";
            string joyPath = $@"{JoystickRoot}\{vidPid}";
            bool Ours(RegistryKey hive, string path, string value) =>
                ReadOriginal(hive, path, value).val == (hive == Registry.CurrentUser ? writtenUser : written);

            if (diExisted != null)
            {
                if (!Ours(Registry.LocalMachine, diPath, DirectInputOemNameValue)) left.Add($@"HKLM\{diPath}\{DirectInputOemNameValue}");
                else if (diExisted == true) RestoreValue(Registry.LocalMachine, diPath, DirectInputOemNameValue, diOrig);
                else
                {
                    DeleteSubkey(Registry.LocalMachine, $@"{DirectInputRoot}\{vidPid}", DirectInputOemSubkey);
                    // Claim's CreateSubKey made the identity's key too when
                    // it was missing. Empty now, it goes.
                    DeleteIfEmpty(Registry.LocalMachine, DirectInputRoot, vidPid);
                }
            }
            if (jmExisted != null)
            {
                if (!Ours(Registry.LocalMachine, joyPath, JoystickOemNameValue)) left.Add($@"HKLM\{joyPath}\{JoystickOemNameValue}");
                else if (jmExisted == true) RestoreValue(Registry.LocalMachine, joyPath, JoystickOemNameValue, jmOrig);
                else DeleteSubkeyFromParent(Registry.LocalMachine, JoystickRoot, vidPid);
            }
            if (juExisted != null)
            {
                if (sid == null || !string.Equals(sid, CurrentUserSid(), StringComparison.OrdinalIgnoreCase))
                {
                    // Another account's hive: the HKLM parts are done, and
                    // the record keeps the HKCU part alone.
                    DropMachineParts(vidPid);
                    return new UninstallRestore(true,
                        $"the DirectInput and joy.cpl labels were put back. The per-user joy.cpl label belongs to {AccountName(sid)}, whose program set it, so the record stays for a removal run from that account");
                }
                if (!Ours(Registry.CurrentUser, joyPath, JoystickOemNameValue)) left.Add($@"HKCU\{joyPath}\{JoystickOemNameValue}");
                else if (juExisted == true) RestoreValue(Registry.CurrentUser, joyPath, JoystickOemNameValue, juOrig);
                else DeleteSubkeyFromParent(Registry.CurrentUser, JoystickRoot, vidPid);
            }

            using var pendingRoot = Registry.LocalMachine.OpenSubKey(PendingRoot, writable: true);
            pendingRoot?.DeleteSubKeyTree(vidPid, throwOnMissingSubKey: false);
            bool empty = pendingRoot != null && pendingRoot.SubKeyCount == 0 && pendingRoot.ValueCount == 0;
            pendingRoot?.Close();
            if (empty) Registry.LocalMachine.DeleteSubKey(PendingRoot, throwOnMissingSubKey: false);
            return new UninstallRestore(false, left.Count == 0 ? null
                : $"restored, except {string.Join(" and ", left)}, which another program changed since HIDMaestro wrote it, and which stays as it is");
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Drop the HKLM targets' originals from a record whose HKLM
    /// targets were put back, so it holds the HKCU part alone.</summary>
    private static void DropMachineParts(string vidPid)
    {
        using var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: true);
        foreach (string v in new[] { DI_OriginalName, DI_OriginalExisted, JM_OriginalName, JM_OriginalExisted })
            pending?.DeleteValue(v, throwOnMissingValue: false);
    }

    private static string AccountName(string? sid)
    {
        if (sid == null) return "an account the record does not name";
        try
        {
            return new System.Security.Principal.SecurityIdentifier(sid)
                .Translate(typeof(System.Security.Principal.NTAccount)).Value;
        }
        catch { return "account " + sid; }
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
    //  Write pass: mutate all three targets
    // ────────────────────────────────────────────────────────────────

    private static void WriteAllTargets(string vidPid, string label, bool user)
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

        // 3. Joystick (HKCU): wins over HKLM, what joy.cpl actually shows.
        // Not for an account whose original the record does not hold.
        if (!user) return;
        using (var key = Registry.CurrentUser.CreateSubKey($@"{JoystickRoot}\{vidPid}", writable: true)!)
        {
            key.SetValue(JoystickOemNameValue, label, RegistryValueKind.String);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Capture pass: save originals for all three targets
    // ────────────────────────────────────────────────────────────────

    /// <summary>Capture each target the record does not hold yet, creating
    /// the record when there is none. Returns true when the HKCU target was
    /// captured now.</summary>
    private static bool CaptureMissingOriginals(string vidPid)
    {
        bool di, jm, ju, exists;
        using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
        {
            exists = pending is not null;
            di = pending is null || ReadExistedFlag(pending, DI_OriginalExisted) is null;
            jm = pending is null || ReadExistedFlag(pending, JM_OriginalExisted) is null;
            ju = pending is null || ReadExistedFlag(pending, JU_OriginalExisted) is null;
        }
        if (!exists) { CaptureAllOriginalsToPendingStore(vidPid); return true; }
        if (!di && !jm && !ju) return false;
        // A target missing from a record was put back and dropped by a
        // removal from another account, or was never written under a record
        // from a build before the joystick targets existed. Either way its
        // value now is the original.
        using var record = Registry.LocalMachine.CreateSubKey(PendingEntryPath(vidPid), writable: true)!;
        if (di)
        {
            var (o, e) = ReadOriginal(Registry.LocalMachine, $@"{DirectInputRoot}\{vidPid}\{DirectInputOemSubkey}", DirectInputOemNameValue);
            WriteOriginalPair(record, DI_OriginalName, DI_OriginalExisted, o, e);
        }
        if (jm)
        {
            var (o, e) = RegistryLedger.TryGetJoystickOriginal(vidPid, out string? ledgerName, out bool ledgerKeyExisted)
                ? (ledgerName, ledgerKeyExisted)
                : ReadOriginal(Registry.LocalMachine, $@"{JoystickRoot}\{vidPid}", JoystickOemNameValue);
            WriteOriginalPair(record, JM_OriginalName, JM_OriginalExisted, o, e);
        }
        if (ju)
        {
            var (o, e) = ReadOriginal(Registry.CurrentUser, $@"{JoystickRoot}\{vidPid}", JoystickOemNameValue);
            WriteOriginalPair(record, JU_OriginalName, JU_OriginalExisted, o, e);
        }
        return ju;
    }

    private static void CaptureAllOriginalsToPendingStore(string vidPid)
    {
        var (diOrig, diExisted) = ReadOriginal(
            Registry.LocalMachine,
            $@"{DirectInputRoot}\{vidPid}\{DirectInputOemSubkey}",
            DirectInputOemNameValue);

        // A live virtual controller of this identity has already replaced the
        // HKLM value with its own name. The registry ledger recorded what was
        // there before, and that is the value to put back on Release.
        var (jmOrig, jmExisted) = RegistryLedger.TryGetJoystickOriginal(vidPid, out string? ledgerName, out bool ledgerKeyExisted)
            ? (ledgerName, ledgerKeyExisted)
            : ReadOriginal(
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
    //  Restore pass: replay record, delete pending entry
    // ────────────────────────────────────────────────────────────────

    private static void RestoreFromPendingStore(string vidPid)
    {
        string? diOrig; bool? diExisted;
        string? jmOrig; bool? jmExisted;
        string? juOrig; bool? juExisted;
        string? sid;

        using (var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false))
        {
            if (pending is null) return; // nothing to restore; idempotent
            sid       = pending.GetValue(V_UserSid) as string;

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

        // The HKCU original of another account goes back only from that
        // account: the record keeps it alone, and this hive is not touched.
        if (juExisted != null && OtherAccount(sid))
        {
            DropMachineParts(vidPid);
            return;
        }

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

    private static void DeleteIfEmpty(RegistryKey baseKey, string parentPath, string subkey)
    {
        using var parent = baseKey.OpenSubKey(parentPath, writable: true);
        if (parent == null) return;
        bool empty;
        using (var k = parent.OpenSubKey(subkey)) empty = k != null && k.SubKeyCount == 0 && k.ValueCount == 0;
        if (empty) parent.DeleteSubKey(subkey, throwOnMissingSubKey: false);
    }

    // ────────────────────────────────────────────────────────────────
    //  Registry ledger coordination. Callers hold the shared mutex.
    // ────────────────────────────────────────────────────────────────

    /// <summary>An override record exists for this identity.</summary>
    internal static bool HasRecord(string vidPid)
    {
        try
        {
            using var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false);
            return pending is not null;
        }
        catch { return false; }
    }

    /// <summary>The HKLM Joystick OEMName this identity had before the
    /// active override record claimed it. False when there is no record, or
    /// the record predates the HKLM fields.</summary>
    internal static bool TryGetJoystickHklmOriginal(string vidPid, out string? originalName, out bool keyExisted)
    {
        originalName = null; keyExisted = false;
        try
        {
            using var pending = Registry.LocalMachine.OpenSubKey(PendingEntryPath(vidPid), writable: false);
            if (pending is null) return false;
            bool? existed = ReadExistedFlag(pending, JM_OriginalExisted);
            if (existed is null) return false;
            keyExisted = existed.Value;
            originalName = pending.GetValue(JM_OriginalName) as string;
            return true;
        }
        catch { return false; }
    }

    // ────────────────────────────────────────────────────────────────
    //  Plumbing
    // ────────────────────────────────────────────────────────────────

    // An abandoned lock is acquired: two-phase ordering guards state.
    private static LedgerLock AcquireMutex() => LedgerLock.Acquire();

    private static string FormatVidPid(ushort vid, ushort pid) =>
        $"VID_{vid:X4}&PID_{pid:X4}";

    private static string PendingEntryPath(string vidPid) =>
        $@"{PendingRoot}\{vidPid}";
}
