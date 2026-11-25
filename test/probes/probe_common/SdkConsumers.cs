// Another running program that holds machine-wide HIDMaestro state, by the
// rule the battery's own watcher applies (test\regression\HMForeign.cs):
// one a live owner record names, or one with a 1.x HIDMaestro.Core.dll
// loaded. A program built on 2.0.0 or later writes its owner record under
// HKLM\SOFTWARE\HIDMaestroOwners at its first machine-wide write and holds
// its owner claim until it exits (sdk\HIDMaestro.Core\Internal\
// MachineClaims.cs, ReadRecord and IsHeld), so a program with controllers,
// one between its InstallDriver and its first create, and a NativeAOT or
// single-file program, which loads no HIDMaestro.Core.dll, all count. One
// that only loaded a 2.x SDK, as a test host whose tests create no device
// does, writes nothing machine-wide and shares no section and no device
// with a probe, so it does not. A 1.x build writes no record, so its module
// is looked for the way HMForeign looks (OpenProcess with
// PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, EnumProcessModulesEx with
// LIST_MODULES_ALL, GetModuleBaseNameW, then GetModuleFileNameExW and its
// file version), each process on a thread of its own.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

internal static class SdkConsumers
{
    const string RecordsRoot = @"SOFTWARE\HIDMaestroOwners";
    const string OwnerPipePrefix = @"\\.\pipe\ProtectedPrefix\Administrators\HIDMaestro.Owner.";
    const string Module = "HIDMaestro.Core.dll";
    const int ERROR_FILE_NOT_FOUND = 2;
    const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010, LIST_MODULES_ALL = 3;

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WaitNamedPipe(string name, uint timeoutMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);

    [DllImport("psapi.dll", EntryPoint = "GetModuleBaseNameW", CharSet = CharSet.Unicode)]
    static extern int GetModuleBaseName(IntPtr process, IntPtr module, StringBuilder name, int size);

    [DllImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", CharSet = CharSet.Unicode)]
    static extern int GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder name, int size);

    /// <summary>"name (pid)" of a program other than this one that holds
    /// machine-wide HIDMaestro state, or null when there is none. With
    /// <paramref name="consider"/>, only the process IDs it accepts are
    /// looked at.</summary>
    public static string? Find(Func<int, bool>? consider = null)
    {
        int self = Environment.ProcessId;
        foreach (int pid in LiveOwnerPids())
        {
            if (pid == self || pid <= 4) continue;
            if (consider != null && !consider(pid)) continue;
            // A program that exits after its claim was read is gone.
            try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} ({pid})"; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return FindOneX(self, consider);
    }

    /// <summary>The process IDs the live owner records name. A record that
    /// a sweep deletes while this reads it is skipped, as
    /// MachineClaims.ReadAll skips one.</summary>
    static List<int> LiveOwnerPids()
    {
        var pids = new List<int>();
        RegistryKey? root = null;
        string[] ids;
        try
        {
            root = Registry.LocalMachine.OpenSubKey(RecordsRoot);
            if (root == null) return pids;
            ids = root.GetSubKeyNames();
        }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
        {
            root?.Dispose();
            return pids;
        }
        using (root)
        {
            foreach (string id in ids)
            {
                try
                {
                    using var rec = root.OpenSubKey(id);
                    if (rec?.GetValue("Pid") is int pid && IsHeld(OwnerPipePrefix + id)) pids.Add(pid);
                }
                catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException) { }
            }
        }
        return pids;
    }

    /// <summary>MachineClaims.IsHeld: true and ERROR_SEM_TIMEOUT both mean a
    /// holder, ERROR_FILE_NOT_FOUND means none, and any other error counts
    /// as a holder. The probe opens nothing, so it keeps no claim alive.</summary>
    static bool IsHeld(string pipe) =>
        WaitNamedPipe(pipe, 1) || Marshal.GetLastWin32Error() != ERROR_FILE_NOT_FOUND;

    /// <summary>"name (pid)" of a process with a 1.x HIDMaestro.Core.dll
    /// loaded, which writes no owner record.</summary>
    static string? FindOneX(int self, Func<int, bool>? consider)
    {
        var processes = Process.GetProcesses();
        string? found = null;
        try
        {
            Parallel.ForEach(processes, (p, loop) =>
            {
                // The idle (0) and System (4) processes hold no user modules.
                if (p.Id == self || p.Id <= 4) return;
                if (consider != null && !consider(p.Id)) return;
                if (ModulePath(p.Id) is not string path || !OneX(path)) return;
                string name;
                try { name = p.ProcessName; } catch (InvalidOperationException) { name = "?"; }
                Interlocked.CompareExchange(ref found, $"{name} ({p.Id})", null);
                loop.Stop();
            });
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
        return found;
    }

    /// <summary>The full path of the process's HIDMaestro.Core.dll, "" when
    /// its path cannot be read, or null when it has none or cannot be
    /// opened. A process still loading its modules can fail the call once,
    /// as dotnet/runtime's EnumProcessModulesUntilSuccess notes, so it is
    /// tried a few times, as HMForeign.ModulePath tries it.</summary>
    static string? ModulePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var modules = new IntPtr[256];
            int needed = 0;
            bool ok = false;
            for (int i = 0; i < 4 && !ok; i++)
            {
                ok = EnumProcessModulesEx(h, modules, modules.Length * IntPtr.Size, out needed, LIST_MODULES_ALL);
                if (ok && needed > modules.Length * IntPtr.Size)
                {
                    modules = new IntPtr[needed / IntPtr.Size];
                    ok = false;
                }
            }
            if (!ok) return null;
            int count = Math.Min(modules.Length, needed / IntPtr.Size);
            var name = new StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                name.Length = 0;
                if (GetModuleBaseName(h, modules[i], name, name.Capacity) > 0
                    && string.Equals(name.ToString(), Module, StringComparison.OrdinalIgnoreCase))
                {
                    var path = new StringBuilder(1024);
                    return GetModuleFileNameEx(h, modules[i], path, path.Capacity) > 0 ? path.ToString() : "";
                }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Whether a HIDMaestro.Core.dll at <paramref name="path"/> is
    /// a 1.x build. A path that cannot be read counts as 1.x, so its
    /// program is stood aside for, as HMForeign.OneX decides.</summary>
    static bool OneX(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileMajorPart < 2; }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException or IOException) { return true; }
    }
}
