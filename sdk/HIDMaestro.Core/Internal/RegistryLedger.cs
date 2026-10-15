using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Every machine-wide registry key a virtual controller shares with real
/// devices of the same VID and PID goes through here: the GameInput
/// mapping key (<see cref="GameInputMapping"/>) and the joy.cpl OEM key
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_xxxx&amp;PID_xxxx</c>.
///
/// <para>foundation-sunshine #1138. Through v1.11.0 the SDK rewrote both
/// keys on create, deleted the OEM key's <c>OEMData</c> value and
/// <c>Axes</c> subkey, left everything in place afterwards, and its
/// cleanup deleted every OEM key under the Microsoft, Sony and VID 0000
/// prefixes in both hives. A vendor prefix is not ownership, and a real
/// pad of the same identity lost its calibration and joy.cpl data.</para>
///
/// <para>Now a write records the key's prior state here first, under
/// <c>HKLM\SOFTWARE\HIDMaestroRegistryLedger</c>, together with the
/// controllers that hold it (process id, process start time, controller
/// index). The last holder's teardown puts the prior state back, and only
/// when the key still holds what HIDMaestro wrote: a value someone else
/// changed in the meantime is theirs. A holder whose process is gone counts
/// as released. <see cref="ReleaseAll"/> restores everything, which the
/// full cleanup does because it has removed every virtual by then.</para>
///
/// <para>The ledger shares its lock, <see cref="LedgerLock"/>, with
/// <see cref="OemNameOverrideStore"/>. Both write the HKLM OEMName value,
/// and each consults the other so the value ends at the true pre-HIDMaestro
/// state in either order of claim and release.</para>
/// </summary>
internal static class RegistryLedger
{
    internal const string Root = @"SOFTWARE\HIDMaestroRegistryLedger";
    internal const string JoystickOemRoot =
        @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
    private const string GameInputKind = "GameInput";
    private const string JoystickOemKind = "JoystickOem";
    // v1.13.0: a devnode HIDMaestro bound WinUSB to (WinUsbBinding).
    private const string WinUsbKind = "WinUsb";
    private static readonly string[] AllKinds = { GameInputKind, JoystickOemKind, WinUsbKind };
    private const string V_InstanceId = "InstanceId";
    private const string V_PriorService = "PriorService";
    private const string V_PriorInf = "PriorInf";
    private const string HoldersKey = "Holders";
    private const string OemNameValue = "OEMName";

    // Entry values.
    private const string V_KeyExisted = "KeyExisted";
    private const string V_NameExisted = "NameExisted";
    private const string V_OriginalName = "OriginalName";
    private const string V_OriginalKind = "OriginalKind";
    private const string V_WrittenName = "WrittenName";

    private static readonly long s_selfStart = ProcessStartFileTime(Environment.ProcessId) ?? 0;

    // ── Holders ──────────────────────────────────────────────────────────

    private static string HolderName(int controllerIndex) =>
        $"{Environment.ProcessId}-{s_selfStart:X}-{controllerIndex}";

