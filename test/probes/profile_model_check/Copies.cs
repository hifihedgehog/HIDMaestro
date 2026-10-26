// HMProfile's ButtonMap, AxisMap, ExtendedReport, ExtendedOutputReport and
// Layout hand out copies, as XInput does. Every controller of a profile
// encodes with the profile's own objects, so a program that writes into
// what one of these reads returned must change no later read, no report a
// controller of the profile builds and no program it compiles. Each check
// takes a profile of a load of its own, so a write that reaches the profile
// spoils no other check.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Copies
{
    public static int Run()
    {
        var c = new Checks("copies");

        c.Run("ButtonMap: each read is an array of its own, and a write into one changes no later read and no report", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            byte[] before = Report(inner);
            int[] first = p.ButtonMap!, second = p.ButtonMap!;
            int[] was = (int[])second.Clone();
            first[0] = 13;
            first[1] = 13;
            byte[] after = Report(inner);
            bool own = !ReferenceEquals(first, second);
            bool reads = p.ButtonMap!.SequenceEqual(was) && inner.ButtonMap!.SequenceEqual(was);
            bool report = after.SequenceEqual(before);
            return (own && reads && report, $"own array {own}, later reads unchanged {reads}, report unchanged {report}");
        });

        c.Run("AxisMap: each read is a dictionary of its own with the profile's comparer, and a write into one changes no later read and no report", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            var control = Fresh("dualsense-bt");
            var first = p.AxisMap!;
            var second = p.AxisMap!;
            var was = second.ToDictionary(kv => kv.Key, kv => kv.Value);
            first.Clear();
            first["0x30"] = "leftTrigger";
            // The builder reads the map when it is first built, after the write.
            byte[] built = Report(inner), expected = Report(control);
            bool own = !ReferenceEquals(first, second) && Equals(second.Comparer, inner.AxisMap!.Comparer);
            bool reads = Same(p.AxisMap!, was) && Same(inner.AxisMap!, was);
            bool report = built.SequenceEqual(expected);
            return (own && reads && report, $"own dictionary {own}, later reads unchanged {reads}, report unchanged {report}");
        });

        foreach (bool output in new[] { false, true })
        {
            string name = output ? "ExtendedOutputReport" : "ExtendedReport";
            c.Run($"{name}: each read is a spec of its own, equal to the profile's as JSON, and a write into one changes no later read", () =>
            {
                var inner = Fresh("dualsense-bt");
                var p = new HMProfile(inner);
                ExtendedReportSpec Read() => (output ? p.ExtendedOutputReport : p.ExtendedReport)!;
                ExtendedReportSpec Own() => (output ? inner.ExtendedOutputReport : inner.ExtendedReport)!;
                string json = Json(Own());
                var first = Read();
                var second = Read();
                bool faithful = Json(second) == json;
                first.Size = 3;
                first.Fields.Clear();
                first.ArmOn?.Clear();
                first.IdleFrameIntervalMs = 77;
                bool own = !ReferenceEquals(first, second);
                bool reads = Json(Read()) == json && Json(Own()) == json && Json(second) == json;
                return (own && faithful && reads, $"own spec {own}, equal to the profile's {faithful}, later reads unchanged {reads}");
            });
        }

        c.Run("ExtendedReport: a write into a read changes no program a controller of the profile compiles", () =>
        {
            var inner = Fresh("steam-controller-2");
            var p = new HMProfile(inner);
            var spec = p.ExtendedReport!;
            spec.IdleFrameIntervalMs = 0;
            spec.Fields.Clear();
            var prog = VendorBlobProgram.Get(inner.ExtendedReport!);
            return (prog.NominalTicks != 0 && prog.Fields.Length > 0, $"NominalTicks {prog.NominalTicks}, {prog.Fields.Length} fields");
        });

        c.Run("Layout: each read is a layout of its own, equal to the profile's as JSON, and a write into one of its lists changes no later read", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            string json = Json(inner.Layout!);
            var first = (HMGamepadLayout)p.Layout!;
            var second = (HMGamepadLayout)p.Layout!;
            bool faithful = Json(second) == json;
            bool wrote = false;
            if (first.Sticks is IList<HMStick> sticks && !sticks.IsReadOnly && sticks.Count > 0)
            {
                sticks[0] = sticks[0] with { XAxis = HMAxis.Dial };
                wrote = true;
            }
            bool own = !ReferenceEquals(first, second) && !ReferenceEquals(first.Sticks, second.Sticks);
            bool reads = Json(p.Layout!) == json && Json(inner.Layout!) == json && Json(second) == json;
            return (own && faithful && reads, $"own layout {own}, equal to the profile's {faithful}, a list written {wrote}, later reads unchanged {reads}");
        });

        return c.Failures;
    }

    /// <summary>A profile of a load of its own, under a user id, as Edits
    /// takes one.</summary>
    static ControllerProfile Fresh(string id)
    {
        var src = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == id);
        var text = JsonSerializer.Serialize(src, HMJsonContext.Default.ControllerProfile);
        var db = ProfileDatabase.LoadTexts(new[] { ($"user-{id}.json", text.Replace($"\"id\":\"{id}\"", $"\"id\":\"user-{id}\"")) },
            ModelRegistry.Catalog);
        return db.All.Single();
    }

    /// <summary>The descriptor report of one state that presses A and B and
    /// moves the right stick and both triggers, as the profile's own report
    /// builder encodes it.</summary>
    static byte[] Report(ControllerProfile p)
    {
        var axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = 0.2f, [HMAxis.Y] = 0.3f, [HMAxis.Z] = 0.7f, [HMAxis.Rz] = 0.9f, [HMAxis.Rx] = 0.4f, [HMAxis.Ry] = 0.6f,
        };
        return p.GetOrBuildReportBuilder().BuildReport(axes, 0, (uint)(HMButton.A | HMButton.B));
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static string Json(ExtendedReportSpec s) => JsonSerializer.Serialize(s, HMJsonContext.Default.ExtendedReportSpec);

    static string Json(HMLayout l) => JsonSerializer.Serialize(l, HMJsonContext.Default.HMLayout);
}
