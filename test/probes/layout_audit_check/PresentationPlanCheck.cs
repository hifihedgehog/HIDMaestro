// The presentation plan of every catalog profile and of the builder
// cases below, against the golden record presentation-plan.txt, which
// holds the decisions of master 3dd5e2a lifted into PresentationPlan with
// nothing changed. A field may differ from its golden line only when
// presentation-plan-changes.txt lists that profile, field and new value,
// with the lane that made the change and its reason, and every listed
// change must happen. The golden file is never written again to absorb a
// change: an intended change goes in the change list.
//
//   LayoutAuditCheck --record-plan <file>   write the record, for the
//                                           commit that lifts the decisions

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class PresentationPlanCheck
{
    /// <summary>The profiles the record covers, by id: every catalog
    /// profile, and the profiles a program builds in code that the plan
    /// must decide the same way.</summary>
    internal static List<(string Id, HMProfile Profile)> Cases(HMContext ctx)
    {
        var list = ctx.AllProfiles.Select(p => (p.Id, p)).ToList();

        // A Microsoft-VID custom profile with "xbox" in its name, the shape
        // output_perf_bench builds, which gets the XUSB companion.
        var wheel = new HidDescriptorBuilder()
            .Joystick()
            .AddStick("Left", bits: 16)
            .AddTrigger("Left", bits: 8)
            .AddTrigger("Right", bits: 8)
            .AddButtons(10)
            .AddPidFfbBlock();
        list.Add(("builder/045e-xbox", new HMProfileBuilder()
            .Id("xbox-dual-gate-wheel").Name("Xbox Dual Gate Wheel (bench)").Vendor("Bench")
            .Vid(0x045E).Pid(0xB719).ProductString("Xbox Dual Gate Wheel")
            .Type("wheel").Connection("usb").FromDescriptorBuilder(wheel).Build()));

        // The same without "xbox": a Microsoft-VID device that is no
        // XInput device.
        list.Add(("builder/045e-plain", new HMProfileBuilder()
            .Id("bench-wheel").Name("Bench Wheel").Vendor("Bench")
            .Vid(0x045E).Pid(0xB71A).ProductString("Bench Wheel")
            .Type("wheel").Connection("usb").FromDescriptorBuilder(wheel).Build()));

        // The battery's custom profile (HIDMaestroTest make-custom-profile,
        // PadForge's HMaestroProfileCatalog.BuildCustomProfile), BEEF:F000.
        var pad = new HidDescriptorBuilder()
            .Gamepad()
            .AddStick("Left", 16)
            .AddStick("Right", 16)
            .AddTrigger("Left", 16)
            .AddTrigger("Right", 16)
            .AddHat()
            .AddButtons(11);
        list.Add(("builder/beef-f000", new HMProfileBuilder()
            .Id("padforge-custom").Name("PadForge Custom (regression)").Vendor("Custom")
            .Vid(0xBEEF).Pid(0xF000).ProductString("PadForge Game Controller")
            .ManufacturerString("PadForge").Type("gamepad").Connection("usb")
            .FromDescriptorBuilder(pad).Build()));

        // A builder copy of each xinputhid catalog profile.
        foreach (var p in ctx.AllProfiles
                     .Where(p => string.Equals(p.DriverMode, "xinputhid", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Id, StringComparer.Ordinal).ToList())
            list.Add(("builder/copy-" + p.Id, new HMProfileBuilder().FromProfile(p).Build()));

        return list.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    internal static string Record(HMContext ctx) =>
        string.Concat(Cases(ctx).Select(c => PresentationPlan.Of(c.Profile.Inner).Describe(c.Id)));

    static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            d[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        return d;
    }

    /// <summary>Compares the record with the golden file and the change
    /// list, prints every difference, and returns the number of
    /// failures.</summary>
    internal static int Compare(HMContext ctx, string goldenPath, string changesPath, Action<string, bool, string> check)
    {
        var golden = Parse(File.ReadAllText(goldenPath));
        var now = Parse(Record(ctx));
        // "<id> <field>=<value> | <lane> | <reason>", or "<id> <field>=<absent>"
        // for a line the change removes.
        var listed = new Dictionary<string, (string Value, string Why)>(StringComparer.Ordinal);
        if (File.Exists(changesPath))
            foreach (var raw in File.ReadAllLines(changesPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string[] parts = line.Split(" | ");
                if (parts.Length != 3) throw new FormatException($"{changesPath}: '{line}' is not '<id> <field>=<value> | <lane> | <reason>'.");
                int eq = parts[0].IndexOf('=');
                listed.Add(parts[0].Substring(0, eq), (parts[0].Substring(eq + 1), parts[1] + ": " + parts[2]));
            }

        int fails = 0, changed = 0;
        foreach (string key in golden.Keys.Union(now.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            string was = golden.TryGetValue(key, out var g) ? g : "<absent>";
            string isNow = now.TryGetValue(key, out var n) ? n : "<absent>";
            bool hasEntry = listed.TryGetValue(key, out var entry);
            if (was == isNow)
            {
                if (hasEntry) { check($"plan {key}: the listed change to '{entry.Value}' did not happen", false, ""); fails++; }
                continue;
            }
            bool ok = hasEntry && entry.Value == isNow;
            if (ok) changed++;
            else fails++;
            check($"plan {key}: '{was}' -> '{isNow}'", ok, ok ? entry.Why : (hasEntry ? $"listed as '{entry.Value}'" : "not in the change list"));
        }
        Console.WriteLine($"  plan record: {now.Count} lines, {changed} listed change(s), {fails} failure(s)");
        return fails;
    }
}
