// Another running program with HIDMaestro.Core.dll loaded, found the way
// Process.Modules finds a module (dotnet/runtime release/10.0,
// ProcessManager.Win32.cs NtProcessManager.GetModules): OpenProcess with
// PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, EnumProcessModulesEx with
// LIST_MODULES_ALL, retried as EnumProcessModulesUntilSuccess retries it,
// then GetModuleBaseNameW of each module. Process.Modules also reads each
// module's file name and image information, which ModuleName does not use,
// and walks the processes one at a time. This reads only the base name, and
// walks the processes in parallel, so the probes that refuse to run beside
// another consumer spend well under a second here instead of several.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static unsafe partial class SdkConsumers
{
    const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010, LIST_MODULES_ALL = 3;
    const int MAX_PATH = 260;
    const string Module = "HIDMaestro.Core.dll";

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumProcessModulesEx(IntPtr process, IntPtr* modules, int size, out int needed, uint filter);

    [LibraryImport("psapi.dll", EntryPoint = "GetModuleBaseNameW")]
    private static partial int GetModuleBaseName(IntPtr process, IntPtr module, char* name, int size);

    /// <summary>"name (pid)" of a process other than this one with
    /// HIDMaestro.Core.dll loaded, or null when there is none. With
    /// <paramref name="consider"/>, only the process IDs it accepts are
    /// looked at.</summary>
    public static string? Find(Func<int, bool>? consider = null)
    {
        int self = Environment.ProcessId;
        var processes = Process.GetProcesses();
        string? found = null;
        try
        {
            Parallel.ForEach(processes, (p, loop) =>
            {
                // GetModules refuses the idle (0) and system (4) processes.
                if (p.Id == self || p.Id == 0 || p.Id == 4) return;
                if (consider != null && !consider(p.Id)) return;
                if (!HasModule(p.Id)) return;
                string name;
                try { name = p.ProcessName; } catch { name = "?"; }
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

    static bool HasModule(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!EnumUntilSuccess(h, null, 0, out int needed)) return false;
            IntPtr[] modules;
            while (true)
            {
                modules = new IntPtr[needed / IntPtr.Size];
                int size = needed;
                fixed (IntPtr* m = modules)
                    if (!EnumUntilSuccess(h, m, size, out needed)) return false;
                if (needed <= size) break;
            }
            int count = Math.Min(modules.Length, needed / IntPtr.Size);
            char* name = stackalloc char[MAX_PATH];
            for (int i = 0; i < count; i++)
            {
                int n = GetModuleBaseName(h, modules[i], name, MAX_PATH);
                if (n == Module.Length && new ReadOnlySpan<char>(name, n).Equals(Module, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        finally { CloseHandle(h); }
    }

    // EnumProcessModulesEx fails with ERROR_PARTIAL_COPY on a process not yet
    // initialized or whose module list changes during the call. Process.Modules
    // tries 52 times with a 1 ms sleep between, then gives up on the process.
    static bool EnumUntilSuccess(IntPtr h, IntPtr* modules, int size, out int needed)
    {
        for (int i = 0; ; i++)
        {
            if (EnumProcessModulesEx(h, modules, size, out needed, LIST_MODULES_ALL)) return true;
            if (i > 50) return false;
            Thread.Sleep(1);
        }
    }
}
