// Which processes hold a named kernel object, for a check that finds one
// still there. NtQuerySystemInformation's extended handle table lists every
// handle in the system with its process and the index of its object type.
// Each handle of the object's type in any process is duplicated into this
// one and compared with this process's own handle to the object
// (CompareObjectHandles), so the answer needs no kernel address, which
// Windows hides from a caller without SeDebugPrivilege. Handles of other
// types are never duplicated. The scan takes SeDebugPrivilege on its own
// thread's token, when the process has it, so an elevated probe can
// duplicate from the driver hosts, which run as LocalService, and the
// process token never changes. A process that mapped a section and then
// closed its handle keeps the section through its view and holds no handle,
// so a section can exist with no holder listed. A process the scan could
// not open is counted, never taken for one that holds nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class ObjectHolders
{
    internal readonly record struct Holder(int Pid, string Image, int Handles);

    /// <summary>The holders of the object a handle of the probe names, other
    /// than through that handle, the number of processes with handles of the
    /// object's type the scan could not compare, the object's type index,
    /// and every type index the scan duplicated handles of.</summary>
    internal readonly record struct Scan(IReadOnlyList<Holder> Holders, int Unread, ushort Type, IReadOnlyCollection<ushort> Compared);

    /// <summary>One look at a named object: whether it exists, its owner and
    /// DACL when it opens for READ_CONTROL, and its holders.</summary>
    internal readonly record struct Report(bool Present, string Text, IReadOnlyList<Holder> Holders);

    const int SystemExtendedHandleInformation = 64;
    const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    const uint PROCESS_DUP_HANDLE = 0x0040, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint DUPLICATE_SAME_ACCESS = 0x2;
    const uint READ_CONTROL = 0x00020000, SYNCHRONIZE = 0x00100000, MAXIMUM_ALLOWED = 0x02000000;
    const int ERROR_FILE_NOT_FOUND = 2;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008, SE_PRIVILEGE_ENABLED = 0x2;
    const int SecurityImpersonation = 2;
    const uint PAGE_READWRITE = 0x04;

    [DllImport("ntdll.dll")]
    static extern uint NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out IntPtr target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernelbase.dll")]
    static extern bool CompareObjectHandles(IntPtr first, IntPtr second);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, IntPtr group,
        out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, IntPtr size);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ImpersonateSelf(int level);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool RevertToSelf();
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state,
        int length, IntPtr previous, IntPtr returned);

    // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES. The LUID is two
    // 32-bit fields, so it sits at offset 4.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivilege
    {
        public int Count;
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    readonly record struct Entry(int Pid, long Handle, ushort Type);

    /// <summary>Every handle in the system. SYSTEM_HANDLE_INFORMATION_EX: a
    /// count and a reserved pointer-sized field, then entries of three
    /// pointer-sized fields (object, process id, handle value) and four
    /// 32-bit ones (access, back-trace and type index, attributes,
    /// reserved).</summary>
    static List<Entry> Entries()
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + (1 << 16));
                    continue;
                }
                if (status != 0)
                    throw new InvalidOperationException($"NtQuerySystemInformation returned 0x{status:X8}");
                int p = IntPtr.Size;
                long count = Marshal.ReadIntPtr(buffer, 0).ToInt64();
                int stride = 3 * p + 16;
                var list = new List<Entry>((int)count);
                for (long i = 0; i < count; i++)
                {
                    int at = (int)(2 * p + i * stride);
                    int pid = (int)Marshal.ReadIntPtr(buffer, at + p).ToInt64();
                    long handle = Marshal.ReadIntPtr(buffer, at + 2 * p).ToInt64();
                    ushort type = (ushort)Marshal.ReadInt16(buffer, at + 3 * p + 6);
                    list.Add(new Entry(pid, handle, type));
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("the handle table kept growing past every buffer");
    }

    /// <summary>Impersonate this process on the calling thread and enable
    /// SeDebugPrivilege on that thread's token, which has it only when the
    /// process token does. The process token never changes, so a scan on
    /// another thread neither sees the privilege nor loses it. The returned
    /// action ends the impersonation.</summary>
    static Action DebugOnThisThread()
    {
        if (!ImpersonateSelf(SecurityImpersonation)) return () => { };
        if (OpenThreadToken(GetCurrentThread(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, true, out IntPtr token))
        {
            if (LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid))
            {
                var on = new TokenPrivilege { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref on, 0, IntPtr.Zero, IntPtr.Zero);
            }
            CloseHandle(token);
        }
        return () => RevertToSelf();
    }

    /// <summary>The processes that hold the object <paramref name="own"/>
    /// names, other than through <paramref name="own"/> itself.</summary>
    internal static Scan Of(IntPtr own) => OfAll(new[] { own })[0];

    /// <summary>The holders of each object a handle in <paramref name="own"/>
    /// names, from one snapshot of the handle table read in one pass: each
    /// handle of one of the objects' types in any process is duplicated once
    /// and compared with each of the probe's handles of that type. The
    /// handles in <paramref name="own"/> are left out. This process is
    /// listed when it holds another handle to one of the objects.</summary>
    internal static IReadOnlyList<Scan> OfAll(IReadOnlyList<IntPtr> own)
    {
        int self = Environment.ProcessId;
        var entries = Entries();
        var ownHandles = new HashSet<long>(own.Select(h => h.ToInt64()));
        var types = new ushort[own.Count];
        for (int i = 0; i < own.Count; i++)
        {
            var mine = entries.FirstOrDefault(e => e.Pid == self && e.Handle == own[i].ToInt64());
            if (mine.Pid != self)
                throw new InvalidOperationException("this process's own handle is not in the handle table");
            types[i] = mine.Type;
        }
        var wanted = new HashSet<ushort>(types);
        var counts = own.Select(_ => new Dictionary<int, int>()).ToArray();
        var unread = new Dictionary<ushort, HashSet<int>>();
        var compared = new HashSet<ushort>();
        var processes = new Dictionary<int, IntPtr>();
        Action revert = DebugOnThisThread();
        try
        {
            foreach (var e in entries)
            {
                // Only handles of the objects' own types are duplicated.
                if (!wanted.Contains(e.Type)) continue;
                if (e.Pid == self && ownHandles.Contains(e.Handle)) continue;
                IntPtr handle;
                bool copied = false;
                if (e.Pid == self)
                {
                    handle = new IntPtr(e.Handle);
                }
                else
                {
                    if (!processes.TryGetValue(e.Pid, out IntPtr process))
                        processes[e.Pid] = process = OpenProcess(PROCESS_DUP_HANDLE, false, e.Pid);
                    if (process == IntPtr.Zero
                        || !DuplicateHandle(process, new IntPtr(e.Handle), GetCurrentProcess(), out handle, 0, false, DUPLICATE_SAME_ACCESS))
                    {
                        if (!unread.TryGetValue(e.Type, out var pids)) unread[e.Type] = pids = new HashSet<int>();
                        pids.Add(e.Pid);
                        continue;
                    }
                    copied = true;
                }
                compared.Add(e.Type);
                try
                {
                    for (int i = 0; i < own.Count; i++)
                        if (types[i] == e.Type && CompareObjectHandles(handle, own[i]))
                            counts[i][e.Pid] = counts[i].GetValueOrDefault(e.Pid) + 1;
                }
                finally
                {
                    if (copied) CloseHandle(handle);
                }
            }
        }
        finally
        {
            revert();
            foreach (var process in processes.Values)
                if (process != IntPtr.Zero) CloseHandle(process);
        }
        var images = new Dictionary<int, string>();
        string ImageCached(int pid) => images.TryGetValue(pid, out var s) ? s : images[pid] = ImageOf(pid);
        var scans = new List<Scan>();
        for (int i = 0; i < own.Count; i++)
        {
            var holders = counts[i].OrderBy(kv => kv.Key).Select(kv => new Holder(kv.Key, ImageCached(kv.Key), kv.Value)).ToList();
            var notRead = unread.TryGetValue(types[i], out var pids) ? new HashSet<int>(pids) : new HashSet<int>();
            notRead.ExceptWith(counts[i].Keys);
            scans.Add(new Scan(holders, notRead.Count, types[i], compared));
        }
        return scans;
    }

    /// <summary>A process's image file name, or "?" when it does not open
    /// for PROCESS_QUERY_LIMITED_INFORMATION.</summary>
    internal static string ImageOf(int pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return "?";
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageNameW(process, 0, name, ref size)
                ? System.IO.Path.GetFileName(name.ToString(0, size))
                : "?";
        }
        finally { CloseHandle(process); }
    }

    static string AccountOf(string sid) => sid switch
    {
        "S-1-5-18" => "SYSTEM",
        "S-1-5-19" => "LocalService",
        "S-1-5-20" => "NetworkService",
        "S-1-5-32-544" => "Administrators",
        _ => sid,
    };

    /// <summary>Look at the named event, or section with
    /// <paramref name="section"/>: absent, or its owner, its DACL and every
    /// process that holds it.</summary>
    internal static Report Describe(string name, bool section) => DescribeAll(new[] { (name, section) })[0];

    /// <summary><see cref="Describe"/> for several objects at once. Each is
    /// opened, then one scan (<see cref="OfAll"/>) reads all their holders,
    /// then every handle is closed, so the probe holds none of them once
    /// this returns. An object that refuses READ_CONTROL is opened for
    /// whatever access its DACL grants, and its holders are still listed
    /// with its owner unread.</summary>
    internal static IReadOnlyList<Report> DescribeAll(IReadOnlyList<(string Name, bool Section)> objects)
    {
        var reports = new Report[objects.Count];
        var opened = new List<(int Index, IntPtr Handle, string Security)>();
        try
        {
            for (int i = 0; i < objects.Count; i++)
            {
                var (name, section) = objects[i];
                IntPtr h = Open(name, section, READ_CONTROL);
                if (h != IntPtr.Zero)
                {
                    opened.Add((i, h, SecurityOf(h)));
                    continue;
                }
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_FILE_NOT_FOUND)
                {
                    h = Open(name, section, MAXIMUM_ALLOWED);
                    if (h != IntPtr.Zero)
                    {
                        opened.Add((i, h, "owner unread"));
                        continue;
                    }
                    error = Marshal.GetLastWin32Error();
                }
                reports[i] = error == ERROR_FILE_NOT_FOUND
                    ? new Report(false, $"{name} absent", Array.Empty<Holder>())
                    : new Report(true, $"{name} present, opens for nothing here (error {error})", Array.Empty<Holder>());
            }
            if (opened.Count > 0)
            {
                IReadOnlyList<Scan>? scans = null;
                string failure = "";
                try { scans = OfAll(opened.Select(o => o.Handle).ToList()); }
                catch (Exception e) { failure = e.Message; }
                for (int k = 0; k < opened.Count; k++)
                {
                    var (index, _, security) = opened[k];
                    string name = objects[index].Name;
                    if (scans == null)
                    {
                        reports[index] = new Report(true, $"{name} present, {security}, holders unread: {failure}", Array.Empty<Holder>());
                        continue;
                    }
                    Scan scan = scans[k];
                    string holders = scan.Holders.Count == 0
                        ? "no other handle"
                        : "held by " + string.Join(", ", scan.Holders.Select(x => $"{x.Pid} {x.Image} ({x.Handles})"));
                    string unread = scan.Unread == 0 ? "" : $", {scan.Unread} process(es) with handles of its type unread";
                    reports[index] = new Report(true, $"{name} present, {security}, {holders}{unread}", scan.Holders);
                }
            }
        }
        finally
        {
            foreach (var o in opened) CloseHandle(o.Handle);
        }
        return reports;
    }

    /// <summary>Open exactly <paramref name="access"/>, nothing added, so a
    /// DACL that grants only that right still lets the name open.</summary>
    static IntPtr Open(string name, bool section, uint access) => section
        ? OpenFileMappingW(access, false, name)
        : OpenEventW(access, false, name);

    static string SecurityOf(IntPtr h)
    {
        if (GetSecurityInfo(h, 6, 0x1 | 0x4, out IntPtr owner, IntPtr.Zero, out IntPtr dacl, IntPtr.Zero, out IntPtr sd) != 0)
            return "owner unread";
        try
        {
            return $"owner {AccountOf(new System.Security.Principal.SecurityIdentifier(owner).Value)}, "
                + (dacl == IntPtr.Zero ? "NULL DACL" : "a DACL");
        }
        finally { LocalFree(sd); }
    }

    /// <summary>OF12, which needs no elevation. Under Local\ names this run
    /// makes:
    /// An event. Before anything else opens it, no holder is listed (the
    /// control), and the scan duplicates handles of the event's type only.
    /// A child process (cmd.exe, waiting on a line of input) given a handle
    /// to it is listed with its image. A second handle in this process lists
    /// this process. Once the child exits and the second handle closes, no
    /// holder is listed again. Describe reads the owner and DACL through its
    /// own handle and lists the creating handle, and calls the name absent
    /// once that closes.
    /// A section: Describe reads its owner and DACL and lists the creating
    /// handle, and the scan duplicates handles of the section type only.
    /// An event whose DACL grants everyone READ_CONTROL alone, as U's
    /// locked event does: Describe reads its owner and lists the creating
    /// handle. An event whose DACL grants everyone, its owner included,
    /// SYNCHRONIZE alone: Describe, refused READ_CONTROL, still lists the
    /// creating handle.</summary>
    internal static void SelfCheck(Action<string, bool, string> check)
    {
        string tag = $"{Environment.ProcessId}_{Guid.NewGuid():N}";
        string name = $@"Local\HIDMaestroHolders_{tag}";
        IntPtr ev = CreateEventW(IntPtr.Zero, false, false, name);
        if (ev == IntPtr.Zero)
        {
            check("OF12: the check's event is created", false, $"error {Marshal.GetLastWin32Error()}");
            return;
        }
        int self = Environment.ProcessId;
        System.Diagnostics.Process? child = null;
        try
        {
            Scan alone = Of(ev);
            check("OF12: a new event has no other holder", alone.Holders.Count == 0, Text(alone));
            check("OF12: the scan duplicates handles of the event's type only",
                  alone.Compared.Count == 1 && alone.Compared.Contains(alone.Type),
                  $"type {alone.Type}, compared {string.Join(",", alone.Compared.OrderBy(t => t))}");

            child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c set /p line=")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            })!;
            bool given = DuplicateHandle(GetCurrentProcess(), ev, child.Handle, out _, 0, false, DUPLICATE_SAME_ACCESS);
            Scan withChild = Of(ev);
            check("OF12: a child process holding it is listed with its image",
                  given && withChild.Holders.Any(h => h.Pid == child.Id && h.Handles == 1
                                                      && string.Equals(h.Image, "cmd.exe", StringComparison.OrdinalIgnoreCase)),
                  $"child {child.Id}: {Text(withChild)}");

            IntPtr second = OpenEventW(SYNCHRONIZE, false, name);
            Scan withSecond = Of(ev);
            check("OF12: a second handle in this process lists this process",
                  second != IntPtr.Zero && withSecond.Holders.Any(h => h.Pid == self && h.Handles == 1), Text(withSecond));
            if (second != IntPtr.Zero) CloseHandle(second);

            child.StandardInput.WriteLine();
            child.StandardInput.Close();
            if (!child.WaitForExit(10_000)) child.Kill();
            child.WaitForExit(10_000);
            Scan after = Of(ev);
            check("OF12: once the child exits and the second handle closes, no holder is listed",
                  child.HasExited && after.Holders.Count == 0, Text(after));

            Report report = Describe(name, section: false);
            check("OF12: Describe reads the owner and DACL and lists the creating handle",
                  report.Present && report.Text.Contains(" owner ", StringComparison.Ordinal)
                  && report.Holders.Count == 1 && report.Holders[0].Pid == self, report.Text);
        }
        finally
        {
            if (child is { HasExited: false }) child.Kill();
            child?.Dispose();
            CloseHandle(ev);
        }
        Report gone = Describe(name, section: false);
        check("OF12: and calls the name absent once that handle closes", !gone.Present, gone.Text);

        string sectionName = $@"Local\HIDMaestroHoldersSection_{tag}";
        IntPtr map = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, 4096, sectionName);
        if (map == IntPtr.Zero)
        {
            check("OF12: the check's section is created", false, $"error {Marshal.GetLastWin32Error()}");
        }
        else
        {
            try
            {
                Scan sectionAlone = Of(map);
                check("OF12: the scan of a section duplicates handles of the section type only",
                      sectionAlone.Compared.Count <= 1 && sectionAlone.Compared.All(t => t == sectionAlone.Type),
                      $"type {sectionAlone.Type}, compared {string.Join(",", sectionAlone.Compared.OrderBy(t => t))}");
                Report sectionReport = Describe(sectionName, section: true);
                check("OF12: Describe reads a section's owner and DACL and lists the creating handle",
                      sectionReport.Present && sectionReport.Text.Contains(" owner ", StringComparison.Ordinal)
                      && sectionReport.Holders.Count == 1 && sectionReport.Holders[0].Pid == self, sectionReport.Text);
            }
            finally { CloseHandle(map); }
            Report sectionGone = Describe(sectionName, section: true);
            check("OF12: and calls the section absent once that handle closes", !sectionGone.Present, sectionGone.Text);
        }

        // READ_CONTROL only, for everyone: the shape of U's LockedEvent.
        DaclCase(check, $@"Local\HIDMaestroHoldersReadControl_{tag}", "D:P(A;;0x00020000;;;WD)",
                 "OF12: an event whose DACL grants READ_CONTROL alone shows its owner and its creating handle",
                 r => r.Text.Contains(" owner ", StringComparison.Ordinal) && !r.Text.Contains("owner unread", StringComparison.Ordinal));
        // SYNCHRONIZE only, the owner included (OWNER RIGHTS), so READ_CONTROL
        // is refused to this process too.
        DaclCase(check, $@"Local\HIDMaestroHoldersSynchronize_{tag}", "D:P(A;;0x00100000;;;WD)(A;;0x00100000;;;OW)",
                 "OF12: an event that refuses READ_CONTROL still shows its creating handle",
                 r => r.Text.Contains("owner unread", StringComparison.Ordinal));
    }

    static void DaclCase(Action<string, bool, string> check, string name, string sddl, string title, Func<Report, bool> security)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr sd, IntPtr.Zero))
        {
            check(title, false, $"descriptor error {Marshal.GetLastWin32Error()}");
            return;
        }
        IntPtr ev;
        IntPtr sa = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            Marshal.StructureToPtr(new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd }, sa, false);
            ev = CreateEventW(sa, false, false, name);
        }
        finally
        {
            Marshal.FreeHGlobal(sa);
            LocalFree(sd);
        }
        if (ev == IntPtr.Zero)
        {
            check(title, false, $"create error {Marshal.GetLastWin32Error()}");
            return;
        }
        int self = Environment.ProcessId;
        try
        {
            Report r = Describe(name, section: false);
            check(title, r.Present && security(r) && r.Holders.Count == 1 && r.Holders[0].Pid == self, r.Text);
        }
        finally { CloseHandle(ev); }
    }

    static string Text(Scan scan) =>
        (scan.Holders.Count == 0 ? "no holder" : string.Join(", ", scan.Holders.Select(h => $"{h.Pid} {h.Image} ({h.Handles})")))
        + (scan.Unread == 0 ? "" : $", {scan.Unread} unread");
}
