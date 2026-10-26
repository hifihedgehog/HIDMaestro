// A profile edited after it loads creates with its edited values (design
// note, "Where errors appear").
//
// The load checks compile what a profile declares, its extended reports'
// programs, its report builder and its answers, to refuse a bad file at
// load. They keep none of it: a program can edit a loaded profile through
// ControllerProfile's setters and objects before its first create, as it
// could before v2.0.0, and the first create compiles from what the profile
// then holds. HMProfile's accessors hand out copies since 2.1.0
// (Copies.cs). At 4d4199ce the load kept every compile on the profile, so
// the device harness, which sets steam-controller-2's idle interval to 0
// after the catalog loads, met the load's program with the catalog's 4 ms
// interval (NominalTicks 12000 where b22324e had 0). Each create also
// resolves the device model again, so answers set after the load are the
// ones a create serves.
using System;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Edits
{
    public static int Run()
    {
        var c = new Checks("edits");
        static ControllerProfile Fresh(string id)
        {
            // A profile of a load of its own, so no other check's create
            // compiled it first.
            var src = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == id);
            var text = System.Text.Json.JsonSerializer.Serialize(src, HMJsonContext.Default.ControllerProfile);
            var db = ProfileDatabase.LoadTexts(new[] { ($"user-{id}.json", text.Replace($"\"id\":\"{id}\"", $"\"id\":\"user-{id}\"")) },
                ModelRegistry.Catalog);
            return db.All.Single();
        }
        c.Run("steam-controller-2's idle interval set to 0 after the load reaches the program its first encode compiles", () =>
        {
            var p = Fresh("steam-controller-2");
            p.ExtendedReport!.IdleFrameIntervalMs = 0;
            var prog = VendorBlobProgram.Get(p.ExtendedReport);
            return (prog.NominalTicks == 0, $"NominalTicks {prog.NominalTicks}");
        });
        c.Run("an axisMap edited after the load reaches the report builder the first create builds", () =>
        {
            var p = Fresh("dualsense");
            // 0x33 is dualsense's left trigger: give the role to 0x32 alone.
            p.AxisMap!.Remove("0x33");
            p.AxisMap["0x32"] = "leftTrigger";
            var b = p.GetOrBuildReportBuilder();
            return (b.LeftTrigger?.Usage == 0x32, $"leftTrigger on usage 0x{b.LeftTrigger?.Usage:X2}");
        });
        c.Run("a descriptor replaced after the load is the one the first create parses", () =>
        {
            var p = Fresh("dualsense");
            var pedals = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "heusinkveld-ultimate-pedals");
            p.Descriptor = pedals.Descriptor;
            p.Layout = null;
            var bytes = p.GetDescriptorBytes()!;
            return (bytes.SequenceEqual(pedals.GetDescriptorBytes()!), $"{bytes.Length} bytes");
        });
        c.Run("answers removed after the load are gone from the model the create resolves", () =>
        {
            var p = Fresh("steam-controller-2");
            p.FeatureStubs = null;
            p.ValueSources = null;
            var refresh = typeof(ControllerProfile).GetMethod("ResolveForCreate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? throw new MissingMethodException("ControllerProfile", "ResolveForCreate");
            refresh.Invoke(p, null);
            return (p.Resolved.Model.FeatureStubs == null, p.Resolved.Sources["answers"]);
        });
        return c.Failures;
    }
}
