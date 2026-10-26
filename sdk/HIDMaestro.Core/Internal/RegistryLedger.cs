using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
internal static partial class RegistryLedger
{
    internal const string Root = @"SOFTWARE\HIDMaestroRegistryLedger";
    internal const string JoystickOemRoot =
        @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
    private const string GameInputKind = "GameInput";
    private const string JoystickOemKind = "JoystickOem";
    // v2.0.0: a devnode HIDMaestro bound WinUSB to (WinUsbBinding).
    private const string WinUsbKind = "WinUsb";
    private static readonly string[] AllKinds = { GameInputKind, JoystickOemKind, WinUsbKind };
    private const string V_InstanceId = "InstanceId";
    private const string V_PriorService = "PriorService";
    private const string V_PriorInf = "PriorInf";
    // The INF names of the WinUSB packages the uninstall-grade cleanup left
    // in the driver store, because a devnode still used them or a delete
    // failed (NoteWinUsbPackagesLeft). A key of its own, written only under
    // the deploy lock (Note).
    private const string NoteKey = @"SOFTWARE\HIDMaestroPackagesLeft";
    private const string V_WinUsbPackages = "WinUsb";
    // A WinUSB entry's follow-up deadline, in UTC wall-clock milliseconds
    // (NowMs), which every process on the machine reads alike and which a
    // reboot, unlike a tick count, does not reset.
    private const string V_FollowUntil = "FollowUntilUtcMs";
    // How long past its deadline a follow-up may hold its mutex for its last
    // restore: the WinUSB lock's 120 s and the ledger's 30 s. A holder past
    // that is frozen or stuck, and its entry no longer waits for it.
    private const int FollowUpRestoreMs = 150_000;
    // How long a follow-up watches past the last call for its entry.
    private const int FollowUpMs = 300_000;
    // How long a restore, which holds the devnode lock, waits for the
    // ledger's.
    private const int RestoreLedgerTimeoutMs = 30_000;
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

    /// <summary>A holder counts while a process runs under its id with its
    /// start time. The holder's process is opened for
    /// PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE, and its creation
    /// time read (<see cref="OpenedStartTime"/>), a few microseconds. Only a
    /// process that exists and refuses that open is looked up in the
    /// system's process list (<see cref="ProcessCreateTimes"/>), read once
    /// for the walk, so a process this one may not open counts only when the
    /// list names it with the holder's start time. When neither can be read, the
    /// holder counts, the side that keeps a key from being restored under a
    /// live program. A holder carries Process.StartTime.ToFileTimeUtc(),
    /// which gives the process's creation time back exactly: DateTime keeps
    /// the hour a clock falls back as ambiguous local time and converts it
    /// back to the same UTC instant.</summary>
    private static bool HolderAlive(string holder, StartTimes times)
    {
        if (!TryParseHolder(holder, out int pid, out long start, out _)) return false;
        long? opened = OpenedStartTime(pid);
        if (opened == null) return false;
        if (opened.Value != long.MinValue) return opened.Value == start;
        if (times.List is { } list) return list.TryGetValue(pid, out long created) && created == start;
        return true;
    }

    /// <summary><see cref="HolderAlive(string, StartTimes)"/>, the test the
    /// census and the dead-holder cleanup make, with the read of the process
    /// list handed in, for the coexistence probe's OF13.</summary>
    internal static bool HolderAliveIn(string holder, Func<Dictionary<int, long>?> processTimes) =>
        HolderAlive(holder, new StartTimes(processTimes));

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ErrorInvalidParameter = 87;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0;

