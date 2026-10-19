// The child path a ROOT parent takes when a record on its identity's own
// path was left without its driver (DeviceManager.ChildPrefixFor). Before
// v2.0.0 a removal another program's open handle vetoed deleted the HID
// child's driver and left its record, and every later child on that path
// stopped with problem 19. Such a parent gives its child the next prefix of
// its identity, 1&hash&1 to 1&hash&3, whose path holds no such record.
//
// Checked here, with the registry read stood in for: the choice for each
// set of broken paths, the enumerator the read is asked about, that no
// alternate of one identity is another identity's prefix or alternate over
// the key set (the 0xCA invariant, one prefix to one token), and that the
// sweep counts a child on an alternate path as HIDMaestro's once its parent
// record is gone.

using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro.Internal;

internal static class AlternatePrefixes
{
    sealed class NoRecords : DeviceOrchestrator.IOrphanSource
    {
        public bool RecordExists(string instanceId) => false;
        public bool Owned(string instanceId) => false;
        public IEnumerable<string> ParentRecords() => Array.Empty<string>();
        public string? ParentIdPrefix(string instanceId) => null;
        public IEnumerable<string> RecordedTokens() => new[] { DeviceIdentity.ForKey("alternate-check:claimed", 0).Token };
    }

    internal static void Run(Action<string, bool, string> check)
    {
        var id0 = DeviceIdentity.ForIndex(0);
        check("alternates: the nth alternate of 1&74102611&0 is 1&74102611&n",
              DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, 1) == "1&74102611&1"
              && DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, 3) == "1&74102611&3",
              $"{id0.ParentIdPrefix} -> {DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, 1)}");

        var asked = new List<string>();
        string Choose(params int[] broken)
        {
            var set = new HashSet<string>(broken.Select(n => n == 0 ? id0.ParentIdPrefix : DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, n)));
            DeviceManager.DriverlessChildHook = (e, p) => { asked.Add(e); return set.Contains(p); };
            try { return DeviceManager.ChildPrefixFor("HIDClass", id0.ParentIdPrefix); }
            finally { DeviceManager.DriverlessChildHook = null; }
        }
        check("alternates: no broken record keeps the identity's own prefix", Choose() == "1&74102611&0", Choose());
        check("alternates: a broken record on the own path gives the first alternate", Choose(0) == "1&74102611&1", Choose(0));
        check("alternates: broken records on the own path and the first alternate give the second", Choose(0, 1) == "1&74102611&2", Choose(0, 1));
        check("alternates: a broken first alternate alone keeps the own prefix", Choose(1) == "1&74102611&0", Choose(1));
        check("alternates: every path broken keeps the own prefix", Choose(0, 1, 2, 3) == "1&74102611&0", Choose(0, 1, 2, 3));
        check("alternates: the record read is asked about the parent's enumerator", asked.Count > 0 && asked.All(e => e == "HIDClass"),
              string.Join(",", asked.Distinct()));

        // One prefix to one token, alternates included.
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clash = new List<string>();
        IEnumerable<DeviceIdentity> identities = Enumerable.Range(0, 16).Select(DeviceIdentity.ForIndex)
            .Concat(Enumerable.Range(0, 20_000).Select(i => DeviceIdentity.ForKey($"alternate-check:{i}", 0)));
        foreach (var id in identities)
            for (int n = 0; n <= DeviceIdentity.PrefixAlternates; n++)
            {
                string p = n == 0 ? id.ParentIdPrefix : DeviceIdentity.AlternatePrefix(id.ParentIdPrefix, n);
                if (owner.TryGetValue(p, out string? other) && other != id.Token) clash.Add($"{p}: {other} and {id.Token}");
                else owner[p] = id.Token;
            }
        check("alternates: over 16 indexes and 20,000 keys no prefix or alternate belongs to two tokens", clash.Count == 0,
              clash.Count == 0 ? $"{owner.Count} prefixes" : string.Join(" | ", clash.Take(3)));

        var owned = DeviceOrchestrator.OwnedPrefixes(new NoRecords());
        string claimed = DeviceIdentity.ForKey("alternate-check:claimed", 0).ParentIdPrefix;
        check("alternates: the sweep owns each index's and each recorded token's alternates once the parent record is gone",
              owned.Contains(DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, 1))
              && owned.Contains(DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, DeviceIdentity.PrefixAlternates))
              && owned.Contains(DeviceIdentity.AlternatePrefix(claimed, 2)),
              $"{owned.Count} prefixes");
        check("alternates: the sweep owns no alternate past the last",
              !owned.Contains(DeviceIdentity.AlternatePrefix(id0.ParentIdPrefix, DeviceIdentity.PrefixAlternates + 1)), "");
    }
}
