// What HIDMaestro leaves on a machine, read directly from the registry, the
// driver database, the certificate stores and the file system, so the
// probe's verdict does not rest on the SDK code it checks. A snapshot is
// compared with a later one to show a preview changed nothing, and read
// after a removal to show nothing is left.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

internal sealed class Inventory
{
    /// <summary>Device records whose ID or hardware IDs name HIDMaestro,
    /// each with "present" or "phantom", and every record under Enum\HID
    /// by name.</summary>
    public SortedSet<string> Devnodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Driver database packages whose provider is HIDMaestro.</summary>
    public SortedSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Certificates whose subject names HIDMaestro, by store and
    /// thumbprint.</summary>
    public SortedSet<string> Certificates { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each watched registry key, dumped with its values and
    /// subkeys, or "absent".</summary>
    public SortedDictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Each file and folder under the watched folders, with its
    /// length and last write time.</summary>
    public SortedSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly string[] WatchedKeys =
    {
        @"HKLM\SOFTWARE\HIDMaestro",
        @"HKLM\SOFTWARE\HIDMaestroOwners",
        @"HKLM\SOFTWARE\HIDMaestroRegistryLedger",
        @"HKLM\SOFTWARE\HIDMaestroPackagesLeft",
        @"HKLM\SOFTWARE\HIDMaestroOemOverrides",
        @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceOverrides",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\HIDMaestro",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\HMXInput",
        @"HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput",
        @"HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM",
        @"HKCU\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM",
        @"HKLM\SYSTEM\CurrentControlSet\Control\GameInput\Devices",
        @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}",
        @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{6c53d5fd-6480-440f-b618-476750c5e1a6}",
    };

    internal static string ProgramFilesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HIDMaestro");
    internal static string ProgramDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro");

