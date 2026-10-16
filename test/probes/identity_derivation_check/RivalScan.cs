// The 0xCA rival scan (DeviceManager.ClearParentIdPrefixRivals) on
// recorded device trees, through DeviceManager.RivalSourceHook. No
// registry, no PnP: each tree lists instance keys with the ParentIdPrefix
// they hold, whether they are in the device tree, whether they carry a
// HIDMaestro hardware ID and who owns them, and the fake records every
// removal the scan asks for.
//
// The invariant: no two parents whose HID children would share a device
// instance path, HID\<parent's enumerator>\<ParentIdPrefix>&<collection>.
// A rival is a parent under the create's enumerator, in ROOT or SWD, on a
// prefix the create's child can arrive under. A ROOT record re-enumerates
// at every boot, so it counts present or not. A profile whose presentation
// moves (the nine Microsoft non-Xbox profiles from VID_045E&PID_xxxx&IG_00
// to HIDClass, xbox-360-wireless from PID 0719 to 02A1) leaves its old
// parent with the same token under the old enumerator, which the scan of
// every HIDMaestro ROOT enumerator removes as the same controller twice.
// An SWD create's child starts under the prefix its retained record holds,
// so the record is rewritten first, and its old value scanned when it
// cannot be.

using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class RivalScan
{
    sealed record Node(string Prefix, bool Live, bool Owned, OwnerCensus.Owner Owner);

    sealed class Tree : DeviceManager.IRivalSource
    {
        public readonly Dictionary<string, Node> Nodes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Removed = new();
        public bool RemovalFails;
        public bool WriteFails;

        public Tree Add(string id, string prefix, bool live = false, bool owned = true,
                        OwnerCensus.Owner owner = OwnerCensus.Owner.Dead)
        {
            Nodes[id] = new Node(prefix, live, owned, owner);
            return this;
        }

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
        public void Remove(string id) { Removed.Add(id); if (!RemovalFails) Nodes.Remove(id); }
        public bool RecordExists(string id) => Nodes.ContainsKey(id);
        public bool SetParentIdPrefix(string id, string prefix)
        {
            if (WriteFails || !Nodes.TryGetValue(id, out var n)) return false;
            Nodes[id] = n with { Prefix = prefix };
            return true;
        }
    }

    const string Token = "HM_8C2F0B6E41D7A953";
    const string Prefix = "1&2d595ca7&0";
    const string Other = "HM_0007";

    static (bool Clear, Exception? Error, Tree Tree) Scan(Tree tree, string enumRoot, string enumerator, bool remove = true)
    {
        DeviceManager.RivalSourceHook = tree;
        try { return (DeviceManager.ClearParentIdPrefixRivals(enumRoot, enumerator, Token, Prefix, remove), null, tree); }
        catch (Exception e) { return (false, e, tree); }
        finally { DeviceManager.RivalSourceHook = null; }
    }

    static (bool Clear, Exception? Error) Gamepad(Tree tree, string enumerator, DeviceIdentity identity)
    {
        DeviceManager.RivalSourceHook = tree;
        try { return (DeviceOrchestrator.ClearGamepadParentRivals(enumerator, identity, remove: true), null); }
        catch (Exception e) { return (false, e); }
        finally { DeviceManager.RivalSourceHook = null; }
    }

    /// <summary>The token v1.8.0 and v1.8.1 gave a consumer that handed
    /// back "index:N": HM_ and the identity hash of that key in hex
    /// (DeviceIdentity.Hash at v1.8.1, the same salt every release
    /// uses).</summary>
    internal static string V18IndexToken(int index)
    {
        byte[] d = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("HIDMaestro.DeviceIdentity.v1\n" + DeviceIdentity.DefaultKey(index)));
        return DeviceIdentity.TokenPrefix + System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(d).ToString("X16");
    }

    internal static void Run(Action<string, bool, string> check)
    {
        // The nine Microsoft non-Xbox profiles move to HIDClass: the old
        // parent of the same token is under VID_045E&PID_001B&IG_00.
        var r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", Prefix), "ROOT", "HIDClass");
        check("rival: a same-token parent under another IG_00 enumerator is removed",
              r.Clear && r.Error == null && r.Tree.Removed.SequenceEqual(new[] { $@"ROOT\VID_045E&PID_001B&IG_00\{Token}" }),
              $"clear={r.Clear} removed=[{string.Join(", ", r.Tree.Removed)}] {r.Error?.GetType().Name}");

        // xbox-360-wireless moves from PID 0719 to 02A1.
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_0719&IG_00\{Token}", Prefix), "ROOT", "VID_045E&PID_02A1&IG_00");
        check("rival: the 0719 parent is removed before the 02A1 create",
              r.Clear && r.Tree.Removed.Count == 1, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // Back the other way: a HIDClass parent left by a newer release, met
        // by a create under an IG_00 enumerator.
        r = Scan(new Tree().Add($@"ROOT\HIDClass\{Token}", Prefix), "ROOT", "VID_045E&PID_028E&IG_00");
        check("rival: a same-token HIDClass parent is removed before an IG_00 create",
              r.Clear && r.Tree.Removed.Count == 1, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // Another token on the same prefix under another enumerator: its
        // child is HID\VID_045E&PID_07B9&IG_00\<prefix>&..., not the
        // create's HID\HIDCLASS\<prefix>&..., so it is no rival.
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_07B9&IG_00\{Other}", Prefix), "ROOT", "HIDClass");
        check("rival: another token's parent on the prefix under another enumerator is left alone",
              r.Clear && r.Tree.Removed.Count == 0, $"removed=[{string.Join(", ", r.Tree.Removed)}]");
        r = Scan(new Tree().Add($@"ROOT\HIDClass\{Other}", Prefix), "ROOT", "HIDClass");
        check("rival: another token's parent on the prefix under the same enumerator is removed",
              r.Clear && r.Tree.Removed.Count == 1, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // The same token under another enumerator holding another prefix is
        // still the same controller twice.
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", "1&99999999&0"), "ROOT", "HIDClass");
        check("rival: a same-token parent with another prefix is removed",
              r.Clear && r.Tree.Removed.Count == 1, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // A rival HIDMaestro does not own is never touched, and the create
        // is refused. Under another enumerator it is no rival.
        r = Scan(new Tree().Add(@"ROOT\HIDClass\0000", Prefix, live: true, owned: false), "ROOT", "HIDClass");
        check("rival: a foreign parent on the prefix under the same enumerator refuses the create untouched",
              !r.Clear && r.Error == null && r.Tree.Removed.Count == 0, $"clear={r.Clear} removed={r.Tree.Removed.Count}");
        r = Scan(new Tree().Add(@"ROOT\HIDClass\0000", Prefix, live: true, owned: false), "ROOT", "VID_045E&PID_028E&IG_00");
        check("rival: a foreign parent on the prefix under another enumerator is left alone",
              r.Clear && r.Error == null && r.Tree.Removed.Count == 0, $"clear={r.Clear} removed={r.Tree.Removed.Count}");
        // The child path takes the enumerator segment, not the root, so a
        // parent under SWD\HIDClass on the prefix would give the same child.
        r = Scan(new Tree().Add(@"SWD\HIDClass\0000", Prefix, live: true, owned: false), "ROOT", "HIDClass");
        check("rival: a live parent on the prefix under the same segment in the other root refuses the create",
              !r.Clear && r.Tree.Removed.Count == 0, $"clear={r.Clear}");

        // A live rival whose owner runs refuses the create with
        // HMResourceInUseException, under another enumerator too.
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", Prefix, live: true, owner: OwnerCensus.Owner.Live), "ROOT", "HIDClass");
        check("rival: a running program's parent under another enumerator throws HMResourceInUseException",
              r.Error is HMResourceInUseException && r.Tree.Removed.Count == 0, r.Error?.GetType().Name ?? "no exception");
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", Prefix, live: true, owner: OwnerCensus.Owner.LiveLegacy), "ROOT", "HIDClass");
        check("rival: a running v1.12.0 program's parent throws HMResourceInUseException",
              r.Error is HMResourceInUseException e1 && e1.Message.Contains("1.12.0"), r.Error?.Message ?? "no exception");

        // A live rival whose owner is gone is removed.
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", Prefix, live: true, owner: OwnerCensus.Owner.DeadLegacy), "ROOT", "HIDClass");
        check("rival: a dead program's live parent under another enumerator is removed",
              r.Clear && r.Tree.Removed.Count == 1, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // The registration gate's check under the lock removes nothing.
        r = Scan(new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", Prefix), "ROOT", "HIDClass", remove: false);
        check("rival: the check without removal reports the rival and removes nothing",
              !r.Clear && r.Tree.Removed.Count == 0, $"clear={r.Clear}");

        // A rival that survives its removal refuses the create.
        var stuck = new Tree().Add($@"ROOT\VID_045E&PID_001B&IG_00\{Token}", Prefix);
        stuck.RemovalFails = true;
        r = Scan(stuck, "ROOT", "HIDClass");
        check("rival: a parent that survives removal refuses the create", !r.Clear && r.Tree.Removed.Count == 1, "");

        // The create's own prior life under its own enumerator is the
        // registration gate's, and enumerators no plan creates parents
        // under are not scanned.
        r = Scan(new Tree()
                .Add($@"ROOT\HIDClass\{Token}", Prefix)
                .Add(@"ROOT\USB\0000", Prefix)
                .Add(@"ROOT\SYSTEM\0001", Prefix)
                .Add($@"ROOT\VID_045E&PID_028E&XI_00\{Other}", Prefix), "ROOT", "HIDClass");
        check("rival: the create's own prior life and enumerators outside the plans are left alone",
              r.Clear && r.Tree.Removed.Count == 0, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // Unrelated prefixes under the scanned enumerators are left alone.
        r = Scan(new Tree()
                .Add(@"ROOT\HIDClass\HM_0001", "1&11111111&0")
                .Add(@"ROOT\VID_054C&PID_0CE6&IG_00\HM_0002", "1&22222222&0"), "ROOT", "HIDClass");
        check("rival: parents on other prefixes are left alone", r.Clear && r.Tree.Removed.Count == 0, "");

        // SWD records enumerate only while their creator holds them, so the
        // SWD scan counts live rivals only, and a parent under another SWD
        // enumerator gives another child path.
        r = Scan(new Tree()
                .Add($@"SWD\HIDMAESTRO_VID_045E_PID_02FF&IG_00\{Other}", Prefix, live: false)
                .Add($@"SWD\HIDMAESTRO_VID_045E_PID_0B12&IG_00\{Other}", Prefix, live: true), "SWD", "HIDMAESTRO_VID_045E_PID_02FF&IG_00");
        check("rival: an SWD scan leaves absent records and other SWD enumerators alone",
              r.Clear && r.Tree.Removed.Count == 0, $"removed=[{string.Join(", ", r.Tree.Removed)}]");

        // Review 2, finding 1. v1.8.0 and v1.8.1 named the default identity
        // of index 0 HM_<hash("index:0")> too, with HM_0000's prefix. A
        // v1.8.x program holds that parent live, an earlier life left
        // HM_0000's record, and a create of index 0's default identity takes
        // the record over: its child would arrive as the live child's path.
        // The gamepad companion's own rival step must refuse it.
        var index0 = DeviceIdentity.ForIndex(0);
        string v18Token = V18IndexToken(0);
        check("rival: v1.8.x's keyed token of index 0 derives index 0's prefix",
              DeviceIdentity.ParentIdPrefixOfToken(v18Token) == index0.ParentIdPrefix, $"{v18Token} {index0.ParentIdPrefix}");
        const string Gp = "HIDMAESTRO_VID_045E_PID_0B13&IG_00";
        var v18 = new Tree()
            .Add($@"SWD\{Gp}\{v18Token}", index0.ParentIdPrefix, live: true, owner: OwnerCensus.Owner.Unknown)
            .Add($@"SWD\{Gp}\{index0.Token}", index0.ParentIdPrefix, live: false);
        var g = Gamepad(v18, Gp, index0);
        check("rival: a gamepad create beside a live v1.8.x parent on its prefix is refused",
              g.Error is HMResourceInUseException && v18.Removed.Count == 0, g.Error?.GetType().Name ?? $"clear={g.Clear}");

        // The retained record holds another value, and a live parent holds
        // that one. Rewritten first, the record's child arrives under the
        // identity's prefix and the way is clear. When the write fails, the
        // old value is scanned too and the create is refused.
        const string Kept = "1&5e6f7a8b&3";
        var kept = new Tree()
            .Add($@"SWD\{Gp}\{index0.Token}", Kept, live: false)
            .Add($@"SWD\{Gp}\HM_0009", Kept, live: true, owner: OwnerCensus.Owner.Live);
        g = Gamepad(kept, Gp, index0);
        check("rival: a gamepad create rewrites its retained record before the scan",
              g.Clear && g.Error == null && kept.Nodes[$@"SWD\{Gp}\{index0.Token}"].Prefix == index0.ParentIdPrefix,
              $"clear={g.Clear} {g.Error?.GetType().Name} prefix={kept.Nodes[$@"SWD\{Gp}\{index0.Token}"].Prefix}");
        kept.Nodes[$@"SWD\{Gp}\{index0.Token}"] = kept.Nodes[$@"SWD\{Gp}\{index0.Token}"] with { Prefix = Kept };
        kept.WriteFails = true;
        g = Gamepad(kept, Gp, index0);
        check("rival: a retained record it cannot rewrite has its old prefix scanned, and a live parent on it refuses the create",
              g.Error is HMResourceInUseException, g.Error?.GetType().Name ?? $"clear={g.Clear}");

        // The enumerators a plan creates ROOT parents under.
        foreach (var (e, want) in new[]
        {
            ("HIDClass", true), ("HIDCLASS", true), ("VID_045E&PID_028E&IG_00", true), ("vid_0e6f&pid_0213&ig_00", true),
            ("VID_045E&PID_028E&XI_00", false), ("VID_045E&PID_028E&IG_01", false), ("VID_045E&PID_28E&IG_00", false),
            ("VID_045G&PID_028E&IG_00", false), ("HID_IG_00", false), ("HIDMAESTRO", false), ("USB", false), ("SYSTEM", false),
        })
            check($"rival: IsRootParentEnumerator({e}) is {want}", PresentationPlan.IsRootParentEnumerator(e) == want, "");
    }
}
