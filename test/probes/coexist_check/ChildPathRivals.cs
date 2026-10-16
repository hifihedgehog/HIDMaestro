// [sandbox] "child-paths": the 0xCA invariant across a v1.12.0 program and
// this release creating one identity key. hidclass names a HID child
// HID\<parent's enumerator>\<ParentIdPrefix>&<collection>, and two children
// on one path are a duplicate PDO, bugcheck 0xCA, which a ROOT record turns
// into a boot loop since it is enumerated again at every boot. From v2.0.0
// sidewinder-force-feedback-2 moves from ROOT\VID_045E&PID_001B&IG_00 to
// ROOT\HIDClass with the same token and prefix, so its two parents give two
// child paths, and a v1.12.0 program, which scans only its own enumerator,
// meets no path it could collide with.
//
// Steps, each followed by two checks. Every present HIDMaestro parent's
// HID children, read from the device tree, carry the path the rule gives
// (HID\<enumerator>[&COLnn]\<ParentIdPrefix>&nnnn), and a present parent
// whose controller runs has at least one, so a wrong rule fails the run.
// And no two parents give their children one path: the paths read from
// the tree for present parents, the rule's for ROOT records that are not
// present, which the next boot enumerates:
//   1. A v1.12.0 program installs its driver and creates
//      sidewinder-force-feedback-2 with key K, then is killed.
//   2. A program on this release creates the same profile with K, then is
//      killed. Its parent is under ROOT\HIDClass with the prefix v1.12.0
//      gave K, and when the v1.12.0 parent is still there, the two hold
//      that prefix under two enumerators.
//   3. A v1.12.0 program creates K again, then is killed.
// Runs only inside lane T's Windows Sandbox, behind the markers X8 uses
// (SectionX.X8InSandbox), since it churns devices with v1.12.0 code and
// lets v1.12.0's InstallDriver replace the driver. Anywhere else it stops
// before its first step. Not part of S71.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal static partial class Program
{
    const string ChildPathsKey = "coexist-child-paths";
    const string ChildPathsProfile = "sidewinder-force-feedback-2";
    const string SideWinderV1Enumerator = "VID_045E&PID_001B&IG_00";
    const string SideWinderV2Enumerator = "HIDClass";

    static int ChildPathRivals()
    {
        Console.WriteLine("=== [sandbox] HID child paths across v1.12.0 and this release ===");
        if (!X8InSandbox(out string markers))
        {
            Check("child-paths: runs only inside lane T's Windows Sandbox", false, $"{markers}. Nothing was run.");
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
            return 1;
        }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("child-paths: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return 1;
        }
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }
        Clean();
        try
        {
            // 1. v1.12.0 creates K.
            string? legacyPrefix = null;
            using (var old = PeerProcess.Start("child-paths-v1.12.0-a", "v1.12.0"))
            {
                Check("child-paths 1: the v1.12.0 program installs its driver", Ok(old.Send("install")));
                string co = old.Send($"create profile {ChildPathsProfile} {ChildPathsKey}");
                Check("child-paths 1: the v1.12.0 program creates the SideWinder with key K", Ok(co), co);
                var parents = HmRootParents();
                var legacy = parents.Where(r => r.Enumerator.Equals(SideWinderV1Enumerator, StringComparison.OrdinalIgnoreCase)).ToList();
                Check($"child-paths 1: its parent is under ROOT\\{SideWinderV1Enumerator}",
                      legacy.Count == 1 && legacy[0].Prefix != null, Describe(parents));
                legacyPrefix = legacy.FirstOrDefault()?.Prefix;
                old.Kill();
            }
            NoSharedChildPath("child-paths 1: after the v1.12.0 program is killed");

            // 2. This release creates K.
            using (var nu = PeerProcess.Start("child-paths-new"))
            {
                string cn = nu.Send($"create profile {ChildPathsProfile} {ChildPathsKey}");
                Check("child-paths 2: this release creates the SideWinder with key K", Ok(cn), cn);
                var parents = HmRootParents();
                var moved = parents.Where(r => r.Enumerator.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)).ToList();
                Check("child-paths 2: its parent is under ROOT\\HIDClass with the prefix v1.12.0 gave the key",
                      moved.Count == 1 && moved[0].Prefix != null
                      && (legacyPrefix == null || string.Equals(moved[0].Prefix, legacyPrefix, StringComparison.OrdinalIgnoreCase)),
                      Describe(parents));
                var legacy = parents.FirstOrDefault(r => r.Enumerator.Equals(SideWinderV1Enumerator, StringComparison.OrdinalIgnoreCase));
                if (legacy?.Prefix != null && moved.Count == 1)
                    Check("child-paths 2: the v1.12.0 parent left beside it holds the prefix under another enumerator, so another child path",
                          string.Equals(legacy.Prefix, moved[0].Prefix, StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(ChildPath(legacy), ChildPath(moved[0]), StringComparison.OrdinalIgnoreCase),
                          $"{ChildPath(legacy)} and {ChildPath(moved[0])}");
                else
                    Console.WriteLine("  [INFO] child-paths 2: no v1.12.0 parent was left beside it (the create removed it as the same controller)");
                NoSharedChildPath("child-paths 2: with this release's controller live", SideWinderV2Enumerator);
                nu.Kill();
            }
            NoSharedChildPath("child-paths 2: after this release's program is killed");

            // 3. v1.12.0 creates K again, beside what this release left.
            using (var old = PeerProcess.Start("child-paths-v1.12.0-b", "v1.12.0"))
            {
                string co = old.Send($"create profile {ChildPathsProfile} {ChildPathsKey}");
                Check("child-paths 3: the v1.12.0 program creates the SideWinder with key K again", Ok(co), co);
                NoSharedChildPath("child-paths 3: with the v1.12.0 controller live", SideWinderV1Enumerator);
                old.Kill();
            }
            NoSharedChildPath("child-paths 3: after the v1.12.0 program is killed");
        }
        catch (Exception ex) { Check("child-paths ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        Clean();
        NoSharedChildPath("child-paths: after the cleanup");
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    sealed record ParentRecord(string Root, string Enumerator, string Instance, string? Prefix, bool HidMaestro);

    /// <summary>Every record under Enum\<paramref name="root"/>, present or
    /// not, with its ParentIdPrefix and whether a HIDMaestro hardware ID is
    /// in its HardwareID list.</summary>
    static List<ParentRecord> Records(string root)
    {
        var list = new List<ParentRecord>();
        using var rk = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{root}");
        if (rk == null) return list;
        foreach (string e in rk.GetSubKeyNames())
        {
            using var ek = rk.OpenSubKey(e);
            if (ek == null) continue;
            foreach (string i in ek.GetSubKeyNames())
            {
                using var ik = ek.OpenSubKey(i);
                if (ik == null) continue;
                bool hm = ik.GetValue("HardwareID") is string[] ids
                          && ids.Any(id => id.Equals(@"root\HIDMaestro", StringComparison.OrdinalIgnoreCase)
                                        || id.Equals(@"root\HIDMaestroGamepad", StringComparison.OrdinalIgnoreCase)
                                        || id.Equals(@"root\HIDMaestroXUSB", StringComparison.OrdinalIgnoreCase));
                list.Add(new ParentRecord(root, e, i, ik.GetValue("ParentIdPrefix") as string, hm));
            }
        }
        return list;
    }

    static List<ParentRecord> HmRootParents() => Records("ROOT").Where(r => r.HidMaestro && r.Prefix != null).ToList();

    static string ChildPath(ParentRecord r) => $@"HID\{r.Enumerator.ToUpperInvariant()}\{r.Prefix}";

    static string Describe(IEnumerable<ParentRecord> records) =>
        string.Join(", ", records.Select(r => $@"{r.Root}\{r.Enumerator}\{r.Instance}={r.Prefix}"));

    static string Id(ParentRecord r) => $@"{r.Root}\{r.Enumerator}\{r.Instance}";

    /// <summary>The HID children of a present parent, read from the device
    /// tree.</summary>
    static List<string> TreeChildren(ParentRecord r) =>
        Native.Subtree(Id(r)).Skip(1).Where(c => c.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The child's path without its collection parts: the
    /// HID\&lt;segment&gt;\&lt;prefix&gt; stem, or null for a path the rule
    /// does not give for <paramref name="r"/>.</summary>
    static string? Stem(string child, ParentRecord r)
    {
        var m = Regex.Match(child, @"^HID\\" + Regex.Escape(r.Enumerator) + @"(&COL[0-9A-F]{2})?\\" + Regex.Escape(r.Prefix ?? "") + @"&[0-9A-F]{4}$",
                            RegexOptions.IgnoreCase);
        return m.Success ? ChildPath(r) : null;
    }

    /// <summary>A child path read from the tree without its collection
    /// parts: HID\&lt;segment&gt;\&lt;prefix&gt;.</summary>
    static string TreeStem(string child)
    {
        var m = Regex.Match(child, @"^(HID\\[^\\]+?)(&COL[0-9A-F]{2})?\\(.+)&[0-9A-F]{4}$", RegexOptions.IgnoreCase);
        return m.Success ? $@"{m.Groups[1].Value.ToUpperInvariant()}\{m.Groups[3].Value}" : child.ToUpperInvariant();
    }

    /// <summary>The 0xCA invariant, with the naming rule read from the
    /// device tree. Every present HIDMaestro parent's HID children carry the
    /// rule's path, and the parent under <paramref name="liveEnumerator"/>
    /// has at least one. Then no two parents give their HID children one
    /// path: the tree's paths for present parents, ROOT records that are not
    /// present by the rule, SWD records only when present.</summary>
    static void NoSharedChildPath(string when, string? liveEnumerator = null)
    {
        var roots = Records("ROOT").Where(r => r.Prefix != null).ToList();
        var swds = Records("SWD").Where(r => r.Prefix != null && Native.IsStarted(Id(r))).ToList();
        var stems = new List<(string Stem, ParentRecord Parent)>();
        var wrong = new List<string>();
        int read = 0;
        bool liveHasChild = liveEnumerator == null;
        foreach (var r in roots.Concat(swds))
        {
            bool present = r.Root == "SWD" || Native.IsStarted(Id(r));
            if (!present) { stems.Add((ChildPath(r), r)); continue; }
            var children = TreeChildren(r);
            if (r.HidMaestro)
            {
                foreach (string c in children)
                {
                    read++;
                    if (Stem(c, r) == null) wrong.Add($"{Id(r)} has {c}");
                }
                if (liveEnumerator != null && r.HidMaestro && children.Count > 0
                    && string.Equals(r.Enumerator, liveEnumerator, StringComparison.OrdinalIgnoreCase))
                    liveHasChild = true;
            }
            if (children.Count == 0) stems.Add((ChildPath(r), r));
            foreach (string c in children.Select(TreeStem).Distinct(StringComparer.OrdinalIgnoreCase))
                stems.Add((c, r));
        }
        Check($"{when}, every present HIDMaestro parent's HID children, read from the device tree, follow the rule",
              wrong.Count == 0 && liveHasChild,
              wrong.Count > 0 ? string.Join("; ", wrong.Take(4)) : liveHasChild ? $"{read} children read" : $"no HID child under {liveEnumerator}");
        var shared = stems.GroupBy(x => x.Stem, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        Check($"{when}, no two parents give their HID children one path", shared.Count == 0,
              shared.Count == 0 ? Describe(HmRootParents())
                                : string.Join("; ", shared.Select(g => $"{g.Key}: {Describe(g.Select(x => x.Parent))}")));
    }
}
