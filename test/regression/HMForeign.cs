// Other programs that embed HIDMaestro, for the regression battery.
//
// The battery's scenarios measure machine-wide state: devnodes, sweeps,
// XInput slots, Windows.Gaming.Input lists, the registry ledger. A second
// program with its own virtual controllers changes all of it, so a
// scenario that ran beside one measured two programs. The battery waits
// for such a program to exit before each scenario, and a scenario during
// which one appeared runs again once it has gone (regression_battery.ps1,
// Test-Scenario). Load, Steam, browsers and other devices never hold a
// scenario back.
//
// Such a program is one that holds machine-wide HIDMaestro state. A
// program built on 2.0.0 or later writes an owner record, under
// HKLM\SOFTWARE\HIDMaestroOwners, with its process ID and start time, at
// its first machine-wide write and until it exits
// (sdk\HIDMaestro.Core\Internal\MachineClaims.cs), so a running process
// whose record names it holds state. One that loaded the SDK and changed
// nothing, an offline probe of another work tree on 2026-10-19 for one,
// has no record and changes nothing a scenario measures. A program built
// on 1.x writes no record, so one with a 1.x HIDMaestro.Core.dll among its
// modules counts whenever it runs. The module is found as
// test\probes\probe_common\SdkConsumers.cs finds it (OpenProcess with
// PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, EnumProcessModulesEx with
// LIST_MODULES_ALL, GetModuleBaseNameW). The battery's own process tree
// (its probes, the test app and the peers they start) is left out: a
// process belongs to it when its parent does and it was created after that
// parent, so a reused process ID never joins it. So are the programs
// Windows ships in its own directory, which never load HIDMaestro, except
// the PowerShell hosts, whose scripts can: reading every process's modules
// takes about a second on the devbox with 362 processes, and the image
// path a few microseconds.
//
// Windows PowerShell 5.1 compiles this with Add-Type, so it is C# 5.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class HMForeign
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    static extern int GetModuleBaseNameW(IntPtr process, IntPtr module, StringBuilder name, int size);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    static extern int GetModuleFileNameExW(IntPtr process, IntPtr module, StringBuilder name, int size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    const uint TH32CS_SNAPPROCESS = 0x2;
    const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_VM_READ = 0x0010, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint LIST_MODULES_ALL = 3;
    const string Module = "HIDMaestro.Core.dll";

    struct Entry
    {
        public uint Pid;
        public uint Parent;
        public string Name;
    }

    static List<Entry> Snapshot()
    {
        var list = new List<Entry>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W();
            e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
            if (!Process32FirstW(snap, ref e)) return list;
            do
            {
                var x = new Entry();
                x.Pid = e.th32ProcessID;
                x.Parent = e.th32ParentProcessID;
                x.Name = e.szExeFile;
                list.Add(x);
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    static long Created(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            long creation, exit, kernel, user;
            return GetProcessTimes(h, out creation, out exit, out kernel, out user) ? creation : -1;
        }
        finally { CloseHandle(h); }
    }

    // Every process the battery has held, by ID with its creation time,
    // kept after it exits. A probe that crashes leaves its children running
    // with no live parent, and they stay the battery's: the battery fails
    // the scenario that left them and stops them (Orphans), and never waits
    // for its own leftovers as if another program ran them.
    static readonly Dictionary<uint, long> s_own = new Dictionary<uint, long>();

    /// <summary>The battery's processes: <paramref name="root"/>, every
    /// process once seen as one of them that still runs with the same
    /// creation time, and every process created after a parent that is one
    /// of them, running or exited.</summary>
    static HashSet<uint> Own(List<Entry> all, uint root)
    {
        var created = new Dictionary<uint, long>();
        var own = new HashSet<uint>();
        own.Add(root);
        if (!s_own.ContainsKey(root)) s_own[root] = CreatedOnce(created, root);
        foreach (var e in all)
        {
            long known;
            if (e.Pid == root || !s_own.TryGetValue(e.Pid, out known)) continue;
            // An ID another process took since is not the battery's.
            if (CreatedOnce(created, e.Pid) == known) own.Add(e.Pid);
            else s_own.Remove(e.Pid);
        }
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var e in all)
            {
                if (own.Contains(e.Pid) || e.Pid == e.Parent) continue;
                long parent;
                if (own.Contains(e.Parent)) parent = CreatedOnce(created, e.Parent);
                else if (!s_own.TryGetValue(e.Parent, out parent)) continue;
                long child = CreatedOnce(created, e.Pid);
                if (child < 0 || parent < 0 || child < parent) continue;
                own.Add(e.Pid);
                s_own[e.Pid] = child;
                grew = true;
            }
        }
        return own;
    }

    static long CreatedOnce(Dictionary<uint, long> created, uint pid)
    {
        long c;
        if (!created.TryGetValue(pid, out c)) { c = Created(pid); created[pid] = c; }
        return c;
    }

    /// <summary>The processes of <paramref name="root"/>'s tree: the root
    /// and every process whose parent is in the tree and that was created
    /// after that parent.</summary>
    static HashSet<uint> Tree(List<Entry> all, uint root)
    {
        var tree = new HashSet<uint>();
        tree.Add(root);
        var created = new Dictionary<uint, long>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var e in all)
            {
                if (tree.Contains(e.Pid) || !tree.Contains(e.Parent) || e.Pid == e.Parent) continue;
                long child, parent;
                if (!created.TryGetValue(e.Pid, out child)) { child = Created(e.Pid); created[e.Pid] = child; }
                if (!created.TryGetValue(e.Parent, out parent)) { parent = Created(e.Parent); created[e.Parent] = parent; }
                if (child < 0 || parent < 0 || child < parent) continue;
                tree.Add(e.Pid);
                grew = true;
            }
        }
        return tree;
    }

    static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    /// <summary>Whether the process's image is one Windows ships in its own
    /// directory, other than a PowerShell host.</summary>
    static bool WindowsImage(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, path, ref size)) return false;
            string image = path.ToString();
            if (!image.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) return false;
            string file = System.IO.Path.GetFileName(image);
            return !file.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
                && !file.Equals("powershell_ise.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(h); }
    }

    static bool HasModule(uint pid)
    {
        return ModulePath(pid) != null;
    }

    /// <summary>The full path of the process's HIDMaestro.Core.dll, or null
    /// when it has none or cannot be read.</summary>
    static string ModulePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var modules = new IntPtr[256];
            int needed = 0;
            // A process still loading its modules can fail the call once,
            // as dotnet/runtime's EnumProcessModulesUntilSuccess notes, so
            // it is tried a few times.
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
                if (GetModuleBaseNameW(h, modules[i], name, name.Capacity) > 0
                    && string.Equals(name.ToString(), Module, StringComparison.OrdinalIgnoreCase))
                {
                    var path = new StringBuilder(1024);
                    return GetModuleFileNameExW(h, modules[i], path, path.Capacity) > 0 ? path.ToString() : "";
                }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Whether a HIDMaestro.Core.dll at <paramref name="path"/> is
    /// a 1.x build, which writes no owner record. A path that cannot be
    /// read counts as 1.x, so its program is waited for.</summary>
    static bool OneX(string path)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileMajorPart < 2; }
        catch (Exception) { return true; }
    }

    /// <summary>Each live owner record's process ID and start time
    /// (FILETIME, UTC), from HKLM\SOFTWARE\HIDMaestroOwners.</summary>
    static Dictionary<uint, long> Records()
    {
        var map = new Dictionary<uint, long>();
        using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners"))
        {
            if (root == null) return map;
            foreach (var id in root.GetSubKeyNames())
            {
                using (var k = root.OpenSubKey(id))
                {
                    if (k == null) continue;
                    object pid = k.GetValue("Pid"), start = k.GetValue("StartTime");
                    if (pid is int && start is long) map[(uint)(int)pid] = (long)start;
                }
            }
        }
        return map;
    }

    sealed class Read
    {
        public string Name;
        public uint Parent;
        public long Created;
        public long At;
        public bool Windows;
    }

    // Processes read without the module, by ID with their image name and
    // parent, which a reused ID would have to repeat, and when. Even outside
    // the Windows directory, reading every process's modules takes about
    // 0.6 s on the devbox, and opening every process once for its creation
    // time 0.12 s. A program loads HIDMaestro.Core.dll when it starts using
    // it, which can be after its own start, so a process under a minute old
    // is read on every look, and an older one once its entry is 30 s old.
    // The coexistence peer of a test on 2026-10-19 was read before its load
    // and lived 9 s, which a 30 s entry hid. A Windows image is never read
    // again.
    static readonly Dictionary<uint, Read> s_without = new Dictionary<uint, Read>();
    static readonly object s_lock = new object();
    static readonly long Reread = TimeSpan.FromSeconds(30).Ticks;
    static readonly long Young = TimeSpan.FromSeconds(60).Ticks;

    /// <summary>"name (pid)" of a process outside <paramref name="root"/>'s
    /// tree that holds machine-wide HIDMaestro state: one named by a live
    /// owner record, or one with a 1.x HIDMaestro.Core.dll loaded. Null
    /// when there is none.</summary>
    public static string Find(int root)
    {
        lock (s_lock)
        {
            var all = Snapshot();
            var tree = Own(all, (uint)root);
            var records = Records();
            // FILETIME ticks, as GetProcessTimes gives a creation time.
            long now = DateTime.UtcNow.ToFileTimeUtc();
            var live = new HashSet<uint>();
            string found = null;
            foreach (var e in all)
            {
                live.Add(e.Pid);
                // The system idle process and System hold no user modules.
                if (found != null || e.Pid == 0 || e.Pid == 4 || tree.Contains(e.Pid)) continue;
                // A record appears at a program's first machine-wide write,
                // so it is read on every look. Its start time must be this
                // process's, within the second the two clocks may round to,
                // or the ID is another process's now.
                long start;
                if (records.TryGetValue(e.Pid, out start))
                {
                    long c = Created(e.Pid);
                    if (c > 0 && Math.Abs(c - start) < TimeSpan.TicksPerSecond) { found = e.Name + " (" + e.Pid + ")"; continue; }
                }
                Read r;
                bool known = s_without.TryGetValue(e.Pid, out r) && r.Name == e.Name && r.Parent == e.Parent;
                if (known && (r.Windows || (now - r.Created >= Young && now - r.At < Reread))) continue;
                long created = known ? r.Created : Created(e.Pid);
                if (!known && WindowsImage(e.Pid))
                {
                    s_without[e.Pid] = new Read { Name = e.Name, Parent = e.Parent, Created = created, At = now, Windows = true };
                    continue;
                }
                string module = ModulePath(e.Pid);
                if (module != null && OneX(module)) { found = e.Name + " (" + e.Pid + ")"; continue; }
                s_without[e.Pid] = new Read { Name = e.Name, Parent = e.Parent, Created = created, At = now };
            }
            var gone = new List<uint>();
            foreach (var pid in s_without.Keys) if (!live.Contains(pid)) gone.Add(pid);
            foreach (var pid in gone) s_without.Remove(pid);
            return found;
        }
    }

    /// <summary>The process IDs of the battery's processes that embed
    /// HIDMaestro and run with no live parent in <paramref name="root"/>'s
    /// tree: what a probe left behind when it exited.</summary>
    public static int[] Orphans(int root)
    {
        lock (s_lock)
        {
            var all = Snapshot();
            var own = Own(all, (uint)root);
            var tree = Tree(all, (uint)root);
            var list = new List<int>();
            foreach (var e in all)
                if (own.Contains(e.Pid) && !tree.Contains(e.Pid) && HasModule(e.Pid)) list.Add((int)e.Pid);
            return list.ToArray();
        }
    }

    static Timer s_timer;
    static int s_root;
    static int s_busy;
    static string s_seen;

    /// <summary>Starts looking every 2 s, until <see cref="Stop"/>, for a
    /// program outside <paramref name="root"/>'s tree that embeds
    /// HIDMaestro, and keeps the first one seen.</summary>
    public static void Watch(int root)
    {
        Stop();
        s_root = root;
        Volatile.Write(ref s_seen, null);
        s_timer = new Timer(Tick, null, 0, 2000);
    }

    static void Tick(object state)
    {
        // A scan that takes longer than the period never overlaps the next.
        if (Interlocked.Exchange(ref s_busy, 1) == 1) return;
        try
        {
            if (Volatile.Read(ref s_seen) != null) return;
            string found = Find(s_root);
            if (found != null) Interlocked.CompareExchange(ref s_seen, found, null);
        }
        // A process can exit while it is read. The next tick reads again,
        // and Stop takes a last look of its own.
        catch (Exception) { }
        finally { Volatile.Write(ref s_busy, 0); }
    }

    /// <summary>Stops looking, after one last look, and returns the first
    /// program seen, or null.</summary>
    public static string Stop()
    {
        Timer t = s_timer;
        s_timer = null;
        if (t != null)
        {
            using (var done = new ManualResetEvent(false))
            {
                if (t.Dispose(done)) done.WaitOne(10000);
            }
        }
        string seen = Volatile.Read(ref s_seen);
        if (seen == null && t != null) seen = Find(s_root);
        return seen;
    }
}