    /// <summary>The OpenVR path registries the watch reads: this account's
    /// and SYSTEM's.</summary>
    internal static IEnumerable<string> OpenVrFiles() => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath"),
            Path.Combine(Environment.SystemDirectory, @"config\systemprofile\AppData\Local\openvr\openvrpaths.vrpath"),
        }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static Inventory Take()
    {
        var inv = new Inventory();
        inv.ReadDevnodes();
        inv.ReadPackages();
        inv.ReadCertificates();
        foreach (string k in WatchedKeys) inv.Keys[k] = Dump(k);
        inv.ReadFiles(ProgramFilesFolder);
        inv.ReadFiles(ProgramDataFolder);
        foreach (string temp in TempRoots())
        {
            try
            {
                foreach (string d in Directory.EnumerateFileSystemEntries(temp, "HIDMaestro*"))
                {
                    string n = Path.GetFileName(d);
                    // The battery's own transcripts and the sandbox probes'
                    // result folders change while this probe runs.
                    if (n.StartsWith("HIDMaestro_probe_", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("HIDMaestro_sandbox_", StringComparison.OrdinalIgnoreCase)
                        || n.StartsWith("HIDMaestro_uninstall_", StringComparison.OrdinalIgnoreCase)) continue;
                    inv.ReadFiles(d);
                }
            }
            catch { }
        }
        foreach (string f in OpenVrFiles())
            inv.Files.Add($"{f}|{(File.Exists(f) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) : "absent")}");
        return inv;
    }

    /// <summary>What differs between two snapshots, one line each.</summary>
    internal IEnumerable<string> Diff(Inventory later)
    {
        foreach (var (name, a, b) in new[]
                 {
                     ("devnode", Devnodes, later.Devnodes), ("package", Packages, later.Packages),
                     ("certificate", Certificates, later.Certificates), ("file", Files, later.Files),
                 })
        {
            foreach (string x in a.Except(b, StringComparer.OrdinalIgnoreCase)) yield return $"{name} gone: {x}";
            foreach (string x in b.Except(a, StringComparer.OrdinalIgnoreCase)) yield return $"{name} new: {x}";
        }
        foreach (var (k, v) in Keys)
            if (!later.Keys.TryGetValue(k, out var w) || w != v) yield return $"registry key changed: {k}";
    }

    // ── Devices ──────────────────────────────────────────────────────────

    private void ReadDevnodes()
    {
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey == null) return;
        foreach (string e in enumKey.GetSubKeyNames())
        {
            RegistryKey? ek;
            try { ek = enumKey.OpenSubKey(e); } catch { continue; }
            using (ek)
            {
                if (ek == null) continue;
                foreach (string d in ek.GetSubKeyNames())
                {
                    RegistryKey? dk;
                    try { dk = ek.OpenSubKey(d); } catch { continue; }
                    using (dk)
                    {
                        if (dk == null) continue;
                        foreach (string i in dk.GetSubKeyNames())
                        {
                            string id = $@"{e}\{d}\{i}";
                            if (e.Equals("HID", StringComparison.OrdinalIgnoreCase)) { Devnodes.Add($"{id}|record"); }
                            if (!IsHidMaestroRecord(dk, i, id)) continue;
                            Devnodes.Add($"{id}|{(Present(id) ? "present" : "phantom")}");
                        }
                    }
                }
            }
        }
    }

    private static bool IsHidMaestroRecord(RegistryKey device, string instance, string id)
    {
        if (id.Contains("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) || id.Contains("HMCOMPANION", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            using var k = device.OpenSubKey(instance);
            return k?.GetValue("HardwareID") is string[] hw && hw.Any(h => h.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>The HIDMaestro device records, without usbip-win2's host
    /// controllers, which carry the SDK's ROOT\HIDMAESTRO_UDE stamp and are
    /// usbip-win2's.</summary>
    internal List<string> HidMaestroDevnodes() =>
        Devnodes.Where(x => !x.EndsWith("|record", StringComparison.Ordinal))
                .Select(x => x[..x.LastIndexOf('|')])
                .Where(id => !HardwareIdsOf(id).Any(h => h.Contains("USBIP_WIN2", StringComparison.OrdinalIgnoreCase)))
                .ToList();

    /// <summary>HID records of the shapes only HIDMaestro makes on a clean
    /// machine: HIDCLASS, an IG_ segment, and HIDMAESTRO_*.</summary>
    internal List<string> HidMaestroShapedHidRecords() =>
        Devnodes.Where(x => x.EndsWith("|record", StringComparison.Ordinal))
                .Select(x => x[..x.LastIndexOf('|')])
                .Where(id =>
                {
                    string seg = id.Split('\\')[1];
                    return seg.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                        || seg.Contains("IG_", StringComparison.OrdinalIgnoreCase)
                        || seg.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

    internal static string[] HardwareIdsOf(string id)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}");
            return k?.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    // ── Packages and certificates ────────────────────────────────────────

    private void ReadPackages()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverPackages");
        if (root == null) return;
        foreach (string id in root.GetSubKeyNames())
        {
            try
            {
                using var k = root.OpenSubKey(id);
                if (string.Equals(k?.GetValue("Provider") as string, "HIDMaestro", StringComparison.OrdinalIgnoreCase))
                    Packages.Add($"{id}|{k!.GetValue("")}");
            }
            catch { }
        }
    }

    private void ReadCertificates()
    {
        foreach (var name in new[] { StoreName.My, StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (CryptographicException) { continue; }
            foreach (var c in store.Certificates)
                using (c)
                    if (c.Subject.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase))
                        Certificates.Add($@"{name}\{c.Thumbprint}");
        }
    }

    // ── Registry ─────────────────────────────────────────────────────────

    internal static string Dump(string path)
    {
        var (hive, sub) = Split(path);
        try
        {
            using var k = hive.OpenSubKey(sub);
            if (k == null) return "absent";
            var sb = new StringBuilder();
            DumpInto(k, "", sb);
            return sb.ToString();
        }
        catch (Exception e) { return "unreadable: " + e.GetType().Name; }
    }

    private static void DumpInto(RegistryKey k, string rel, StringBuilder sb)
    {
        sb.Append('[').Append(rel).Append("]\n");
        foreach (string v in k.GetValueNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            object? value = k.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string text = value switch
            {
                byte[] b => Convert.ToHexString(b),
                string[] m => string.Join("|", m),
                null => "null",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
            sb.Append(v).Append('=').Append(k.GetValueKind(v)).Append(':').Append(text).Append('\n');
        }
        foreach (string s in k.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var c = k.OpenSubKey(s);
                if (c != null) DumpInto(c, rel + "\\" + s, sb);
            }
            catch { sb.Append('[').Append(rel).Append('\\').Append(s).Append("] unreadable\n"); }
        }
    }

    internal static bool KeyExists(string path)
    {
        var (hive, sub) = Split(path);
        try { using var k = hive.OpenSubKey(sub); return k != null; }
        catch { return true; }
    }

    internal static (RegistryKey Hive, string Sub) Split(string path) =>
        path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? (Registry.CurrentUser, path[5..]) : (Registry.LocalMachine, path[5..]);

    // ── Files ────────────────────────────────────────────────────────────

    private void ReadFiles(string root)
    {
        if (!Directory.Exists(root) && !File.Exists(root)) return;
        // A folder is listed by name only. The times a directory listing gives
        // come from the parent's index entry, and two listings seconds apart
        // read different write times for folders last written two hours
        // earlier (2026-10-20, one of them 43 microseconds apart), so they
        // say nothing about a change. Its entries show any change in it. A
        // file's length and write time are read again from the file itself.
        static string Line(FileSystemInfo e)
        {
            if (e is not FileInfo f) return $"{e.FullName}|dir";
            f.Refresh();
            return $"{f.FullName}|{f.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{f.LastWriteTimeUtc.Ticks}";
        }
        try
        {
            Files.Add(Line(Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root)));
            if (!Directory.Exists(root)) return;
            foreach (var e in new DirectoryInfo(root).EnumerateFileSystemInfos("*",
                         new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                Files.Add(Line(e));
        }
        catch (Exception ex) { Files.Add($"{root}|unreadable {ex.GetType().Name}"); }
    }

    /// <summary>This account's %TEMP%, Windows' Temp folder and every
    /// profile's AppData\Local\Temp.</summary>
    internal static List<string> TempRoots()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string p)
        {
            try { p = Path.GetFullPath(p).TrimEnd('\\'); } catch { return; }
            if (Directory.Exists(p) && seen.Add(p)) list.Add(p);
        }
        Add(Path.GetTempPath());
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (profiles != null)
            foreach (string sid in profiles.GetSubKeyNames())
            {
                using var k = profiles.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is string p) Add(Path.Combine(p, "AppData", "Local", "Temp"));
            }
        return list;
    }

    // ── Plug and Play ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    // devpkey.h: DEVPKEY_Device_LastArrivalDate {83da6326-...},102.
    private static readonly DEVPROPKEY LastArrivalKey = new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 102 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    internal static bool Present(string id) => CM_Locate_DevNodeW(out _, id, 0) == 0;

    /// <summary>Each present USB root hub with its last arrival: a hub that
    /// restarted arrives again and carries a later time.</summary>
    internal static SortedDictionary<string, long> RootHubs()
    {
        var hubs = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb == null) return hubs;
        foreach (string d in usb.GetSubKeyNames())
        {
            if (!d.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
            using var dk = usb.OpenSubKey(d);
            if (dk == null) continue;
            foreach (string i in dk.GetSubKeyNames())
            {
                string id = $@"USB\{d}\{i}";
                if (CM_Locate_DevNodeW(out uint dev, id, 0) != 0) continue;
                var key = LastArrivalKey;
                var buf = new byte[8];
                uint size = 8;
                hubs[id] = CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) == 0 ? BitConverter.ToInt64(buf, 0) : 0;
            }
        }
        return hubs;
    }

    /// <summary>The paths Windows deletes at its next start
    /// (PendingFileRenameOperations entries with an empty target).</summary>
    internal static List<string> PendingDeletes()
    {
        var list = new List<string>();
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        if (k?.GetValue("PendingFileRenameOperations") is not string[] ops) return list;
        for (int n = 0; n + 1 < ops.Length; n += 2)
            if (ops[n + 1].Length == 0) list.Add(ops[n].StartsWith(@"\??\", StringComparison.Ordinal) ? ops[n][4..] : ops[n]);
        return list;
    }
}
