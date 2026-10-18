// The invariant behind the 0xCA guard: no two parents whose HID children
// would share a device instance path. hidclass names a HID child
// HID\<segment>\<ParentIdPrefix>&<collection>, the segment being the
// parent's enumerator, the second field of its device ID: ROOT\HIDClass\HM_0000
// has the child HID\HIDCLASS\1&hash&0&0000, ROOT\VID_045E&PID_028E&IG_00\...
// has HID\VID_045E&PID_028E&IG_00\..., and
// SWD\HIDMAESTRO_VID_045E_PID_0B00&IG_00\... has
// HID\HIDMAESTRO_VID_045E_PID_0B00&IG_00\... (the design note, mechanism 6,
// has the sources). Two children share a path only when their parents
// share the enumerator and the prefix.
//
// Checked over every parent a v1 program (v1.8.0 to v1.12.0) and v2.0.0
// create for every catalog profile and key form. v1's presentations are
// the plan record of master 3dd5e2a (layout_audit_check's
// presentation-plan.txt), whose enumerator rule is v1.12.0's, and
// AddedAfterRecord for a profile the catalog gained later. v2.0.0's are
// PresentationPlan.Of. The key forms are the default identity of an
// index, the canonical "index:N" key, which v1.8.0 and v1.8.1 also named
// HM_<hash> with the same prefix, and consumer keys. v1 derives the same
// token and prefix as v2.0.0 for every key: checked against the released
// v1.11.0 and v1.12.0 HIDMaestro.Core.dll in coexist_check's legacy_cache
// when it is there. Every pair of distinct parents whose children share a
// path must be refused by the rival scan the create runs, with the other
// parent present and its owner running, and removed or harmless when its
// owner is gone. Every other pair gives two paths.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class ChildPaths
{
    sealed record Parent(string Root, string Enumerator, string Token, string Prefix, bool V2Creates)
    {
        public string Id => $@"{Root}\{Enumerator}\{Token}";
        public string ChildPath => $@"HID\{Enumerator.ToUpperInvariant()}\{Prefix}";
    }

    sealed record Node(string Prefix, bool Live, bool Owned, OwnerCensus.Owner Owner);

    sealed class Tree : DeviceManager.IRivalSource
    {
        public readonly Dictionary<string, Node> Nodes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Removed = new();
        public string[] Enumerators(string enumRoot) => Nodes.Keys
            .Where(k => k.StartsWith(enumRoot + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split('\\')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        public string[] Instances(string enumRoot, string enumerator) => Nodes.Keys
            .Where(k => k.StartsWith($@"{enumRoot}\{enumerator}\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split('\\')[2]).ToArray();
        public string? ParentIdPrefix(string id) => Nodes.TryGetValue(id, out var n) ? n.Prefix : null;
        public bool Live(string id) => Nodes.TryGetValue(id, out var n) && n.Live;
        public bool Owned(string id) => Nodes.TryGetValue(id, out var n) && n.Owned;
        public OwnerCensus.Owner Classify(string id) => Nodes[id].Owner;
        public void Remove(string id) { Removed.Add(id); Nodes.Remove(id); }
        public bool RecordExists(string id) => Nodes.ContainsKey(id);
        public bool SetParentIdPrefix(string id, string prefix)
        {
            if (!Nodes.TryGetValue(id, out var n)) return false;
            Nodes[id] = n with { Prefix = prefix };
            return true;
        }
    }

    // Catalog profiles added after the record's commit, which no v1 release
    // shipped, each with the parent v1.12.0 makes for it if a v1 program
    // loads its file: DeviceNodeCreator.CreateDeviceNode at b4d29d9 puts a
    // profile with no upper filter and a vendor other than Microsoft's
    // under ROOT\HIDClass.
    static readonly Dictionary<string, (string Path, string Enumerator)> AddedAfterRecord = new(StringComparer.Ordinal)
    {
        ["switch-pro-usb"] = ("Root", "HIDClass"),
    };

    internal static void Run(Action<string, bool, string> check)
    {
        string? planFile = Find(Path.Combine("layout_audit_check", "presentation-plan.txt"));
        if (planFile == null)
        {
            check("paths: layout_audit_check's presentation-plan.txt is beside this probe", false, "");
            return;
        }
        var v1Slots = new Dictionary<string, (string Path, string Enumerator)>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(planFile))
        {
            int sp = line.IndexOf(' ');
            if (sp < 0) continue;
            string id = line[..sp], rest = line[(sp + 1)..];
            v1Slots.TryGetValue(id, out var slot);
            if (rest.StartsWith("path=", StringComparison.Ordinal)) v1Slots[id] = (rest[5..], slot.Enumerator);
            else if (rest.StartsWith("enumerator=", StringComparison.Ordinal)) v1Slots[id] = (slot.Path, rest[11..]);
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var slots = new HashSet<(string Version, string Root, string Enumerator)>();
        int profiles = 0, v1Missing = 0;
        foreach (var p in ctx.AllProfiles)
        {
            profiles++;
            var plan = PresentationPlan.Of(p.Inner);
            if (Slot(plan.Path.ToString(), plan.Enumerator) is { } v2) slots.Add(("v2", v2.Root, v2.Enumerator));
            if (!v1Slots.TryGetValue(p.Id, out var old) && !AddedAfterRecord.TryGetValue(p.Id, out old)) { v1Missing++; continue; }
            if (Slot(old.Path, old.Enumerator) is { } v1) slots.Add(("v1", v1.Root, v1.Enumerator));
        }
        check($"paths: the plan record, or the list of profiles added after it, holds v1's presentation of every one of {profiles} catalog profiles",
              v1Missing == 0, $"{v1Missing} without one");
        var listed = AddedAfterRecord.Keys.Where(id => v1Slots.ContainsKey(id) || ctx.AllProfiles.All(p => p.Id != id)).ToList();
        check("paths: every profile listed as added after the record is a catalog profile the record lacks",
              listed.Count == 0, string.Join(", ", listed));

        // The segments of ROOT and SWD parents never coincide, so a child
        // path is shared only within one enumerator of one root.
        var rootSegments = slots.Where(s => s.Root == "ROOT").Select(s => s.Enumerator.ToUpperInvariant()).ToHashSet();
        var swdSegments = slots.Where(s => s.Root == "SWD").Select(s => s.Enumerator.ToUpperInvariant()).ToHashSet();
        check($"paths: the {rootSegments.Count} ROOT and {swdSegments.Count} SWD child segments are disjoint",
              !rootSegments.Overlaps(swdSegments), string.Join(", ", rootSegments.Intersect(swdSegments)));

        // The key forms, each a (version, token, prefix) a program creates.
        var keys = new List<(string Version, string Token, string Prefix)>();
        foreach (int n in new[] { 0, 1, 7, 63, ControllerLease.IndexLimit - 1 })
        {
            var d = DeviceIdentity.ForIndex(n);
            keys.Add(("any", d.Token, d.ParentIdPrefix));
            var canonical = DeviceIdentity.ForKey(DeviceIdentity.DefaultKey(n), 0);
            check($"paths: the key index:{n} is index {n}'s default identity", canonical.Token == d.Token && canonical.ParentIdPrefix == d.ParentIdPrefix, canonical.Token);
            keys.Add(("v1.8", RivalScan.V18IndexToken(n), d.ParentIdPrefix));
        }
        foreach (string k in new[] { "padforge:slot0:xbox-360-wired", "padforge:slot3:dualsense-bt", "chaseplane:0", "a" })
        {
            var d = DeviceIdentity.ForKey(k, 0);
            keys.Add(("any", d.Token, d.ParentIdPrefix));
        }
        LegacyDerivation(check, keys.Where(k => k.Version == "any").Select(k => k.Token).ToList());

        // A parent v2.0.0 creates runs v2.0.0's rival scan. One only a v1
        // program creates runs v1's, and v2.0.0 meets it only as the parent
        // already there.
        var v2Slots = slots.Where(s => s.Version == "v2").Select(s => (s.Root, s.Enumerator.ToUpperInvariant())).ToHashSet();
        var parents = new List<Parent>();
        foreach (var s in slots)
            foreach (var k in keys)
            {
                if (k.Version == "v1.8" && s.Version != "v1") continue;
                bool v2 = k.Version != "v1.8" && v2Slots.Contains((s.Root, s.Enumerator.ToUpperInvariant()));
                int at = parents.FindIndex(q => string.Equals(q.Id, $@"{s.Root}\{s.Enumerator}\{k.Token}", StringComparison.OrdinalIgnoreCase));
                if (at < 0) parents.Add(new Parent(s.Root, s.Enumerator, k.Token, k.Prefix, v2));
                else if (v2) parents[at] = parents[at] with { V2Creates = true };
            }
        var groups = parents.GroupBy(p => p.ChildPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        check($"paths: of {parents.Count} distinct parents, {groups.Count} child paths are reached by more than one",
              groups.Count > 0, "the v1.8.x token of an index beside that index's default identity");
        check("paths: two parents share a child path only under one enumerator of one root",
              groups.All(g => g.Select(p => (p.Root, p.Enumerator.ToUpperInvariant())).Distinct().Count() == 1), "");

        int refused = 0, cleared = 0, pairs = 0;
        var failures = new List<string>();
        foreach (var g in groups)
            foreach (var a in g)
                foreach (var b in g)
                {
                    if (ReferenceEquals(a, b) || !b.V2Creates) continue;
                    pairs++;
                    // a is present and its owner runs: b's create is refused.
                    var tree = new Tree();
                    tree.Nodes[a.Id] = new Node(a.Prefix, true, true, OwnerCensus.Owner.Live);
                    var (clear, error) = Scan(tree, b);
                    if (error is HMResourceInUseException && tree.Removed.Count == 0) refused++;
                    else failures.Add($"{b.Id} beside live {a.Id}: clear={clear} {error?.GetType().Name}");
                    // a is left by a program that is gone: a ROOT record is
                    // removed, an SWD record that is not live enumerates no
                    // child.
                    tree = new Tree();
                    tree.Nodes[a.Id] = new Node(a.Prefix, false, true, OwnerCensus.Owner.Dead);
                    (clear, error) = Scan(tree, b);
                    bool ok = a.Root == "ROOT" ? clear && tree.Removed.SequenceEqual(new[] { a.Id }) : clear && tree.Removed.Count == 0;
                    if (ok && error == null) cleared++;
                    else failures.Add($"{b.Id} beside dead {a.Id}: clear={clear} removed=[{string.Join(", ", tree.Removed)}] {error?.GetType().Name}");
                }
        check($"paths: every one of {pairs} pairs on one child path is refused while the other parent's owner runs",
              refused == pairs, string.Join("; ", failures.Take(3)));
        check($"paths: and the other parent is removed, or enumerates nothing, once its owner is gone",
              cleared == pairs, string.Join("; ", failures.Take(3)));

        // Every pair on two child paths needs no refusal: a create beside
        // a live parent of another enumerator, or of another prefix, is
        // clear, the same controller under another ROOT enumerator aside.
        int quiet = 0, other = 0;
        var noisy = new List<string>();
        foreach (var a in parents)
            foreach (var b in parents)
            {
                if (ReferenceEquals(a, b) || !b.V2Creates
                    || string.Equals(a.ChildPath, b.ChildPath, StringComparison.OrdinalIgnoreCase)) continue;
                bool sameController = a.Root == "ROOT" && b.Root == "ROOT" && string.Equals(a.Token, b.Token, StringComparison.OrdinalIgnoreCase);
                if (sameController) continue;
                other++;
                var tree = new Tree();
                tree.Nodes[a.Id] = new Node(a.Prefix, true, true, OwnerCensus.Owner.Live);
                var (clear, error) = Scan(tree, b);
                if (clear && error == null && tree.Removed.Count == 0) quiet++;
                else if (noisy.Count < 3) noisy.Add($"{b.Id} beside {a.Id}: clear={clear} {error?.GetType().Name}");
            }
        check($"paths: every one of {other} pairs on two child paths, other controllers, creates beside the other",
              quiet == other, string.Join("; ", noisy));
    }

    static (string Root, string Enumerator)? Slot(string path, string? enumerator) => path switch
    {
        "Root" when enumerator is { Length: > 0 } and not "-" => ("ROOT", enumerator),
        "XinputhidGamepad" when enumerator is { Length: > 0 } and not "-" => ("SWD", enumerator),
        _ => null,
    };

    static (bool Clear, Exception? Error) Scan(Tree tree, Parent b)
    {
        DeviceManager.RivalSourceHook = tree;
        try
        {
            bool clear = b.Root == "SWD"
                ? DeviceOrchestrator.ClearGamepadParentRivals(b.Enumerator, Identity(b), remove: true)
                : DeviceManager.ClearParentIdPrefixRivals("ROOT", b.Enumerator, b.Token, b.Prefix);
            return (clear, null);
        }
        catch (Exception e) { return (false, e); }
        finally { DeviceManager.RivalSourceHook = null; }
    }

    /// <summary>The v2.0.0 identity with <paramref name="b"/>'s
    /// token.</summary>
    static DeviceIdentity Identity(Parent b)
    {
        if (b.Token.Length == 7 && int.TryParse(b.Token.AsSpan(3), out int n)) return DeviceIdentity.ForIndex(n);
        foreach (string k in new[] { "padforge:slot0:xbox-360-wired", "padforge:slot3:dualsense-bt", "chaseplane:0", "a" })
            if (DeviceIdentity.ForKey(k, 0) is var d && d.Token == b.Token) return d;
        throw new InvalidOperationException($"no v2.0.0 identity has the token {b.Token}");
    }

    static void LegacyDerivation(Action<string, bool, string> check, List<string> tokens)
    {
        var keys = new List<(string Key, int Index)>
        {
            ("padforge:slot0:xbox-360-wired", 0), ("padforge:slot3:dualsense-bt", 0), ("chaseplane:0", 0), ("a", 0),
        };
        string? cache = Find(Path.Combine("coexist_check", "legacy_cache"));
        foreach (string version in new[] { "v1.11.0", "v1.12.0" })
        {
            string? dll = cache == null ? null : Path.Combine(cache, version, "HIDMaestro.Core.dll");
            if (dll == null || !File.Exists(dll))
            {
                Console.WriteLine($"  [INFO] paths: {version}'s HIDMaestro.Core.dll is not in coexist_check\\legacy_cache, its derivation not compared");
                continue;
            }
            var alc = new AssemblyLoadContext("legacy-" + version, isCollectible: true);
            try
            {
                var type = alc.LoadFromAssemblyPath(dll).GetType("HIDMaestro.Internal.DeviceIdentity", throwOnError: true)!;
                var forKey = type.GetMethod("ForKey", BindingFlags.Public | BindingFlags.Static)!;
                var forIndex = type.GetMethod("ForIndex", BindingFlags.Public | BindingFlags.Static)!;
                var prefix = type.GetProperty("ParentIdPrefix")!;
                var token = type.GetProperty("Token")!;
                bool same = true;
                var ids = Enumerable.Range(0, 64).Select(i => (forIndex.Invoke(null, new object[] { i })!, DeviceIdentity.ForIndex(i)))
                    .Concat(keys.Select(k => (forKey.Invoke(null, new object[] { k.Key, k.Index })!, DeviceIdentity.ForKey(k.Key, k.Index))))
                    .Concat(Enumerable.Range(0, 5000).Select(i => (forKey.Invoke(null, new object[] { $"padforge:slot{i % 100}:profile-{i / 100}", 0 })!,
                                                                  DeviceIdentity.ForKey($"padforge:slot{i % 100}:profile-{i / 100}", 0))));
                int count = 0;
                foreach (var (old, nu) in ids)
                {
                    count++;
                    same &= (string)token.GetValue(old)! == nu.Token && (string)prefix.GetValue(old)! == nu.ParentIdPrefix;
                }
                check($"paths: {version} derives the token and prefix v2.0.0 derives for {count} keys and indexes", same, "");
            }
            finally { alc.Unload(); }
        }
    }

    static string? Find(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (string c in new[] { Path.Combine(dir.FullName, relative), Path.Combine(dir.FullName, "test", "probes", relative) })
                if (File.Exists(c) || Directory.Exists(c)) return c;
        }
        return null;
    }
}
