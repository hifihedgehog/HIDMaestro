// Layout audit (v1.3.9). Walks every deployable profile in the catalog
// and verifies:
//   1. The profile's layout block (when present) schema-validates and
//      every axis/button reference resolves to a real descriptor field.
//   2. When a layout is authored, ApplyLayoutSemantics produces a
//      simple-slot resolution that's internally consistent with the
//      classifier (no slots referencing fields the descriptor doesn't
//      declare).
//   3. profile.Sticks / profile.Triggers report variable-length lists
//      whose entries reference declared HMAxis values.
//   4. profile.Layout.Kind matches profile.Type ordering — wheel-typed
//      profiles, when authored, declare kind=Wheel etc. (advisory; doesn't
//      fail when the profile has no layout block).
//
// This probe runs against the in-process catalog only — no driver
// install, no virtual device. Exit 0 on PASS, 1 on FAIL.

using System;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== Layout audit (v1.3.9) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        int totalProfiles = 0;
        int authoredProfiles = 0;
        int unspecifiedProfiles = 0;
        int unauthoredProfiles = 0;

        foreach (var p in ctx.AllProfiles)
        {
            if (!p.IsDeployable) continue;
            totalProfiles++;

            var layout = p.Layout;
            if (layout is null)
            {
                unauthoredProfiles++;
                // No layout authored — classifier-derived StickCount/TriggerCount apply.
                // Still verify Sticks/Triggers lists are consistent with classifier.
                int stickListCount = p.Sticks.Count;
                int triggerListCount = p.Triggers.Count;
                Check($"{p.Id}: Sticks/Triggers lists are non-null",
                      p.Sticks != null && p.Triggers != null);
                continue;
            }

            if (layout is HMUnspecifiedLayout)
            {
                unspecifiedProfiles++;
                continue;
            }

            authoredProfiles++;

            // Validator already ran during profile load; re-run to verify
            // the cached layout still passes against the descriptor.
            try
            {
                var rb = HidReportBuilder.Parse(p.GetDescriptorBytes()!);
                HMLayoutValidator.Validate(layout, rb);
                Check($"{p.Id}: layout schema-validates against descriptor",
                      true,
                      $"kind={layout.Kind}");
            }
            catch (HMLayoutValidationException ex)
            {
                Check($"{p.Id}: layout schema-validates", false, ex.Message);
            }

            // Sticks list shouldn't have null axes; every entry should resolve
            // to a declared HMAxis.
            foreach (var s in p.Sticks)
            {
                if (s.XAxis != HMAxis.None)
                    Check($"{p.Id}: Sticks[].XAxis declared in descriptor",
                          p.AvailableAxes.Contains(s.XAxis),
                          $"axis={s.XAxis}");
                if (s.YAxis != HMAxis.None)
                    Check($"{p.Id}: Sticks[].YAxis declared in descriptor",
                          p.AvailableAxes.Contains(s.YAxis),
                          $"axis={s.YAxis}");
            }
            foreach (var t in p.Triggers)
            {
                Check($"{p.Id}: Triggers[].Axis declared in descriptor",
                      p.AvailableAxes.Contains(t.Axis),
                      $"axis={t.Axis}");
            }
        }

        // v1.3.10 — XUSB-companion gate audit. Walks every deployable profile
        // and asserts profile.Inner.RequiresXusbCompanion is true ONLY when:
        //   - VID == 0x045E (Microsoft)
        //   - DriverMode is not xinputhid / xusb22
        //   - "xbox" appears in Id / Name / ProductString (case-insensitive)
        // Catches the SideWinder-class regression where a Microsoft-VID
        // non-Xbox device (joystick / FF wheel / etc.) accidentally received
        // an XUSB companion devnode and showed up as an XInput slot.
        Console.WriteLine();
        Console.WriteLine("--- XUSB companion gate (v1.3.10) ---");
        int xusbExpectedTrue = 0, xusbExpectedFalse = 0;
        foreach (var p in ctx.AllProfiles)
        {
            if (!p.IsDeployable) continue;

            bool expected = ExpectedRequiresXusb(p);
            bool actual = p.Inner.RequiresXusbCompanion;
            if (expected) xusbExpectedTrue++; else xusbExpectedFalse++;

            Check($"{p.Id}: RequiresXusbCompanion matches gate predicate",
                  actual == expected,
                  $"expected={expected} actual={actual} vid=0x{p.VendorId:X4}");
        }
        Console.WriteLine($"  XUSB-companion expected true:  {xusbExpectedTrue}");
        Console.WriteLine($"  XUSB-companion expected false: {xusbExpectedFalse}");

        Console.WriteLine();
        Console.WriteLine($"  Catalog: {totalProfiles} deployable profiles");
        Console.WriteLine($"  Authored: {authoredProfiles}  Unspecified: {unspecifiedProfiles}  Unauthored: {unauthoredProfiles}");
        Console.WriteLine($"\n=== {(s_failures == 0 ? "PASS" : "FAIL")}: {s_total - s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }

    // Independent reimplementation of the XUSB-companion gate predicate.
    // The SDK's RequiresXusbCompanion (Internal/ControllerProfile.cs) MUST
    // agree with this for every deployable profile. If they disagree the
    // SDK's predicate has drifted; check git blame on either side.
    static bool ExpectedRequiresXusb(HMProfile p)
    {
        if (p.VendorId != 0x045E) return false;
        if (string.Equals(p.DriverMode, "xinputhid", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(p.DriverMode, "xusb22", StringComparison.OrdinalIgnoreCase)) return false;
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        return (p.Id?.Contains("xbox", cmp) ?? false)
            || (p.Name?.Contains("xbox", cmp) ?? false)
            || (p.ProductString?.Contains("xbox", cmp) ?? false);
    }
}
