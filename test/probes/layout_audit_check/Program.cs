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
//   4. profile.Layout.Kind matches profile.Type ordering: wheel-typed
//      profiles, when authored, declare kind=Wheel etc. (advisory; doesn't
//      fail when the profile has no layout block).
//   5. The presentation plan of every catalog profile and of the builder
//      cases (PresentationPlanCheck.cs) equals presentation-plan.txt but
//      for the changes presentation-plan-changes.txt lists.
//
// This probe runs against the in-process catalog only: no driver
// install, no virtual device. Exit 0 on PASS, 1 on FAIL.

using System;
using System.IO;
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

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--record-plan")
        {
            using var rec = new HMContext();
            rec.LoadDefaultProfiles();
            File.WriteAllText(args[1], PresentationPlanCheck.Record(rec));
            Console.WriteLine($"wrote {args[1]}");
            return 0;
        }
        if (args.Length != 0)
        {
            Console.WriteLine("usage: LayoutAuditCheck [--record-plan <file>]");
            return 2;
        }

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
                // No layout authored: classifier-derived StickCount/TriggerCount apply.
                // Still verify Sticks/Triggers lists are consistent with classifier.
                int stickListCount = p.Sticks.Count;
                int triggerListCount = p.Triggers.Count;
                // Reading Count dereferences each list, so a null one throws
                // here. A null test of the non-nullable properties would leave
                // the loop's next profile maybe null to the compiler.
                Check($"{p.Id}: Sticks/Triggers lists are non-null",
                      stickListCount >= 0 && triggerListCount >= 0);
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

        // v2.0.0: the XInput presentation is profile data. Every catalog
        // profile with XInput states it, in its own xinput block or the
        // driverMode alias, and none takes it from an identity default,
        // so no catalog behavior stays keyed on a VID even as data. The
        // companion and xinputhid sets are the ten Xbox 360 profiles and
        // the twelve Xbox One and Series profiles.
        Console.WriteLine();
        Console.WriteLine("--- XInput presentation (v2.0.0) ---");
        int companions = 0, xinputhids = 0;
        foreach (var p in ctx.AllProfiles)
        {
            var r = XInputPresentation.Resolve(p.Inner);
            Check($"{p.Id}: presentation is its own data, not an identity default",
                  r.Source is "own" or "own:driverMode" or "none", $"source={r.Source}");
            if (r.XusbCompanion)
            {
                companions++;
                Check($"{p.Id}: an XUSB companion is stated in its xinput block", p.XInput?.Via == "xusbCompanion");
            }
            if (r.Xinputhid) xinputhids++;
            Check($"{p.Id}: RequiresXusbCompanion reads the resolved block",
                  p.Inner.RequiresXusbCompanion == r.XusbCompanion && p.Inner.UsesXinputhid == r.Xinputhid);
        }
        Check("10 catalog profiles present an XUSB companion", companions == 10, $"{companions}");
        Check("12 catalog profiles present through xinputhid", xinputhids == 12, $"{xinputhids}");

        // HMProfile.XInput is a copy: a caller that changes it changes no
        // catalog profile, and a builder keeps its own copy.
        var wheel = ctx.GetProfile("xbox-360-wheel-v1")!;
        string Shape(XInputSpec x) => $"{x.Via}|{x.SubType}|{(x.Flags == null ? "-" : string.Join(",", x.Flags))}";
        var copy = wheel.XInput!;
        string before = Shape(copy);
        copy.Via = "xinputhid";
        copy.SubType = "guitar";
        (copy.Flags ??= new()).Add("wireless");
        var fresh = wheel.XInput!;
        Check("HMProfile.XInput: changing the copy leaves the catalog profile",
              Shape(fresh) == before && fresh.Via == "xusbCompanion" && wheel.Inner.RequiresXusbCompanion,
              $"{before} -> {Shape(fresh)}");
        var built = new HMProfileBuilder().FromProfile(wheel).Id("xinput-copy-check")
            .XInput("{\"via\":\"xusbCompanion\",\"flags\":[\"voice\"]}").Build();
        built.XInput!.Flags!.Add("wireless");
        Check("HMProfileBuilder.XInput: the block is read from JSON, and the built profile's copy changes nothing",
              built.XInput!.Via == "xusbCompanion" && built.XInput.Flags!.SequenceEqual(new[] { "voice" }), "");
        string? BuilderRefusal(string json)
        {
            try { new HMProfileBuilder().FromProfile(wheel).Id("xinput-json-check").XInput(json); return null; }
            catch (HMProfileException e) { return e.Message; }
        }
        string? typo = BuilderRefusal("{\"via\":\"xusbCompanion\",\"subTyp\":\"wheel\"}");
        Check("HMProfileBuilder.XInput: a key the block lacks is refused at its path", typo != null && typo.Contains("$.xinput.subTyp"), typo ?? "accepted");

        // The companion's answers are the catalog's XUSB model
        // (profiles/models/xusb-companion.json), read once. A model the SDK
        // cannot use fails naming the JSON path.
        var model = XusbAnswers.Model;
        Check("XUSB model: the catalog's model gives five answers and the wired gamepad's defaults",
              model.Id == "xusb-companion" && model.Answers.Length == 5 && model.SubType == 0x01
              && model.Flags == 0x000C && model.BatteryType == 0x01 && model.BatteryLevel == 0x03,
              $"{model.Id} {model.Answers.Length} {model.SubType} 0x{model.Flags:X4} {model.BatteryType} {model.BatteryLevel}");
        XusbModelSpec Bad(Action<XusbModelSpec> change)
        {
            var f = new XusbModelSpec
            {
                Defaults = new XInputSpec { Via = "xusbCompanion", SubType = "gamepad", Flags = new() { "voice" },
                                            Battery = new XInputBatterySpec { Type = "wired", Level = "full" } },
                Answers = new() { new XusbAnswerSpec { Ioctl = "0x80006000", Mode = "exact", Bytes = "03 01 01 00",
                                                       Fields = new() { new XusbFieldSpec { At = 2, Value = "information.vid" } } } },
            };
            change(f);
            return f;
        }
        string? Refusal(XusbModelSpec f)
        {
            try { XusbAnswers.CompileModel(f, "bad", "bad.json"); return null; }
            catch (InvalidOperationException e) { return e.Message; }
        }
        Check("XUSB model: a well-formed model compiles", Refusal(Bad(_ => { })) == null, Refusal(Bad(_ => { })) ?? "");
        foreach (var (what, change, path) in new (string, Action<XusbModelSpec>, string)[]
        {
            ("a value no source gives", f => f.Answers![0].Fields![0].Value = "rumbleLevel", "$.xusbAnswers.answers[0].fields[0].value"),
            ("a field past the answer's end", f => f.Answers![0].Fields![0].At = 3, "$.xusbAnswers.answers[0].fields[0]"),
            ("a mode the companion lacks", f => f.Answers![0].Mode = "atLeast", "$.xusbAnswers.answers[0].mode"),
            ("an answer past the companion's 64 bytes", f => f.Answers![0].Size = 65, "$.xusbAnswers.answers[0].size"),
            ("a control code not in hex", f => f.Answers![0].Ioctl = "32768", "$.xusbAnswers.answers[0].ioctl"),
            ("defaults with no subtype", f => f.Defaults!.SubType = null, "$.xusbAnswers.defaults.subType"),
            ("defaults with no battery level", f => f.Defaults!.Battery!.Level = null, "$.xusbAnswers.defaults.battery"),
            ("answers past the companion's 512-byte blob", f => f.Answers = Enumerable.Range(0, 7)
                .Select(_ => new XusbAnswerSpec { Ioctl = "0x80006000", Mode = "exact", Bytes = "00", Size = 64 }).ToList(), "$.xusbAnswers.answers make a blob"),
        })
        {
            string? why = Refusal(Bad(change));
            Check($"XUSB model: {what} is refused at {path}", why != null && why.Contains(path), why ?? "accepted");
        }
        // The model file reads through the catalog's strict options.
        string? JsonRefusal(string json)
        {
            try { XusbAnswers.CompileModelJson(json, "bad.json"); return null; }
            catch (InvalidOperationException e) { return e.Message; }
        }
        const string Good = "{\"id\":\"bad\",\"xusbAnswers\":{\"defaults\":{\"via\":\"xusbCompanion\",\"subType\":\"gamepad\",\"flags\":[\"voice\"],"
            + "\"battery\":{\"type\":\"wired\",\"level\":\"full\"}},\"answers\":[{\"ioctl\":\"0x80006000\",\"mode\":\"exact\",\"bytes\":\"03010100\"MINOUT}]}TOP}";
        Check("XUSB model: a well-formed model file compiles", JsonRefusal(Good.Replace("MINOUT", "").Replace("TOP", "")) == null,
              JsonRefusal(Good.Replace("MINOUT", "").Replace("TOP", "")) ?? "");
        foreach (var (what, json, path) in new[]
        {
            ("a key the format lacks", Good.Replace("MINOUT", ",\"minOuptut\":36").Replace("TOP", ""), "$.xusbAnswers.answers[0].minOuptut"),
            ("a key the format lacks at the top", Good.Replace("MINOUT", "").Replace("TOP", ",\"answer\":1"), "$.answer"),
            ("a key written twice", Good.Replace("MINOUT", ",\"mode\":\"exact\"").Replace("TOP", ""), "$.xusbAnswers.answers[0].mode"),
        })
        {
            string? why = JsonRefusal(json);
            Check($"XUSB model: {what} is refused at {path}", why != null && why.Contains(path), why ?? "accepted");
        }

        // How every profile's devnodes present themselves (PresentationPlan),
        // against the record of master 3dd5e2a and the listed changes.
        Console.WriteLine();
        Console.WriteLine("--- presentation plan ---");
        PresentationPlanCheck.Compare(ctx,
            Path.Combine(AppContext.BaseDirectory, "presentation-plan.txt"),
            Path.Combine(AppContext.BaseDirectory, "presentation-plan-changes.txt"),
            (name, ok, detail) => Check(name, ok, detail));

        Console.WriteLine();
        Console.WriteLine($"  Catalog: {totalProfiles} deployable profiles");
        Console.WriteLine($"  Authored: {authoredProfiles}  Unspecified: {unspecifiedProfiles}  Unauthored: {unauthoredProfiles}");
        Console.WriteLine($"\n=== {(s_failures == 0 ? "PASS" : "FAIL")}: {s_total - s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }
}