    /// <summary>A running process's creation time as a FILETIME, read
    /// through a PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE handle,
    /// the test the runtime's ProcessManager.IsProcessRunning makes. Null
    /// when no process has the id (ERROR_INVALID_PARAMETER) or the one that
    /// has it has exited: an exit code other than STILL_ACTIVE, or a
    /// signaled handle for a process whose exit code is 259. long.MinValue
    /// when the process exists and cannot be opened or read.</summary>
    private static long? OpenedStartTime(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
        if (h == IntPtr.Zero)
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? null : long.MinValue;
        try
        {
            if (GetExitCodeProcess(h, out uint code) && code != StillActive) return null;
            if (WaitForSingleObject(h, 0) == WaitObject0) return null;
            return GetProcessTimes(h, out long created, out _, out _, out _) ? created : long.MinValue;
        }
        finally { CloseHandle(h); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private static bool TryParseHolder(string holder, out int pid, out long start, out int index)
    {
        pid = index = 0;
        start = 0;
        var parts = holder.Split('-');
        return parts.Length == 3 && int.TryParse(parts[0], out pid)
            && long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out start)
            && int.TryParse(parts[2], out index);
    }

    /// <summary>The system's process list, read at most once for one walk of
    /// the holders, and only for a process that refuses the open.</summary>
    private sealed class StartTimes(Func<Dictionary<int, long>?> read)
    {
        private Dictionary<int, long>? _list;
        private bool _read;
        public Dictionary<int, long>? List
        {
            get
            {
                if (!_read) { _list = read(); _read = true; }
                return _list;
            }
        }
    }

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>The size the last read of the process list needed, plus
    /// room, so the next read is a single call. 0 before the first.</summary>
    private static uint s_processListSize;

    /// <summary>The buffer of a process's first read of the process
    /// list.</summary>
    private const uint FirstProcessListSize = 4 * 1024 * 1024;

    /// <summary><paramref name="read"/>, run at most once, on the first call
    /// of what this returns: one census hands
    /// <see cref="ProcessCreateTimes"/> through it to both of its holder
    /// reads.</summary>
    internal static Func<Dictionary<int, long>?> ReadOnce(Func<Dictionary<int, long>?> read)
    {
        Dictionary<int, long>? list = null;
        bool done = false;
        return () =>
        {
            if (!done) { list = read(); done = true; }
            return list;
        };
    }

    /// <summary>Each running process's creation time, a FILETIME as the
    /// holder's name carries it, by process id, from
    /// NtQuerySystemInformation's SystemProcessInformation, which needs no
    /// handle to any process. Null when the list cannot be read. Its
    /// SYSTEM_PROCESS_INFORMATION entries carry NextEntryOffset at 0,
    /// CreateTime at 32 and UniqueProcessId after ImageName and
    /// BasePriority: at 80 in a 64-bit process, 68 in a 32-bit one
    /// (winternl.h, and the layout ntexapi.h gives the fields between).</summary>
    internal static Dictionary<int, long>? ProcessCreateTimes() => ProcessCreateTimes(out _);

    /// <summary><see cref="ProcessCreateTimes()"/>, with the number of
    /// kernel queries it made: 1 unless the list outgrew the buffer. For the
    /// coexistence probe's OF13.</summary>
    internal static unsafe Dictionary<int, long>? ProcessCreateTimes(out int queries)
    {
        // A read starts at the size the last read of this process needed,
        // plus room. The first starts at 4 MB: a machine with 527 processes
        // needed 1,267,688 bytes, about 2.4 KB a process, so 4 MB holds some
        // 1,600 processes without a second query.
        queries = 0;
        uint size = Volatile.Read(ref s_processListSize);
        if (size == 0) size = FirstProcessListSize;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            byte* buffer = (byte*)NativeMemory.Alloc(size);
            try
            {
                queries++;
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out uint needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size, needed) + 64 * 1024;
                    continue;
                }
                if (status < 0) return null;
                Volatile.Write(ref s_processListSize, needed + 64 * 1024);
                int pidOffset = IntPtr.Size == 8 ? 80 : 68;
                var list = new Dictionary<int, long>(512);
                uint offset = 0;
                while (true)
                {
                    byte* entry = buffer + offset;
                    long created = *(long*)(entry + 32);
                    int pid = (int)(nint)(*(void**)(entry + pidOffset));
                    list[pid] = created;
                    uint next = *(uint*)entry;
                    if (next == 0) break;
                    offset += next;
                    if (offset >= size) break;
                }
                return list;
            }
            finally { NativeMemory.Free(buffer); }
        }
        return null;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    private static LedgerLock Acquire() => LedgerLock.Acquire();

    /// <summary>The holders of the GameInput and joy.cpl entries whose
    /// process still runs (<see cref="HolderAlive"/>), as process id and
    /// controller index. Every SDK from v1.12.0 writes one for each
    /// controller whose profile is not a keyboard or a mouse, in the same
    /// form, before the controller's devnode exists, and removes it at the
    /// controller's teardown (v1.12.0's WriteInstanceConfig and
    /// TeardownController). This release writes its own only after its
    /// owner record (ControllerLease.Acquire, then the create's instance
    /// configuration). The census reads them for a running v1.12.0
    /// program its devices do not show (OwnerCensus.Build). Read without the
    /// ledger's lock: a holder written or removed during the read is seen
    /// or not, as by a read just before or after it. The ledger is the one
    /// at <paramref name="root"/> of <paramref name="hive"/>, and
    /// <paramref name="processTimes"/> reads the process list for a process
    /// that refuses the open: HKLM's ledger and the system's list, read once
    /// per census, for the census, a scratch key and a list of its own for
    /// the coexistence probe's OF13.</summary>
    internal static List<(int Pid, int Index)> LiveHolders(RegistryKey hive, string root, Func<Dictionary<int, long>?> processTimes)
    {
        var list = new List<(int Pid, int Index)>();
        var times = new StartTimes(processTimes);
        foreach (string kind in new[] { JoystickOemKind, GameInputKind })
        {
            string[] entries;
            try
            {
                using var k = hive.OpenSubKey($@"{root}\{kind}");
                if (k == null) continue;
                entries = k.GetSubKeyNames();
            }
            // OpenSubKey throws SecurityException on a key it may not open,
            // and GetSubKeyNames IOException on a key deleted during the
            // read.
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { continue; }
            // One entry at a time, as MachineClaims.ReadAll reads records:
            // another program's teardown restores an entry, and deletes it,
            // when its last holder goes, and the entries after it still count.
            foreach (string entry in entries)
            {
                try
                {
                    using var h = hive.OpenSubKey($@"{root}\{kind}\{entry}\{HoldersKey}");
                    if (h == null) continue;
                    foreach (string holder in h.GetValueNames())
                        if (TryParseHolder(holder, out int pid, out _, out int index) && HolderAlive(holder, times))
                            list.Add((pid, index));
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
            }
        }
        return list;
    }

    /// <summary>Whether no GameInput or joy.cpl hold of this process stands
    /// in the ledger at <paramref name="root"/> of <paramref name="hive"/>,
    /// once any that does is released through <paramref name="release"/>,
    /// which gets their names. An exit deletes its owner record only then
    /// (OwnerSweep.ExitRecordMayGo): the census takes a running process's
    /// holder that no live record names for a v1.12.0 program's. A ledger
    /// that cannot be read counts as holding. HKLM's ledger and
    /// <see cref="ReleaseHolds"/> for the exit, a scratch key and a release
    /// of its own for the coexistence probe's OF13.</summary>
    internal static bool OwnHoldsSettled(RegistryKey hive, string root, Action<string[]> release)
    {
        string[]? held = OwnHolds(hive, root);
        if (held is { Length: 0 }) return true;
        if (held != null)
        {
            try { release(held); } catch { }
        }
        return OwnHolds(hive, root) is { Length: 0 };
    }

    /// <summary>Release the named holds: the dispose's release, for every
    /// controller index they name.</summary>
    internal static void ReleaseHolds(string[] holders) => Sweep(holders);

    /// <summary>The names of this process's holds in the ledger at
    /// <paramref name="root"/> of <paramref name="hive"/>, or null when a
    /// part of it cannot be read.</summary>
    private static string[]? OwnHolds(RegistryKey hive, string root)
    {
        string prefix = $"{Environment.ProcessId}-{s_selfStart:X}-";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string kind in new[] { JoystickOemKind, GameInputKind })
        {
            try
            {
                using var k = hive.OpenSubKey($@"{root}\{kind}");
                if (k == null) continue;
                foreach (string entry in k.GetSubKeyNames())
                {
                    using var h = k.OpenSubKey($@"{entry}\{HoldersKey}");
                    if (h != null) names.UnionWith(h.GetValueNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)));
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return null; }
        }
        return names.ToArray();
    }

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
    /// controller holds and <paramref name="inUse"/> does not name.
    /// <paramref name="inUse"/> runs once the lock is held, so the gap
    /// between its reading and the delete is the table walk alone, and
    /// not this lock's wait, however long another program's ledger work
    /// holds it. A null answer keeps every table. Returns the number
    /// removed.</summary>
    internal static int RemoveLegacyTables(Func<HashSet<string>?>? inUse = null)
    {
        using var m = Acquire();
        try
        {
            var keep = HeldGameInputKeys();
            if (inUse != null)
            {
                var used = inUse();
                if (used == null) return 0;
                keep.UnionWith(used);
            }
            return GameInputMapping.RemoveLegacyTables(keep);
        }
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
    /// that the ledger cannot put back, and when the ledger's lock is not
    /// free within <paramref name="timeoutMs"/>. The caller holds the
    /// devnode lock (<see cref="WinUsbBinding.AcquireDevnodeLock"/>), which a
    /// restore also holds from its check of the entry to its delete.</summary>
    internal static void ClaimWinUsb(string instanceId, string? priorService, string? priorInf, int controllerIndex,
                                     int timeoutMs)
    {
        string entryPath = $@"{Root}\{WinUsbKind}\{WinUsbEntryName(instanceId)}";
        using var m = LedgerLock.Acquire(TimeoutScale.Apply(timeoutMs));
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
            // Each claim writes a value of its own, so a restore that queued
            // the entry can tell a claim made since under the same holder
            // name, as by a controller of the same index after the cleanup
            // removed the program's devices, from the one it saw
            // (WinUsbEntryUnheld).
            using var holders = Registry.LocalMachine.CreateSubKey($@"{entryPath}\{HoldersKey}");
            holders.SetValue(HolderName(controllerIndex), Random.Shared.NextInt64(), RegistryValueKind.QWord);
        }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>One change to the cleanup's note, the INF names of the
    /// WinUSB packages it left in the driver store: drop
    /// <see cref="Remove"/>, or every name when <see cref="RemoveAll"/>,
    /// then add <see cref="Add"/>. Every change is made through
    /// <see cref="Replace"/>, <see cref="Cleanup"/> or <see cref="Added"/>,
    /// which registry_ownership_check's offline part checks.</summary>
    internal readonly record struct NoteChange(string[] Remove, string[] Add, bool RemoveAll = false)
    {
        /// <summary>The uninstall cleanup's change: the note becomes the
        /// packages it kept. Its listing, under the deploy lock that every
        /// change takes, is the whole driver store at that moment, so a
        /// noted name it did not list is a package no longer there, and the
        /// note goes when nothing is kept.</summary>
        internal static NoteChange Replace(IEnumerable<string> kept) =>
            new(Array.Empty<string>(), kept.ToArray(), RemoveAll: true);

        /// <summary>The deferred run's change: the names it looked at go,
        /// and the packages it kept are added. A name it did not look at
        /// stays.</summary>
        internal static NoteChange Cleanup(IEnumerable<string> looked, IEnumerable<string> kept) =>
            new(looked.ToArray(), kept.ToArray());

        /// <summary>A program added this package again, so it is no
        /// cleanup's leftover. Every other name stays.</summary>
        internal static NoteChange Added(string infName) => new(new[] { infName }, Array.Empty<string>());

        /// <summary>The note after this change, from
        /// <paramref name="noted"/>.</summary>
        internal string[] ApplyTo(string[] noted)
        {
            var names = new SortedSet<string>(RemoveAll ? Array.Empty<string>() : noted, StringComparer.OrdinalIgnoreCase);
            names.ExceptWith(Remove);
            names.UnionWith(Add);
            return names.ToArray();
        }
    }

    /// <summary>Note which WinUSB packages, by INF name, a cleanup left in
    /// the driver store (<see cref="WinUsbBinding.NoteAfterRemoval"/>).
    /// While a name stands, the restore that resolves the last WinUSB entry
    /// removes that package, and so does the first sweep of each program
    /// that finds no WinUSB entry (<see cref="Sweep"/>).</summary>
    internal static void NoteWinUsbPackagesLeft(NoteChange change) => Note(change);

    /// <summary>A program added this package again, so it is no cleanup's
    /// leftover. Every other name in the note stays.</summary>
    internal static void NoteWinUsbPackageAdded(string infName) => Note(NoteChange.Added(infName));

    /// <summary>Write a note change when it is made.
    ///
    /// <para>Under the deploy lock, which every caller holds already: the
    /// uninstall cleanup, a package add and the deferred removal. So the
    /// changes of every program on the machine land in the order they were
    /// decided, each against the note as the one before left it, and none
    /// waits for another program. The note has a key of its own,
    /// <see cref="NoteKey"/>, outside the ledger's lock, which a frozen
    /// program can hold, and outside the ledger's root, which the full
    /// cleanup deletes. Only a write that fails loses a change, and that is
    /// logged.</para></summary>
    private static void Note(NoteChange change)
    {
        if (NoteForProbe is { } probe)
        {
            probe(change);
            return;
        }
        NoteIn(Registry.LocalMachine, change, TakeDeployLock);
    }

    /// <summary>Write <paramref name="change"/> to the note in
    /// <paramref name="hive"/>, read and written while the lock
    /// <paramref name="takeLock"/> takes is held, released by the action it
    /// returns. The SDK passes HKLM and the deploy lock.
    /// registry_ownership_check's offline part passes HKCU and no lock. A
    /// failure is logged and loses this change.</summary>
    internal static void NoteIn(RegistryKey hive, NoteChange change, Func<Action> takeLock)
    {
        try
        {
            Action release = takeLock();
            try { NoteWithin(change, () => NotedPackages(hive), names => WriteNote(hive, names)); }
            finally { release(); }
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"    ledger: the WinUSB package note was not written: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Action TakeDeployLock()
    {
        var m = DriverDeploy.Acquire();
        return () =>
        {
            m.ReleaseMutex();
            m.Dispose();
        };
    }

    /// <summary>A note change against the note as it stands: the names
    /// <paramref name="read"/> gives, changed, go to <paramref name="write"/>,
    /// or null when none is left, which deletes the key.
    /// registry_ownership_check's offline part checks it. The caller holds
    /// the deploy lock.</summary>
    internal static void NoteWithin(NoteChange change, Func<string[]> read, Action<string[]?> write)
    {
        string[] names = change.ApplyTo(read());
        write(names.Length > 0 ? names : null);
    }

    /// <summary>Write the note's names to <paramref name="hive"/>, or delete
    /// its key when there are none. The SDK passes HKLM.
    /// registry_ownership_check's offline part passes HKCU, which it can
    /// write without elevation.</summary>
    internal static void WriteNote(RegistryKey hive, string[]? names)
    {
        if (names != null)
        {
            using var key = hive.CreateSubKey(NoteKey);
            key.SetValue(V_WinUsbPackages, names, RegistryValueKind.MultiString);
        }
        else hive.DeleteSubKeyTree(NoteKey, throwOnMissingSubKey: false);
    }

    /// <summary>Takes each note change in place of its write.
    /// registry_ownership_check's offline part sets it to read the changes
    /// the production call sites make. Null outside the probe.</summary>
    internal static Action<NoteChange>? NoteForProbe;

    /// <summary>The INF names the note in <paramref name="hive"/> holds,
    /// HKLM in the SDK (see <see cref="WriteNote"/>).</summary>
    internal static string[] NotedPackages(RegistryKey hive)
    {
        using var key = hive.OpenSubKey(NoteKey);
        return key?.GetValue(V_WinUsbPackages) as string[] ?? Array.Empty<string>();
    }

    // Set once this program's sweep has handed a standing note to
    // RemoveLeftPackages.
    private static int s_leftPackagesTried;

    /// <summary>The cleanup's note names a package and no WinUSB entry
    /// remains. The caller holds the ledger's lock.</summary>
    private static bool PackagesLeftNoted() => PackagesLeftNoted(Registry.LocalMachine);

    /// <summary>The same, read from <paramref name="hive"/> (see
    /// <see cref="WriteNote"/>).</summary>
    internal static bool PackagesLeftNoted(RegistryKey hive)
    {
        using var root = hive.OpenSubKey(Root);
        using var kind = root?.OpenSubKey(WinUsbKind);
        return LeftToRemove(NotedPackages(hive), kind?.SubKeyCount ?? 0);
    }

    /// <summary>The note names a package and no WinUSB entry remains, the
    /// <paramref name="entries"/> the ledger holds. registry_ownership_check's
    /// offline part checks it.</summary>
    internal static bool LeftToRemove(string[] noted, int entries) => noted.Length > 0 && entries == 0;

    /// <summary>The packages the cleanup's note names, when no WinUSB entry
    /// remains, read under the ledger's lock, or none. The deferred package
    /// removal reads it under the deploy and devnode locks
    /// (<see cref="WinUsbBinding.RemovePackage"/>).</summary>
    internal static string[] WinUsbPackagesLeftToRemove() => LeftToRemoveLocked(Registry.LocalMachine, () =>
    {
        var m = LedgerLock.Acquire(TimeoutScale.Apply(RestoreLedgerTimeoutMs));
        return () =>
        {
            m.ReleaseMutex();
            m.Dispose();
        };
    });

    /// <summary>The same, read from <paramref name="hive"/> while the lock
    /// <paramref name="takeLock"/> takes is held, released by the action it
    /// returns. The SDK passes HKLM and the ledger's lock.
    /// registry_ownership_check's offline part passes HKCU and no
    /// lock.</summary>
    internal static string[] LeftToRemoveLocked(RegistryKey hive, Func<Action> takeLock)
    {
        Action release = takeLock();
        try { return LeftToRemoveIn(hive); }
        finally { release(); }
    }

    /// <summary>The same, read from <paramref name="hive"/> (see
    /// <see cref="WriteNote"/>).</summary>
    internal static string[] LeftToRemoveIn(RegistryKey hive) =>
        PackagesLeftNoted(hive) ? NotedPackages(hive) : Array.Empty<string>();

    /// <summary>Take back a claim whose install did not happen: the
    /// controller's hold goes, and the entry with it once nothing holds it,
    /// since the devnode's driver did not change. A lock that is not free
    /// within <paramref name="timeoutMs"/> leaves the hold, which a restore
    /// drops, since the devnode is not on a HIDMaestro package. The caller
    /// holds the devnode lock.</summary>
    internal static void UnclaimWinUsb(string instanceId, int controllerIndex, int timeoutMs)
    {
        string name = WinUsbEntryName(instanceId);
        using var m = LedgerLock.Acquire(TimeoutScale.Apply(timeoutMs));
        try
        {
            using var kindKey = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}", writable: true);
            if (kindKey == null) return;
            bool empty;
            using (var holders = kindKey.OpenSubKey($@"{name}\{HoldersKey}", writable: true))
            {
                holders?.DeleteValue(HolderName(controllerIndex), throwOnMissingValue: false);
                empty = holders == null || holders.GetValueNames().Length == 0;
            }
            if (empty) kindKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>A registry key name for a devnode: instance ids carry
    /// backslashes, which a key name cannot.</summary>
    private static string WinUsbEntryName(string instanceId)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(instanceId.ToUpperInvariant())))[..16];

    /// <summary>The devnodes the ledger holds a WinUSB binding for: each
    /// entry with a holder recorded, live or not. An entry with none waits
    /// for its device to leave.</summary>
    internal static List<string> HeldWinUsbDevnodes()
    {
        var list = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}");
            if (k == null) return list;
            foreach (var n in k.GetSubKeyNames())
                using (var e = k.OpenSubKey(n))
                using (var h = e?.OpenSubKey(HoldersKey))
                    if (e?.GetValue(V_InstanceId) is string id && h != null && h.ValueCount > 0) list.Add(id);
        }
        catch { }
        return list;
    }

    // ── Release ──────────────────────────────────────────────────────────

    /// <summary>Teardown of one controller: drop its holds, restore every
    /// entry no live controller holds any more.</summary>
    internal static void Release(int controllerIndex) => Sweep(new[] { HolderName(controllerIndex) });

    /// <summary>Teardown of several controllers at once, as at process exit:
    /// one acquisition of the ledger's lock drops all their holds and
    /// restores their GameInput and joy.cpl entries, so a squatted v1.12.0
    /// mutex costs those one long wait. The N WinUSB entries they held are
    /// put back after the lock is let go, as in every sweep
    /// (<see cref="RestoreWinUsbEntries"/>), and take the lock again on their
    /// own: up to twice per entry and once for the empty keys. With the name
    /// marked by the long wait, that is up to (2N+1) waits of the short bound
    /// (<see cref="LedgerLock"/>, 500 ms scaled) after the long one.
    ///
    /// <para>That bound is the wait for v1.12.0's mutex alone. The wait for
    /// the ledger's own mutex has none of it: this program's follow-up
    /// threads (<see cref="FollowUp"/>) take that mutex too, one scope at a
    /// time, with 250 ms between laps. Each scope holds it for a few registry
    /// reads and its own wait for v1.12.0's mutex: the short bound once the
    /// name is marked, up to 5 s scaled before that, and, against an owner
    /// read as SYSTEM or Administrators, as long as the acquisition's own
    /// bound allows. The per-entry acquisitions give up
    /// after <see cref="RestoreLedgerTimeoutMs"/> (30 s scaled) and leave the
    /// entry for the next sweep. The first acquisition and the one for the
    /// empty keys wait with no bound of their own.</para></summary>
    internal static void Release(IEnumerable<int> controllerIndices) =>
        Sweep(controllerIndices.Select(HolderName).ToArray());

    /// <summary>Restore every entry whose holders are all gone.</summary>
    internal static void PruneDead() => Sweep(null);

    /// <summary>Drop one controller's hold on one devnode's WinUSB entry, as
    /// when its device came back on another devnode, and put the devnode
    /// back once nothing holds it.</summary>
    internal static void ReleaseWinUsb(string instanceId, int controllerIndex)
    {
        string name = WinUsbEntryName(instanceId);
        using (var m = Acquire())
        {
            try
            {
                using var holders = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}\{name}\{HoldersKey}", writable: true);
                holders?.DeleteValue(HolderName(controllerIndex), throwOnMissingValue: false);
            }
            finally { m.ReleaseMutex(); }
        }
        RestoreWinUsbEntries(new List<WinUsbQueued> { new(name, instanceId, Array.Empty<string>(), Own: false) }, waitGoneMs: 0, ignoreHolders: false);
    }

    private static void Sweep(string[]? releasing)
    {
        var winUsb = new List<WinUsbQueued>();
        bool packagesLeft = false;
        using (var m = Acquire())
        {
            try
            {
                var times = new StartTimes(ProcessCreateTimes);
                foreach (var kind in AllKinds)
                {
                    using var kindKey = Registry.LocalMachine.OpenSubKey($@"{Root}\{kind}", writable: true);
                    if (kindKey == null) continue;
                    foreach (var name in kindKey.GetSubKeyNames())
                    {
                        bool restore, own = false;
                        using (var holders = kindKey.OpenSubKey($@"{name}\{HoldersKey}", writable: true))
                        {
                            if (holders != null)
                                foreach (var h in holders.GetValueNames())
                                {
                                    bool released = releasing != null && Array.IndexOf(releasing, h) >= 0;
                                    if (released) own = true;
                                    if (released || !HolderAlive(h, times))
                                        holders.DeleteValue(h, throwOnMissingValue: false);
                                }
                            restore = holders == null || holders.GetValueNames().Length == 0;
                        }
                        if (!restore) continue;
                        if (kind == WinUsbKind) TakeWinUsbEntry(kindKey, name, winUsb, own);
                        else RestoreEntry(kindKey, kind, name);
                    }
                }
                DeleteEmptyKeys();
                // The packages a cleanup left, whose removal at the restore
                // of the last entry did not happen: a note written or a
                // removal failed after that restore, or no entry was left to
                // restore. The first sweep of each program with no WinUSB
                // entry tries again, once, so a lasting leftover, a package a
                // gone devnode keeps, costs a teardown nothing after it.
                packagesLeft = winUsb.Count == 0 && PackagesLeftNoted();
            }
            catch { }
            finally { m.ReleaseMutex(); }
        }
        if (packagesLeft && Interlocked.Exchange(ref s_leftPackagesTried, 1) == 0) RemoveLeftPackages();
        // A teardown releases right after its detach, so the devnode of an
        // entry the releasing controller held gets that long to leave. Any
        // other entry is not waited for. A devnode still there is left as
        // it is, and its entry waits for its follow-up or a later sweep
        // (WinUsbBinding.RestoreLocked).
        if (winUsb.Count > 0) WinUsbQueuedForProbe?.Invoke();
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
                        if (kind == WinUsbKind) TakeWinUsbEntry(kindKey, name, winUsb, own: false);
                        else RestoreEntry(kindKey, kind, name);
                    }
                }
                if (winUsb.Count == 0) Registry.LocalMachine.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
            }
            catch { }
            finally { m.ReleaseMutex(); }
        }
        if (winUsb.Count > 0) WinUsbQueuedForProbe?.Invoke();
        RestoreWinUsbEntries(winUsb, waitGoneMs: 0, ignoreHolders: true);
    }

    /// <summary>Called by a sweep and by the full cleanup once they have
    /// queued WinUSB entries and let the ledger's lock go, before the
    /// restores. Coexistence probe section Z sets it through reflection to
    /// claim an entry in that window. Null outside the probe.</summary>
    internal static Action? WinUsbQueuedForProbe;

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
    /// had when it was queued, each as its name and the value its claim
    /// wrote (<see cref="HolderClaim"/>). <see cref="Own"/>: the releasing
    /// controller held it, so the restore waits for its devnode to
    /// leave.</summary>
    private readonly record struct WinUsbQueued(string Name, string InstanceId, string[] Seen, bool Own);

    /// <summary>The driver INF an entry recorded for its devnode at its claim,
    /// empty when the devnode had none, and null when the entry cannot be
    /// read, which keeps the entry of a gone devnode with no driver for a
    /// later sweep (<see cref="WinUsbBinding.RestoreLocked"/>). The caller
    /// holds the devnode lock, which every claim and every delete of an entry
    /// with a devnode take, so the entry does not change under the read.</summary>
    private static string? PriorInfOf(string name)
    {
        try
        {
            using var e = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}\{name}");
            return e?.GetValue(V_PriorInf) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>Queue a WinUSB entry for <see cref="RestoreWinUsbEntries"/>.
    /// Its devnode work takes seconds, so it runs after the ledger's lock is
    /// released, and the entry stays until it is done: a process that ends
    /// in between leaves the entry for the next sweep. An entry with no
    /// devnode recorded is deleted now. The caller holds the ledger's
    /// lock.</summary>
    private static void TakeWinUsbEntry(RegistryKey kindKey, string name, List<WinUsbQueued> queue, bool own)
    {
        string? id;
        string[] seen;
        using (var entry = kindKey.OpenSubKey(name))
        {
            id = entry?.GetValue(V_InstanceId) as string;
            using var holders = entry?.OpenSubKey(HoldersKey);
            seen = holders == null ? Array.Empty<string>() : holders.GetValueNames().Select(h => HolderClaim(holders, h)).ToArray();
        }
        if (id != null) queue.Add(new WinUsbQueued(name, id, seen, own));
        else try { kindKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
    }

    /// <summary>Put each queued WinUSB entry's devnode back
    /// (<see cref="WinUsbBinding.RestoreLocked"/>) and delete the entry. An
    /// entry whose devnode is still present, any device's, stays for a later
    /// sweep, and so does one whose driver could not be read.
    /// Each runs under the machine-wide devnode lock, which every claim also
    /// takes, and takes the ledger's lock only to read and delete the entry.
    /// An entry a bind claimed since it was queued stays, also when
    /// <paramref name="ignoreHolders"/> restores it regardless of the
    /// holders it had then.
    ///
    /// <para>An entry another controller held costs this call next to
    /// nothing: no wait for its devnode to leave, the devnode lock only when
    /// no one holds it, nothing at all while a follow-up of any program
    /// watches the entry, and no pnputil while its devnode is present.</para></summary>
    private static void RestoreWinUsbEntries(List<WinUsbQueued> entries, int waitGoneMs, bool ignoreHolders, bool followUp = true)
    {
        if (entries.Count == 0) return;
        var kept = new List<WinUsbQueued>();
        foreach (var entry in entries)
        {
            var (name, id, seen, own) = entry;
            WinUsbBinding.StepForProbe?.Invoke("entry " + id);
            try
            {
                if (followUp && !own && IsFollowed(name))
                {
                    WinUsbBinding.StepForProbe?.Invoke("followed");
                    continue;
                }
                if (own) WinUsbBinding.WaitGone(id, waitGoneMs);
                using var devnodes = own || !followUp ? WinUsbBinding.AcquireDevnodeLock() : WinUsbBinding.TryAcquireDevnodeLock();
                if (devnodes == null)
                {
                    kept.Add(entry);
                    continue;
                }
                try
                {
                    if (!WinUsbEntryUnheld(name, ignoreHolders, seen)) continue;
                    if (!WinUsbBinding.RestoreLocked(id, PriorInfOf(name)))
                    {
                        DeviceOrchestrator.LogDiag($"    ledger: the WinUSB entry of {id} stays for a later sweep");
                        kept.Add(entry);
                        continue;
                    }
                    using var m = LedgerLock.Acquire(TimeoutScale.Apply(RestoreLedgerTimeoutMs));
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
        bool removePackages = false;
        try
        {
            using var m = Acquire();
            try
            {
                removePackages = PackagesLeftNoted();
                DeleteEmptyKeys();
            }
            finally { m.ReleaseMutex(); }
        }
        catch { }
        if (followUp)
            foreach (var e in kept) FollowUp(e, ignoreHolders);
        if (removePackages) RemoveLeftPackages();
    }

    /// <summary>The package removal the uninstall-grade cleanup left, now
    /// that no WinUSB entry remains: under the deploy lock, on a pool thread,
    /// so no caller waits for another program's deploy. The lock is taken
    /// only when a look with no lock finds the run can do something
    /// (<see cref="WinUsbBinding.LeftRemovalCanChange"/>), so a package a
    /// gone devnode keeps costs a UMDF2 create no wait. The run reads the
    /// note again under the locks and removes no devnode
    /// (<see cref="WinUsbBinding.RemovePackage"/>). A package a devnode still
    /// uses stays, and the note with it. A removal an exit's release starts
    /// can end with the process, pnputil still running: its deploy and
    /// WinUSB mutexes are then abandoned, which every acquirer takes, and
    /// the note stays, so a later program tries again.</summary>
    private static void RemoveLeftPackages() =>
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                RunLeftRemoval(WinUsbPackagesLeftToRemove, WinUsbBinding.LeftRemovalCanChange, () =>
                {
                    using var deploy = DriverDeploy.Acquire();
                    try { WinUsbBinding.RemovePackage(deferred: true); }
                    finally { deploy.ReleaseMutex(); }
                });
            }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    ledger: the WinUSB packages left by a cleanup were not removed: {ex.Message}"); }
        }, null);

    /// <summary>The deferred removal's decision: <paramref name="run"/> only
    /// when <paramref name="canChange"/> finds the run can change something
    /// for the note <paramref name="readNoted"/> gives. While a gone devnode
    /// keeps every noted package, the lasting case, no run can change
    /// anything, and the deploy lock, which every UMDF2 create waits on, is
    /// not taken at all. registry_ownership_check's offline part checks
    /// it.</summary>
    internal static void RunLeftRemoval(Func<string[]> readNoted, Func<string[], bool> canChange, Action run)
    {
        if (canChange(readNoted())) run();
    }

    // The entries a follow-up of this process watches. Guarded by itself.
    private static readonly HashSet<string> s_followed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name, in <see cref="AdminNamespace"/>, of the mutex the
    /// one follow-up of an entry holds, machine-wide.</summary>
    private static string FollowUpObject(string name) => "WinUsbFollowUp." + name;

    /// <summary>True when a follow-up of this or another program watches the
    /// entry: its mutex is held and its deadline, with the last restore's
    /// bound, has not passed. A program frozen while it holds the mutex
    /// holds up no entry past that.</summary>
    private static bool IsFollowed(string name)
    {
        lock (s_followed) if (s_followed.Contains(name)) return true;
        try
        {
            using var m = new Mutex(false, AdminNamespace.Name(FollowUpObject(name)));
            bool free;
            try { free = m.WaitOne(0); }
            catch (AbandonedMutexException) { free = true; }
            if (free) m.ReleaseMutex();
            return !free && NowMs() < FollowDeadline(name) + TimeoutScale.Apply(FollowUpRestoreMs);
        }
        catch { return false; }
    }

    /// <summary>Now, in UTC wall-clock milliseconds.</summary>
    private static long NowMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>The entry's follow-up deadline, 0 when none is recorded. No
    /// call sets one more than five minutes ahead, so a later one, as after
    /// the clock was set back, counts as five minutes from now.</summary>
    private static long FollowDeadline(string name)
    {
        try
        {
            using var e = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}\{name}");
            return e?.GetValue(V_FollowUntil) is long v ? Math.Min(v, NowMs() + TimeoutScale.Apply(FollowUpMs)) : 0;
        }
        catch { return 0; }
    }

    /// <summary>Move the entry's follow-up deadline to
    /// <paramref name="until"/> when that is later, and return the deadline
    /// it had, 0 when none or the entry is gone.</summary>
    private static long ExtendFollowUp(string name, long until)
    {
        using var m = LedgerLock.Acquire(TimeoutScale.Apply(RestoreLedgerTimeoutMs));
        try
        {
            using var entry = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}\{name}", writable: true);
            if (entry == null) return 0;
            long before = entry.GetValue(V_FollowUntil) is long v ? v : 0;
            // No call sets a deadline more than five minutes ahead. A later
            // one, as after the clock was set back, is replaced.
            if (before > NowMs() + TimeoutScale.Apply(FollowUpMs)) before = 0;
            if (until > before) entry.SetValue(V_FollowUntil, until, RegistryValueKind.QWord);
            return before;
        }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>Set by coexistence probe section Z through reflection to keep
    /// a program from starting follow-ups, so each of its sweeps takes the
    /// path a teardown takes when no follow-up watches an entry. False
    /// outside the probe.</summary>
    internal static volatile bool NoFollowUpForProbe;

    /// <summary>Restore an entry a restore kept once more, as soon as its
    /// devnode has left (<see cref="WinUsbBinding.HasLeft"/>) or Windows has
    /// no record of it, within five minutes of the last call for it. One
    /// follow-up runs per entry on the machine: it holds the entry's mutex in
    /// <see cref="AdminNamespace"/> while it watches, on a background thread
    /// of its own, and reads the entry's deadline as it goes. A call that
    /// finds the entry followed, in this program or another, moves that
    /// deadline to five minutes from now and starts nothing, so a teardown's
    /// entry is watched as long as its own follow-up would watch it. A holder
    /// past its deadline and its last restore's bound is frozen or stuck, and
    /// the call watches instead of it. A device present when its holder let
    /// go, on its way out or another that arrived on the instance, is flagged
    /// for its next arrival soon after it goes. An entry still kept then
    /// waits for a later sweep, which starts another follow-up.</summary>
    private static void FollowUp(WinUsbQueued e, bool ignoreHolders)
    {
        if (NoFollowUpForProbe) return;
        long until = NowMs() + TimeoutScale.Apply(FollowUpMs);
        bool start;
        lock (s_followed) start = s_followed.Add(e.Name);
        if (!start)
        {
            // This program's follow-up watches it, and reads the later
            // deadline.
            try { ExtendFollowUp(e.Name, until); }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    ledger: the WinUSB follow-up of {e.InstanceId} was not extended: {ex.Message}"); }
            return;
        }
        var t = new Thread(() =>
        {
            Mutex? m = null;
            bool owned = false;
            try
            {
                // The deadline goes in before the mutex is taken, so a program
                // that finds the mutex held reads a live holder's deadline.
                long before = ExtendFollowUp(e.Name, until);
                m = new Mutex(false, AdminNamespace.Name(FollowUpObject(e.Name)));
                try { owned = m.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                // Another program's follow-up watches it to the deadline just
                // written, unless that one is past its own.
                if (!owned && NowMs() < Math.Min(before, until) + TimeoutScale.Apply(FollowUpRestoreMs)) return;
                while (true)
                {
                    while (!WinUsbBinding.HasLeft(e.InstanceId) && WinUsbBinding.HasRecord(e.InstanceId)
                           && NowMs() < Math.Max(until, FollowDeadline(e.Name)))
                        Thread.Sleep(250);
                    RestoreWinUsbEntries(new List<WinUsbQueued> { e with { Own = false } }, waitGoneMs: 0, ignoreHolders, followUp: false);
                    // A deadline another call moved during the restore is
                    // watched to its end. A deleted entry has none.
                    if (NowMs() >= FollowDeadline(e.Name)) break;
                    Thread.Sleep(250);
                }
            }
            catch (Exception ex) { DeviceOrchestrator.LogDiag($"    ledger: the WinUSB follow-up of {e.InstanceId} stopped: {ex.Message}"); }
            finally
            {
                if (owned) m!.ReleaseMutex();
                m?.Dispose();
                lock (s_followed) s_followed.Remove(e.Name);
            }
        }) { IsBackground = true, Name = "HMWinUsbFollowUp" };
        t.Start();
    }

    /// <summary>One holder's claim: its name and the value its claim wrote,
    /// which differs between two claims of one name.</summary>
    private static string HolderClaim(RegistryKey holders, string name) => $"{name}={holders.GetValue(name)}";

    /// <summary>True when the WinUSB entry still exists and no live
    /// controller holds it. With <paramref name="ignoreHolders"/>, true when
    /// it exists and every live holder's claim is one of
    /// <paramref name="seen"/>, the claims it had when it was queued: a bind
    /// that claimed it since keeps it, also under a holder name it had. Holders
    /// whose process is gone are dropped. The caller holds the devnode lock,
    /// so the wait for the ledger's is bounded.</summary>
    private static bool WinUsbEntryUnheld(string name, bool ignoreHolders, string[] seen)
    {
        using var m = LedgerLock.Acquire(TimeoutScale.Apply(RestoreLedgerTimeoutMs));
        try
        {
            using var entry = Registry.LocalMachine.OpenSubKey($@"{Root}\{WinUsbKind}\{name}");
            if (entry == null) return false;
            using var holders = entry.OpenSubKey(HoldersKey, writable: true);
            if (holders == null) return true;
            var times = new StartTimes(ProcessCreateTimes);
            foreach (var h in holders.GetValueNames())
                if (!HolderAlive(h, times)) holders.DeleteValue(h, throwOnMissingValue: false);
            var live = holders.GetValueNames();
            return ignoreHolders
                ? live.All(h => seen.Contains(HolderClaim(holders, h), StringComparer.Ordinal))
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
