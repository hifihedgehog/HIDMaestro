// The HID orphan phase of RemoveEverything removes a child only when it is
// HIDMaestro's (DeviceOrchestrator.OrphanChildIsOurs): its parent's record
// is in the registry and carries a HIDMaestro hardware ID, or its parent
// cannot be found and its prefix is one a HIDMaestro parent holds or
// derives. Through v1.12.0 a parent that could not be found was taken as
// proof on its own. The phase now walks HID\VID_xxxx&PID_yyyy&IG_00 for
// every vendor, the segment a real Sony, Nintendo or 8BitDo pad's XInput
// HID child can carry, so that rule would remove another driver's child
// whenever PnP lost track of its parent. Checked over a recorded tree with
// no device: a foreign IG_00 child with an unresolvable parent, one whose
// parent resolves to HTREE\ROOT\0, one whose parent record is gone and one
// whose parent is present and foreign are all left alone, and HIDMaestro's
// children under each of those conditions are still removed. Last, the
// order: RemoveEverything takes the prefixes before its first phase
// removes the records they come from.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro.Internal;

internal static class OrphanOwnership
{
    sealed class Tree : DeviceOrchestrator.IOrphanSource
    {
        // instance id -> (owned, ParentIdPrefix)
        internal readonly Dictionary<string, (bool Owned, string? Prefix)> Records = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Tokens = new();
        public bool RecordExists(string instanceId) => Records.ContainsKey(instanceId);
        public bool Owned(string instanceId) => Records.TryGetValue(instanceId, out var r) && r.Owned;
        public IEnumerable<string> ParentRecords() => Records.Keys.Where(k =>
            k.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || k.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase));
        public string? ParentIdPrefix(string instanceId) => Records.TryGetValue(instanceId, out var r) ? r.Prefix : null;
        public IEnumerable<string> RecordedTokens() => Tokens;
    }

    internal static void Run(Action<string, bool, string> check)
    {
        var present = DeviceIdentity.ForKey("orphan-check:present", 0);
        var presentSwd = DeviceIdentity.ForKey("orphan-check:present-swd", 0);
        var claimed = DeviceIdentity.ForKey("orphan-check:claimed", 0);
        var gone = DeviceIdentity.ForKey("orphan-check:gone", 0);
        var tree = new Tree();
        string ourRoot = $@"ROOT\VID_045E&PID_0B13&IG_00\{present.Token}";
        string ourSwd = $@"SWD\HIDMAESTRO_VID_054C_PID_0CE6&IG_00\{presentSwd.Token}";
        string ourMinted = @"ROOT\HIDClass\HM_0042";
        string foreignRoot = @"ROOT\VID_2DC8&PID_3106&IG_00\0000";
        string foreignUsb = @"USB\VID_054C&PID_0CE6&MI_03\8&1a2b3c4d&0&0003";
        tree.Records[ourRoot] = (true, present.ParentIdPrefix);
        tree.Records[ourSwd] = (true, presentSwd.ParentIdPrefix);
        tree.Records[ourMinted] = (true, "1&2d595ca7&1499");
        tree.Records[foreignRoot] = (false, "1&5e6f7a8b&0");
        tree.Records[foreignUsb] = (false, "9&2c3d4e5f&0");
        tree.Tokens.Add(claimed.Token);
        var owned = DeviceOrchestrator.OwnedPrefixes(tree);

        bool Ours(string child, string? parent) => DeviceOrchestrator.OrphanChildIsOurs(child, parent, tree, owned);

        // Foreign children, left alone.
        check("orphans: a foreign IG_00 child whose parent cannot be resolved is left alone",
              !Ours("9&2c3d4e5f&0&0000", null), "");
        check("orphans: a foreign IG_00 child whose parent resolves to HTREE\\ROOT\\0 is left alone",
              !Ours("9&2c3d4e5f&0&0000", @"HTREE\ROOT\0"), "");
        check("orphans: a foreign IG_00 child whose parent record is gone is left alone",
              !Ours("8&11223344&0&0000", @"USB\VID_057E&PID_2009\000000000001"), "");
        check("orphans: a foreign child whose parent is present and foreign is left alone",
              !Ours("9&2c3d4e5f&0&0000", foreignUsb), "");
        check("orphans: a foreign root device's child is left alone, parent present or not",
              !Ours("1&5e6f7a8b&0&0000", foreignRoot) && !Ours("1&5e6f7a8b&0&0000", null), "");
        check("orphans: a prefix that only begins like one of ours is not ours",
              !Ours($"1{present.ParentIdPrefix}&0000", null) && !Ours($"{present.ParentIdPrefix}0&0000", null), "");
        check("orphans: a child name with no collection number is not ours",
              !Ours(present.ParentIdPrefix, null), "");

        // HIDMaestro's children, removed.
        check("orphans: a child whose parent is present and HIDMaestro's is ours",
              Ours($"{present.ParentIdPrefix}&0000", ourRoot) && Ours($"{presentSwd.ParentIdPrefix}&0000", ourSwd), "");
        check("orphans: a child of a HIDMaestro parent is ours whatever its name",
              Ours("7&deadbeef&0&0000", ourRoot), "");
        check("orphans: a child whose parent cannot be resolved is ours by a present parent's prefix",
              Ours($"{present.ParentIdPrefix}&0000", null) && Ours($"{presentSwd.ParentIdPrefix}&0001", null), "");
        check("orphans: a child is ours by the prefix a HIDMaestro record holds, one Windows minted included",
              Ours("1&2d595ca7&1499&0000", @"HTREE\ROOT\0"), "");
        check("orphans: a child whose parent record is gone is ours by an owner record's token",
              Ours($"{claimed.ParentIdPrefix}&0000", @"ROOT\HIDCLASS\" + claimed.Token)
              && Ours($"{claimed.ParentIdPrefix}&0001", null), "");
        check("orphans: a child of a removed index-default parent is ours by that index's prefix",
              Ours($"{DeviceIdentity.ForIndex(3).ParentIdPrefix}&0000", @"HTREE\ROOT\0")
              && Ours($"{DeviceIdentity.ForIndex(ControllerLease.IndexLimit - 1).ParentIdPrefix}&0000", null), "");
        check("orphans: a keyed child with no record and no owner record left is left alone",
              !Ours($"{gone.ParentIdPrefix}&0000", null), "");

        // The prefixes themselves.
        check("orphans: a foreign record's prefix is never one HIDMaestro owns",
              !owned.Contains("1&5e6f7a8b&0") && !owned.Contains("9&2c3d4e5f&0"), "");
        check("orphans: owned prefixes hold each owned record's token's prefix",
              owned.Contains(DeviceIdentity.ParentIdPrefixOfToken(present.Token)!)
              && DeviceIdentity.ParentIdPrefixOfToken(present.Token) == present.ParentIdPrefix
              && DeviceIdentity.ParentIdPrefixOfToken("HM_0003") == DeviceIdentity.ForIndex(3).ParentIdPrefix
              && DeviceIdentity.ParentIdPrefixOfToken("0003") == null && DeviceIdentity.ParentIdPrefixOfToken("HM_XYZ") == null, "");

        // The sweep's order (review 2, finding 2). RemoveEverything's first
        // phase removes every HIDMaestro ROOT and SWD record, and its orphan
        // phase comes after. A keyed controller, as PadForge creates with
        // its own keys, has no owner record once its program is gone, so
        // its phantom child is known only by the prefix its parent record
        // held. The prefixes are taken before the records go.
        var pad1 = DeviceIdentity.ForKey("padforge:slot0:xbox-elite-v2", 0);
        var pad2 = DeviceIdentity.ForKey("padforge:slot1:dualsense", 0);
        var sweep = new Tree();
        sweep.Records[$@"SWD\HIDMAESTRO_VID_045E_PID_0B00&IG_00\{pad1.Token}"] = (true, pad1.ParentIdPrefix);
        sweep.Records[$@"ROOT\HIDClass\{pad2.Token}"] = (true, pad2.ParentIdPrefix);
        var before = DeviceOrchestrator.OwnedPrefixes(sweep);
        sweep.Records.Clear();
        var after = DeviceOrchestrator.OwnedPrefixes(sweep);
        bool Phantom(DeviceIdentity d, HashSet<string> p) =>
            DeviceOrchestrator.OrphanChildIsOurs($"{d.ParentIdPrefix}&0000", @"HTREE\ROOT\0", sweep, p);
        check("orphans: keyed phantom children are removed with the prefixes taken before the records go",
              Phantom(pad1, before) && Phantom(pad2, before), "");
        check("orphans: taken after the records go, the same prefixes would leave them",
              !Phantom(pad1, after) && !Phantom(pad2, after), "");
        string? source = SourceOf(Path.Combine("sdk", "HIDMaestro.Core", "Internal", "DeviceOrchestrator.cs"));
        if (source == null)
        {
            check("orphans: DeviceOrchestrator.cs is beside the probe", false, "");
            return;
        }
        int body = source.IndexOf("private static void RemoveEverythingGated(", StringComparison.Ordinal);
        int end = body < 0 ? -1 : source.IndexOf("Phase(\"hid-orphans\")", body, StringComparison.Ordinal);
        string text = body < 0 || end < 0 ? "" : source[body..end];
        int take = text.IndexOf("OwnedPrefixes(", StringComparison.Ordinal);
        int first = text.IndexOf("SignalStopEventsAndDrain(", StringComparison.Ordinal);
        int walk = text.IndexOf("foreach (var enumRoot in new[] { \"ROOT\", \"SWD\" })", StringComparison.Ordinal);
        check("orphans: RemoveEverything takes the owned prefixes once, before its first phase",
              take >= 0 && first > take && walk > take && text.IndexOf("OwnedPrefixes(", take + 1, StringComparison.Ordinal) < 0,
              $"taken at {take}, first phase at {first}, ROOT and SWD walk at {walk}");
    }

    static string? SourceOf(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, relative);
            if (File.Exists(c)) return File.ReadAllText(c);
        }
        return null;
    }
}
