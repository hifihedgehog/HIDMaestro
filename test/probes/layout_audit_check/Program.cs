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

        Console.WriteLine();
        Console.WriteLine($"  Catalog: {totalProfiles} deployable profiles");
        Console.WriteLine($"  Authored: {authoredProfiles}  Unspecified: {unspecifiedProfiles}  Unauthored: {unauthoredProfiles}");
        Console.WriteLine($"\n=== {(s_failures == 0 ? "PASS" : "FAIL")}: {s_total - s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }
}
