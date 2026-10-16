// Identity derivation check (issue #60). No elevation, no device.
//
// Every id a virtual controller carries derives from its identity key, so
// this probe pins the derivation itself:
//
//   1. The default (index) key reproduces the ids callers got before the
//      key existed: HM_<index> tokens, the legacy per-index ContainerId,
//      the index as the captured-serial variant.
//   2. Derivation is deterministic (same key, same ids) and collision-free
//      across keys for the token, the container, the ParentIdPrefix and
//      the synthetic serial over a fixed 20,000-key set.
//   3. Keys are trimmed; null, empty and blank keys mean the index.
//   4. The USB serial a persona serves: a profile without a captured
//      serial (the Sony composites) gets the identity's synthetic serial
//      at a string index the descriptor did not use before, and the
//      served string descriptor carries it; a profile with a captured
//      serial (the Valve personas) keeps it byte for byte at index 0 and
//      varies its trailing digit run per identity.
//   5. The 0xCA rival scan (DeviceManager.ClearParentIdPrefixRivals) on
//      recorded device trees: a parent whose HID child would take the
//      create's child's device instance path (the create's enumerator and
//      a prefix its child can arrive under), or the same token under
//      another HIDMaestro ROOT enumerator, is found and removed, refused
//      when its owner runs, and left alone with the create refused when
//      HIDMaestro does not own it. An SWD create rewrites its retained
//      record first.
//
// Exit 0 PASS / 1 FAIL.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== Identity derivation (issue #60) ===");

        // 1. The default key is the index.
        for (int i = 0; i < 8; i++)
        {
            var d = DeviceIdentity.ForIndex(i);
            Check($"index {i}: key is index:{i}", d.Key == $"index:{i}" && d.IsDefault);
            Check($"index {i}: token is HM_{i:D4}", d.Token == $"HM_{i:D4}");
            Check($"index {i}: container is the legacy per-index GUID",
                  d.ContainerId == SwdDeviceFactory.ContainerIdFor(i), d.ContainerId.ToString("B"));
            Check($"index {i}: serial variant is the index", d.SerialVariant == i);
            Check($"index {i}: Resolve(null) is the same identity", Same(DeviceIdentity.Resolve(null, i), d));
        }
        Check("legacy container encodes HIDMAESTRO + index",
              SwdDeviceFactory.ContainerIdFor(5).ToString("B").ToUpperInvariant() == "{48494430-4D41-4553-5452-4F0000000005}");

        // 2. Deterministic and collision-free across keys.
        var keys = new List<string>();
        for (int slot = 0; slot < 100; slot++)
            for (int p = 0; p < 200; p++)
                keys.Add($"padforge:slot{slot}:profile-{p}");
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containers = new HashSet<Guid>();
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool shapesOk = true, deterministic = true, neverDefaultShape = true;
        var tokenShape = new Regex("^HM_[0-9A-F]{16}$");
        var prefixShape = new Regex("^1&[0-9a-f]{1,8}&0$");
        var serialShape = new Regex("^HM[0-9A-F]{12}$");
        foreach (var k in keys)
        {
            var a = DeviceIdentity.ForKey(k, 3);
            var b = DeviceIdentity.ForKey(k, 9);
            deterministic &= a.Token == b.Token && a.ContainerId == b.ContainerId
                          && a.ParentIdPrefix == b.ParentIdPrefix && a.SyntheticSerial == b.SyntheticSerial
                          && a.SerialVariant == b.SerialVariant && !a.IsDefault;
            shapesOk &= tokenShape.IsMatch(a.Token) && prefixShape.IsMatch(a.ParentIdPrefix)
                     && serialShape.IsMatch(a.SyntheticSerial) && a.SerialVariant >= 1 && a.SerialVariant <= 999_999;
            neverDefaultShape &= !Regex.IsMatch(a.Token, "^HM_[0-9]{4}$");
            tokens.Add(a.Token); containers.Add(a.ContainerId); prefixes.Add(a.ParentIdPrefix); serials.Add(a.SyntheticSerial);
        }
        Check($"{keys.Count} keys: derivation is deterministic and independent of the index", deterministic);
        Check("every derived value has the documented shape", shapesOk);
        Check("a consumer key can never produce a default-shaped token", neverDefaultShape);
        Check("tokens are distinct across keys", tokens.Count == keys.Count, $"{tokens.Count}/{keys.Count}");
        Check("containers are distinct across keys", containers.Count == keys.Count, $"{containers.Count}/{keys.Count}");
        Check("ParentIdPrefix values are distinct across keys", prefixes.Count == keys.Count, $"{prefixes.Count}/{keys.Count}");
        Check("synthetic serials are distinct across keys", serials.Count == keys.Count, $"{serials.Count}/{keys.Count}");
        Check("a consumer key never collides with a default identity's container",
              !containers.Contains(SwdDeviceFactory.ContainerIdFor(0)) && !containers.Contains(SwdDeviceFactory.ContainerIdFor(1)));

        // 3. Trimming and blanks.
        Check("Resolve(\"\") is the index", DeviceIdentity.Resolve("", 2).IsDefault);
        Check("Resolve(\"   \") is the index", DeviceIdentity.Resolve("   ", 2).IsDefault);
        Check("keys are trimmed", Same(DeviceIdentity.Resolve("  chaseplane:0  ", 0), DeviceIdentity.Resolve("chaseplane:0", 0)));
        Check("keys are case-sensitive", DeviceIdentity.Resolve("A", 0).Token != DeviceIdentity.Resolve("a", 0).Token);
        // HMController.IdentityKey reports "index:N" for a keyless
        // controller. Handing that back must give the identity it had, not
        // a second token over the same hash: two parents on one
        // ParentIdPrefix is bugcheck 0xCA and a machine that cannot boot.
        Check("the literal default key IS the default identity",
              Same(DeviceIdentity.Resolve("index:4", 4), DeviceIdentity.ForIndex(4)));
        Check("and it is that index's identity wherever it is used",
              Same(DeviceIdentity.Resolve("index:7", 0), DeviceIdentity.ForIndex(7)));
        Check("a near miss stays a consumer key",
              !DeviceIdentity.Resolve("index:007", 0).IsDefault && !DeviceIdentity.Resolve("Index:4", 0).IsDefault
              && !DeviceIdentity.Resolve("index:-1", 0).IsDefault);

        // The invariant behind it: a ParentIdPrefix belongs to one token.
        {
            var pool = new List<DeviceIdentity>();
            for (int i = 0; i < 16; i++) { pool.Add(DeviceIdentity.ForIndex(i)); pool.Add(DeviceIdentity.Resolve($"index:{i}", (i + 5) % 16)); }
            foreach (var k in new[] { "a", "A", "padforge:slot0", "padforge:slot1", "chaseplane:0", "index:007", "index", "index:" })
                pool.Add(DeviceIdentity.Resolve(k, 0));
            var byPrefix = pool.GroupBy(d => d.ParentIdPrefix, StringComparer.OrdinalIgnoreCase);
            var shared = byPrefix.Where(g => g.Select(d => d.Token).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                                 .Select(g => g.Key + " <- " + string.Join(",", g.Select(d => d.Token).Distinct())).ToList();
            Check("no two tokens share a ParentIdPrefix", shared.Count == 0, string.Join("; ", shared));
        }

        // 4. USB serials through the descriptor store.
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var ds = ctx.GetProfile("dualsense-composite")?.Inner;
        var deck = ctx.GetProfile("steam-deck-composite")?.Inner;
        Check("dualsense-composite and steam-deck-composite ship", ds != null && deck != null);
        if (ds != null)
        {
            Check("dualsense-composite carries no captured serial", string.IsNullOrEmpty(ds.SerialString));
            var authored = Convert.FromHexString(ds.UsbConfiguration!.DeviceDescriptorHex!);
            Check("the authored Sony device descriptor declares iSerial 0", authored[16] == 0);

            var set0 = new UsbDescriptorSet(ds, 0);
            var id0 = DeviceIdentity.ForIndex(0);
            Check("index identity: iSerial points at the first free string index (3)", set0.DeviceDescriptor[16] == 3,
                  $"iSerial={set0.DeviceDescriptor[16]}");
            Check("index identity: every other device descriptor byte is the authored one",
                  set0.DeviceDescriptor.Where((b, i) => i != 16).SequenceEqual(authored.Where((b, i) => i != 16)));
            Check("index identity: the served serial is the identity's synthetic serial",
                  ServedString(set0, set0.DeviceDescriptor[16]) == id0.SyntheticSerial, ServedString(set0, 3) ?? "(null)");
            var idK = DeviceIdentity.ForKey("padforge:slot0:dualsense-composite", 0);
            var setK = new UsbDescriptorSet(ds, 0, idK);
            Check("keyed identity: a different serial", ServedString(setK, 3) == idK.SyntheticSerial
                  && idK.SyntheticSerial != id0.SyntheticSerial, ServedString(setK, 3) ?? "(null)");
            var setK2 = new UsbDescriptorSet(ds, 5, DeviceIdentity.ForKey("padforge:slot0:dualsense-composite", 5));
            Check("keyed identity: the serial does not depend on the index", ServedString(setK2, 3) == idK.SyntheticSerial);
            Check("manufacturer and product strings still served",
                  ServedString(set0, 1) == ds.ManufacturerString && ServedString(set0, 2) == ds.ProductString);
        }
        if (deck != null)
        {
            string captured = deck.SerialString!;
            Check("steam-deck-composite carries a captured serial", !string.IsNullOrEmpty(captured), captured);
            var authored = Convert.FromHexString(deck.UsbConfiguration!.DeviceDescriptorHex!);
            var set0 = new UsbDescriptorSet(deck, 0);
            Check("captured serial: index 0 serves it byte for byte", ServedString(set0, authored[16]) == captured);
            Check("captured serial: the device descriptor is untouched", set0.DeviceDescriptor.SequenceEqual(authored));
            var set1 = new UsbDescriptorSet(deck, 1);
            Check("captured serial: index 1 adds the index into the trailing digit run",
                  ServedString(set1, authored[16]) == UsbDescriptorSet.InstanceSerial(captured, 1)
                  && ServedString(set1, authored[16]) != captured, ServedString(set1, authored[16]) ?? "(null)");
            var idK = DeviceIdentity.ForKey("padforge:slot1:steam-deck-composite", 0);
            var setK = new UsbDescriptorSet(deck, 0, idK);
            string? servedK = ServedString(setK, authored[16]);
            Check("captured serial with a key: same length, same prefix, varied digits",
                  servedK != null && servedK.Length == captured.Length && servedK.StartsWith("FX1A", StringComparison.Ordinal)
                  && servedK == UsbDescriptorSet.InstanceSerial(captured, idK.SerialVariant) && servedK != captured, servedK ?? "(null)");
        }
        Check("InstanceSerial: index 0 leaves a serial alone", UsbDescriptorSet.InstanceSerial("FX1A00000001", 0) == "FX1A00000001");
        Check("InstanceSerial: carries across a digit run", UsbDescriptorSet.InstanceSerial("AB0099", 1) == "AB0100");
        Check("InstanceSerial: stops at the first non-digit", UsbDescriptorSet.InstanceSerial("ABC", 7) == "ABC");

        // 5. The 0xCA rival scan on recorded trees (RivalScan.cs).
        Console.WriteLine("\n--- ParentIdPrefix rivals ---");
        RivalScan.Run((name, ok, detail) => Check(name, ok, detail));

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }

    static bool Same(DeviceIdentity a, DeviceIdentity b)
        => a.Key == b.Key && a.Token == b.Token && a.ContainerId == b.ContainerId
        && a.ParentIdPrefix == b.ParentIdPrefix && a.SyntheticSerial == b.SyntheticSerial
        && a.SerialVariant == b.SerialVariant && a.IsDefault == b.IsDefault;

    /// <summary>The string a GET_DESCRIPTOR(STRING, index) answers with,
    /// decoded from the UTF-16 payload behind the two-byte header.</summary>
    static string? ServedString(UsbDescriptorSet set, byte index)
    {
        var d = set.GetDescriptor(0x03, index, 0x0409);
        if (d == null || d.Length < 2) return null;
        return Encoding.Unicode.GetString(d, 2, d.Length - 2);
    }
}