    private static long? ProcessStartFileTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToFileTimeUtc();
        }
        catch (ArgumentException) { return null; }            // no such process
        catch (InvalidOperationException) { return null; }    // exited while being read
        catch { return long.MinValue; }                       // exists, not queryable
    }

    /// <summary>A holder counts until its process is provably gone: absent,
    /// or present under the same id with a different start time.</summary>
    private static bool HolderAlive(string holder)
    {
        var parts = holder.Split('-');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int pid)
            || !long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out long start))
            return false;
        long? actual = ProcessStartFileTime(pid);
        if (actual == null) return false;
        if (actual == long.MinValue) return true;
        return actual.Value == start;
    }

    private static LedgerLock Acquire() => LedgerLock.Acquire();

    // ── GameInput mapping ────────────────────────────────────────────────

    /// <summary>Write the GameInput table for a controller whose profile
    /// <see cref="GameInputMapping.Applies"/>, unless Windows ships a
    /// mapping for the identity or a key someone else wrote is there.
    /// Returns true when the controller now holds the table.</summary>
    internal static bool ClaimGameInput(ushort vid, ushort pid, int controllerIndex)
    {
        string keyName = GameInputMapping.KeyName(vid, pid);
        string target = $@"{GameInputMapping.SystemRoot}\{keyName}";
        using var m = Acquire();
        try
        {
            string entryPath = $@"{Root}\{GameInputKind}\{keyName}";
            bool held;
            using (var e = Registry.LocalMachine.OpenSubKey(entryPath)) held = e != null;
            if (!held)
            {
                if (GameInputMapping.WindowsHasMapping(vid, pid)) return false;
                string? current = GameInputMapping.Dump(target);
                if (current != null && !GameInputMapping.IsLegacyTable(keyName)) return false;
                // Record first, then write: a crash between the two leaves a
                // record whose restore finds the table (or nothing) and
                // deletes it, never a table nobody tracks.
                using (Registry.LocalMachine.CreateSubKey(entryPath)) { }
                Registry.LocalMachine.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
                GameInputMapping.WriteTable(vid, pid);
            }
            else if (GameInputMapping.Dump(target) == null)
            {
                GameInputMapping.WriteTable(vid, pid);
            }
            using var holders = Registry.LocalMachine.CreateSubKey($@"{entryPath}\{HoldersKey}");
            holders.SetValue(HolderName(controllerIndex), 1, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>Delete the table an older build left under this identity,
    /// unless a live controller holds it.</summary>
    internal static void RemoveLegacyTable(ushort vid, ushort pid)
    {
        string keyName = GameInputMapping.KeyName(vid, pid);
        string target = $@"{GameInputMapping.SystemRoot}\{keyName}";
        // Most creates find no key at all: answer that without the mutex.
        try { using var probe = Registry.LocalMachine.OpenSubKey(target); if (probe == null) return; }
        catch { return; }
        using var m = Acquire();
        try
        {
            if (HeldGameInputKeys().Contains(keyName)) return;
            if (GameInputMapping.IsLegacyTable(keyName))
                Registry.LocalMachine.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
        }
        catch { }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>Delete every table older builds left behind that no live
    /// controller holds. Returns the number removed.</summary>
    internal static int RemoveLegacyTables()
    {
        using var m = Acquire();
        try { return GameInputMapping.RemoveLegacyTables(HeldGameInputKeys()); }
        catch { return 0; }
        finally { m.ReleaseMutex(); }
    }

    private static void RestoreGameInput(string keyName)
    {
        string target = $@"{GameInputMapping.SystemRoot}\{keyName}";
        if (GameInputMapping.Dump(target) == GameInputMapping.TableDump(false))
            Registry.LocalMachine.DeleteSubKeyTree(target, throwOnMissingSubKey: false);
    }

    // ── joy.cpl OEM key ──────────────────────────────────────────────────

    /// <summary>Set the HKLM OEMName for the identity to the virtual's
    /// display name, after recording the key's prior state.</summary>
    internal static void ClaimJoystickOem(ushort vid, ushort pid, string label, int controllerIndex)
    {
        string vidPid = $"VID_{vid:X4}&PID_{pid:X4}";
        string target = $@"{JoystickOemRoot}\{vidPid}";
        using var m = Acquire();
        try
        {
            string entryPath = $@"{Root}\{JoystickOemKind}\{vidPid}";
            bool held;
            using (var e = Registry.LocalMachine.OpenSubKey(entryPath)) held = e != null;
            if (!held)
            {
                bool keyExisted, nameExisted;
                object? original = null;
                RegistryValueKind originalKind = RegistryValueKind.String;
                if (OemNameOverrideStore.TryGetJoystickHklmOriginal(vidPid, out string? overrideOriginal, out bool overrideKeyExisted))
                {
                    // An HMOemNameOverride claimed this identity first. Its
                    // record holds the value from before any HIDMaestro write.
                    keyExisted = overrideKeyExisted;
                    nameExisted = overrideOriginal != null;
                    original = overrideOriginal;
                }
                else
                {
                    using var k = Registry.LocalMachine.OpenSubKey(target);
                    keyExisted = k != null;
                    original = k?.GetValue(OemNameValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    nameExisted = original != null;
                    if (nameExisted) originalKind = k!.GetValueKind(OemNameValue);
                }
                using var entry = Registry.LocalMachine.CreateSubKey(entryPath);
                entry.SetValue(V_KeyExisted, keyExisted ? 1 : 0, RegistryValueKind.DWord);
                entry.SetValue(V_NameExisted, nameExisted ? 1 : 0, RegistryValueKind.DWord);
                if (nameExisted)
                {
                    entry.SetValue(V_OriginalName, original!, originalKind);
                    entry.SetValue(V_OriginalKind, (int)originalKind, RegistryValueKind.DWord);
                }
            }
            using (var entry = Registry.LocalMachine.CreateSubKey(entryPath))
                entry.SetValue(V_WrittenName, label, RegistryValueKind.String);
            using (var oem = Registry.LocalMachine.CreateSubKey(target))
            {
                oem.SetValue(OemNameValue, label, RegistryValueKind.String);
                RemoveAprilOemWrites(oem);
            }
            using var holders = Registry.LocalMachine.CreateSubKey($@"{entryPath}\{HoldersKey}");
            holders.SetValue(HolderName(controllerIndex), 1, RegistryValueKind.DWord);
        }
        catch { }
        finally { m.ReleaseMutex(); }
    }

    // Builds before April 6, 2026 wrote OEMData = JOY_HWS 0x1017 + 10 buttons
    // and an Axes\5 "Hidden" entry under this key. Those exact bytes are
    // HIDMaestro's own and go. Anything else is the device's.
    private static readonly byte[] s_aprilOemData = { 0x17, 0x10, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 };
    private static readonly byte[] s_aprilAxisAttributes = { 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x01, 0x00 };

    private static void RemoveAprilOemWrites(RegistryKey oem)
    {
        try
        {
            if (oem.GetValue("OEMData") is byte[] data && data.AsSpan().SequenceEqual(s_aprilOemData))
                oem.DeleteValue("OEMData", throwOnMissingValue: false);
            using var axes = oem.OpenSubKey("Axes", writable: true);
            if (axes == null) return;
            using (var five = axes.OpenSubKey("5"))
            {
                bool ours = five != null
                    && five.SubKeyCount == 0
                    && five.GetValueNames().Length == 2
                    && five.GetValue("") as string == "Hidden"
                    && five.GetValue("Attributes") is byte[] a && a.AsSpan().SequenceEqual(s_aprilAxisAttributes);
                if (!ours) return;
            }
            axes.DeleteSubKeyTree("5", throwOnMissingSubKey: false);
            bool empty = axes.SubKeyCount == 0 && axes.GetValueNames().Length == 0;
            axes.Close();
            if (empty) oem.DeleteSubKey("Axes", throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void RestoreJoystickOem(string vidPid, RegistryKey entry)
    {
        string target = $@"{JoystickOemRoot}\{vidPid}";
        // An HMOemNameOverride record still active for this identity holds
        // the true original and restores it when it is cleared.
        if (OemNameOverrideStore.HasRecord(vidPid)) return;

        bool keyExisted = entry.GetValue(V_KeyExisted) is int ke && ke == 1;
        bool nameExisted = entry.GetValue(V_NameExisted) is int ne && ne == 1;
        string? written = entry.GetValue(V_WrittenName) as string;
        using var oem = Registry.LocalMachine.OpenSubKey(target, writable: true);
        if (oem == null) return;
        if (!string.Equals(oem.GetValue(OemNameValue) as string, written, StringComparison.Ordinal))
            return;   // changed since: whoever changed it owns it now
        if (nameExisted)
        {
            var kind = entry.GetValue(V_OriginalKind) is int k ? (RegistryValueKind)k : RegistryValueKind.String;
            object? original = entry.GetValue(V_OriginalName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (original != null) oem.SetValue(OemNameValue, original, kind);
            return;
        }
        oem.DeleteValue(OemNameValue, throwOnMissingValue: false);
        if (keyExisted) return;
        bool empty = oem.SubKeyCount == 0 && oem.GetValueNames().Length == 0;
        oem.Close();
        if (!empty) return;   // something else wrote here meanwhile: not ours alone
        using (var parent = Registry.LocalMachine.OpenSubKey(JoystickOemRoot, writable: true))
            parent?.DeleteSubKey(vidPid, throwOnMissingSubKey: false);
        ClearJoystickSlots(vidPid);
    }

    /// <summary>joy.cpl's per-user slot table names the OEM key each
    /// joystick slot was last bound to. When the identity existed only
    /// because a virtual created it, clear the slots that still name it.
    /// Slots naming any identity a real device had before are left
    /// alone.</summary>
    private static void ClearJoystickSlots(string vidPid)
    {
        try
        {
            using var js = Registry.CurrentUser.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaResources\Joystick", writable: true);
            if (js == null) return;
            foreach (var driver in js.GetSubKeyNames())
            {
                using var settings = js.OpenSubKey($@"{driver}\CurrentJoystickSettings", writable: true);
                if (settings == null) continue;
                foreach (var name in settings.GetValueNames())
                {
                    if (!name.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase)
                        || !name.EndsWith("OEMName", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(settings.GetValue(name) as string, vidPid, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string slot = name.Substring(0, name.Length - "OEMName".Length);   // "JoystickN"
                    foreach (var v in settings.GetValueNames())
                        if (v.StartsWith(slot, StringComparison.OrdinalIgnoreCase)
                            && v.Length > slot.Length && !char.IsDigit(v[slot.Length]))
                            settings.DeleteValue(v, throwOnMissingValue: false);
                }
            }
        }
        catch { }
    }

    /// <summary>The original HKLM OEMName the ledger recorded for an
    /// identity a virtual holds. The caller holds the shared mutex.</summary>
    internal static bool TryGetJoystickOriginal(string vidPid, out string? originalName, out bool keyExisted)
    {
        originalName = null; keyExisted = false;
        try
        {
            using var e = Registry.LocalMachine.OpenSubKey($@"{Root}\{JoystickOemKind}\{vidPid}");
            if (e == null) return false;
            keyExisted = e.GetValue(V_KeyExisted) is int ke && ke == 1;
            bool nameExisted = e.GetValue(V_NameExisted) is int ne && ne == 1;
            originalName = nameExisted ? e.GetValue(V_OriginalName) as string : null;
            return true;
        }
        catch { return false; }
    }

    // ── WinUSB on one devnode ────────────────────────────────────────────

    /// <summary>Record a devnode and the driver it has before
    /// <see cref="WinUsbBinding"/> binds WinUSB to it, and the controller as
    /// holder. Throws when the record cannot be written, so nothing is bound
    /// that the ledger cannot put back. The caller holds the devnode lock
    /// (<see cref="WinUsbBinding.AcquireDevnodeLock"/>), which a restore
    /// also holds from its check of the entry to its delete.</summary>
    internal static void ClaimWinUsb(string instanceId, string? priorService, string? priorInf, int controllerIndex)
    {
        string entryPath = $@"{Root}\{WinUsbKind}\{WinUsbEntryName(instanceId)}";
        using var m = Acquire();
        try
        {
            bool held;
            using (var e = Registry.LocalMachine.OpenSubKey(entryPath)) held = e != null;
            if (!held)
            {
                using var entry = Registry.LocalMachine.CreateSubKey(entryPath);
                entry.SetValue(V_InstanceId, instanceId, RegistryValueKind.String);
                entry.SetValue(V_PriorService, priorService ?? "", RegistryValueKind.String);
                entry.SetValue(V_PriorInf, priorInf ?? "", RegistryValueKind.String);
            }
            using var holders = Registry.LocalMachine.CreateSubKey($@"{entryPath}\{HoldersKey}");
            holders.SetValue(HolderName(controllerIndex), 1, RegistryValueKind.DWord);
        }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>A registry key name for a devnode: instance ids carry
    /// backslashes, which a key name cannot.</summary>
    private static string WinUsbEntryName(string instanceId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(instanceId.ToUpperInvariant())))[..16];

    /// <summary>The devnodes the ledger holds a WinUSB binding for.</summary>
    internal static List<string> HeldWinUsbDevnodes()
    {
        var list = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}");
            if (k == null) return list;
            foreach (var n in k.GetSubKeyNames())
                using (var e = k.OpenSubKey(n))
                    if (e?.GetValue(V_InstanceId) is string id) list.Add(id);
        }
        catch { }
        return list;
    }

    // ── Release ──────────────────────────────────────────────────────────

    /// <summary>Teardown of one controller: drop its holds, restore every
    /// entry no live controller holds any more.</summary>
    internal static void Release(int controllerIndex) => Sweep(new[] { HolderName(controllerIndex) });

    /// <summary>Teardown of several controllers at once, as at process exit:
    /// one acquisition of the ledger's lock drops all their holds, so a
    /// squatted v1.12.0 mutex costs one wait, and the WinUSB devnodes they
    /// held are put back after the lock is let go, as in every sweep.</summary>
    internal static void Release(IEnumerable<int> controllerIndices) =>
        Sweep(controllerIndices.Select(HolderName).ToArray());

    /// <summary>Restore every entry whose holders are all gone.</summary>
    internal static void PruneDead() => Sweep(null);

    private static void Sweep(string[]? releasing)
    {
        var winUsb = new List<WinUsbQueued>();
        using (var m = Acquire())
        {
            try
            {
                foreach (var kind in AllKinds)
                {
                    using var kindKey = Registry.LocalMachine.OpenSubKey($@"{Root}\{kind}", writable: true);
                    if (kindKey == null) continue;
                    foreach (var name in kindKey.GetSubKeyNames())
                    {
                        bool restore;
                        using (var holders = kindKey.OpenSubKey($@"{name}\{HoldersKey}", writable: true))
                        {
                            if (holders != null)
                                foreach (var h in holders.GetValueNames())
                                    if ((releasing != null && Array.IndexOf(releasing, h) >= 0) || !HolderAlive(h))
                                        holders.DeleteValue(h, throwOnMissingValue: false);
                            restore = holders == null || holders.GetValueNames().Length == 0;
                        }
                        if (!restore) continue;
                        if (kind == WinUsbKind) TakeWinUsbEntry(kindKey, name, winUsb);
                        else RestoreEntry(kindKey, kind, name);
                    }
                }
                DeleteEmptyKeys();
            }
            catch { }
            finally { m.ReleaseMutex(); }
        }
        // A teardown releases right after its detach, so the devnode gets
        // that long to leave. One still there then is left as it is and
        // gets the reinstall flag (WinUsbBinding.RestoreLocked).
        RestoreWinUsbEntries(winUsb, waitGoneMs: releasing != null ? TimeoutScale.Apply(2000) : 0, ignoreHolders: false);
    }

    /// <summary>Restore every entry regardless of holders. The full cleanup
    /// calls this after it has removed every virtual controller.</summary>
    internal static void ReleaseAll()
    {
        var winUsb = new List<WinUsbQueued>();
        using (var m = Acquire())
        {
            try
            {
                foreach (var kind in AllKinds)
                {
                    using var kindKey = Registry.LocalMachine.OpenSubKey($@"{Root}\{kind}", writable: true);
                    if (kindKey == null) continue;
                    foreach (var name in kindKey.GetSubKeyNames())
                    {
                        if (kind == WinUsbKind) TakeWinUsbEntry(kindKey, name, winUsb);
                        else RestoreEntry(kindKey, kind, name);
                    }
                }
                if (winUsb.Count == 0) Registry.LocalMachine.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
            }
            catch { }
            finally { m.ReleaseMutex(); }
        }
        RestoreWinUsbEntries(winUsb, waitGoneMs: 0, ignoreHolders: true);
    }

    /// <summary>Restore a GameInput or joy.cpl entry and delete it, under
    /// the ledger's lock: a few registry writes.</summary>
    private static void RestoreEntry(RegistryKey kindKey, string kind, string name)
    {
        try
        {
            if (kind == GameInputKind)
                RestoreGameInput(name);
            else
                using (var entry = kindKey.OpenSubKey(name))
                    if (entry != null) RestoreJoystickOem(name, entry);
        }
        catch { }
        try { kindKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
    }

    /// <summary>A WinUSB entry queued for its restore, with the holders it
    /// had when it was queued.</summary>
    private readonly record struct WinUsbQueued(string Name, string InstanceId, string[] Seen);

    /// <summary>Queue a WinUSB entry for <see cref="RestoreWinUsbEntries"/>.
    /// Its devnode work takes seconds, so it runs after the ledger's lock is
    /// released, and the entry stays until it is done: a process that ends
    /// in between leaves the entry for the next sweep. An entry with no
    /// devnode recorded is deleted now. The caller holds the ledger's
    /// lock.</summary>
    private static void TakeWinUsbEntry(RegistryKey kindKey, string name, List<WinUsbQueued> queue)
    {
        string? id;
        string[] seen;
        using (var entry = kindKey.OpenSubKey(name))
        {
            id = entry?.GetValue(V_InstanceId) as string;
            using var holders = entry?.OpenSubKey(HoldersKey);
            seen = holders?.GetValueNames() ?? Array.Empty<string>();
        }
        if (id != null) queue.Add(new WinUsbQueued(name, id, seen));
        else try { kindKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
    }

    /// <summary>Put each queued WinUSB entry's devnode back
    /// (<see cref="WinUsbBinding.RestoreLocked"/>) and delete the entry.
    /// Each runs under the machine-wide devnode lock, which every claim also
    /// takes, and takes the ledger's lock only to read and delete the entry.
    /// An entry a bind claimed since it was queued stays, also when
    /// <paramref name="ignoreHolders"/> restores it regardless of the
    /// holders it had then.</summary>
    private static void RestoreWinUsbEntries(List<WinUsbQueued> entries, int waitGoneMs, bool ignoreHolders)
    {
        if (entries.Count == 0) return;
        foreach (var (name, id, seen) in entries)
        {
            try
            {
                WinUsbBinding.WaitGone(id, waitGoneMs);
                using var devnodes = WinUsbBinding.AcquireDevnodeLock();
                try
                {
                    if (!WinUsbEntryUnheld(name, ignoreHolders, seen)) continue;
                    WinUsbBinding.RestoreLocked(id);
                    using var m = Acquire();
                    try
                    {
                        using var kindKey = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}", writable: true);
                        kindKey?.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                    }
                    finally { m.ReleaseMutex(); }
                }
                finally { devnodes.ReleaseMutex(); }
            }
            catch (Exception ex)
            {
                DeviceOrchestrator.LogDiag($"    ledger: the WinUSB entry of {id} stays for the next sweep: {ex.GetType().Name}: {ex.Message}");
            }
        }
        try
        {
            using var m = Acquire();
            try { DeleteEmptyKeys(); }
            finally { m.ReleaseMutex(); }
        }
        catch { }
    }

    /// <summary>True when the WinUSB entry still exists and no live
    /// controller holds it. With <paramref name="ignoreHolders"/>, true when
    /// it exists and every live holder is one of <paramref name="seen"/>, the
    /// holders it had when it was queued: a bind that claimed it since keeps
    /// it. Holders whose process is gone are dropped. The caller holds the
    /// devnode lock.</summary>
    private static bool WinUsbEntryUnheld(string name, bool ignoreHolders, string[] seen)
    {
        using var m = Acquire();
        try
        {
            using var entry = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}\{name}");
            if (entry == null) return false;
            using var holders = entry.OpenSubKey(HoldersKey, writable: true);
            if (holders == null) return true;
            foreach (var h in holders.GetValueNames())
                if (!HolderAlive(h)) holders.DeleteValue(h, throwOnMissingValue: false);
            var live = holders.GetValueNames();
            return ignoreHolders
                ? live.All(h => seen.Contains(h, StringComparer.Ordinal))
                : live.Length == 0;
        }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>Leave no trace once nothing is held: delete the empty kind
    /// keys and an empty root. The caller holds the ledger's lock.</summary>
    private static void DeleteEmptyKeys()
    {
        using var root = Registry.LocalMachine.OpenSubKey(Root, writable: true);
        if (root == null) return;
        foreach (var kind in root.GetSubKeyNames())
        {
            bool empty;
            using (var k = root.OpenSubKey(kind)) empty = k != null && k.SubKeyCount == 0;
            if (empty) root.DeleteSubKey(kind, throwOnMissingSubKey: false);
        }
        bool rootEmpty = root.SubKeyCount == 0 && root.GetValueNames().Length == 0;
        root.Close();
        if (rootEmpty) Registry.LocalMachine.DeleteSubKey(Root, throwOnMissingSubKey: false);
    }

    /// <summary>GameInput keys a live controller holds, which the legacy
    /// sweep must leave alone.</summary>
    internal static HashSet<string> HeldGameInputKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{Root}\{GameInputKind}");
            if (k != null) foreach (var n in k.GetSubKeyNames()) set.Add(n);
        }
        catch { }
        return set;
    }
}
