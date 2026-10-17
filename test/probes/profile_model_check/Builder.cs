// HMProfileBuilder and the device model (design note, "Profiles made
// outside the catalog" and decision (d)).
//
// FromProfile copies the whole profile, its own model keys and the model it
// names included, and not what an identity default gave it, which the copy
// resolves again under its own identity. Replacing the descriptor drops
// every key the old descriptor's report format governs, so a copy of every
// catalog profile builds with any valid descriptor. ClearDeviceModel
// drops the keys tied to the descriptor's reports and the model the
// profile names, and keeps driverPid, after which the identity the builder
// sets gives the model. The builder sets DriverPid and FeatureAnswers, and
// HMProfile.BuildsItsOwnReports says whether the raw submits are refused.
// The base copied a dozen fields, dropping the answers, driverPid, the
// backend and the extended reports, and had none of these members.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Builder
{
    public static int Run()
    {
        var c = new Checks("builder");
        try { Checks_(c); }
        catch (Exception e) when (e is TypeLoadException or MissingMemberException)
        {
            c.Check("the SDK has the members these checks call", false, $"{e.GetType().Name}: {e.Message}");
        }
        return c.Failures;
    }

    static string Json(ControllerProfile p) => JsonSerializer.Serialize(p, HMJsonContext.Default.ControllerProfile);

    static string Json(FeatureStubSpec s) => JsonSerializer.Serialize(s, HMJsonContext.Default.FeatureStubSpec);

    /// <summary>HMProfileBuilder.FeatureAnswers(string), read by reflection so
    /// the probe also runs against an SDK whose setter took the internal
    /// FeatureStubSpec.</summary>
    static HMProfileBuilder Answers(this HMProfileBuilder b, string json) =>
        (HMProfileBuilder)(typeof(HMProfileBuilder).GetMethod("FeatureAnswers", new[] { typeof(string) })
            ?? throw new MissingMethodException("HMProfileBuilder", "FeatureAnswers(string)")).Invoke(b, new object?[] { json })!;

    static HMProfileBuilder Model(this HMProfileBuilder b, string? value) =>
        (HMProfileBuilder)(typeof(HMProfileBuilder).GetMethod("DeviceModel", new[] { typeof(string) })
            ?? throw new MissingMethodException("HMProfileBuilder", "DeviceModel(string)")).Invoke(b, new object?[] { value })!;

    static IReadOnlyDictionary<string, string> Sources(HMProfile p) =>
        (IReadOnlyDictionary<string, string>)Api.Prop(p, "DeviceModelSource")!;

    static void Checks_(Checks c)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        HMProfile P(string id) => ctx.GetProfile(id)!;

        foreach (var id in new[] { "steam-controller-2", "dualsense-bt", "xbox-series-xs", "dualshock-3-full", "switch2-pro-controller-composite" })
            c.Run($"a copy of {id} through FromProfile is the catalog profile, key for key", () =>
            {
                var src = P(id);
                var copy = new HMProfileBuilder().FromProfile(src).Build();
                // The id is the source's until a setter changes it.
                string a = Json(src.Inner), b = Json(copy.Inner);
                return (a == b, a == b ? "" : $"{a.Length} and {b.Length} characters");
            });
        c.Run("the copy of steam-controller-2 keeps its answers, its value source, the usbip backend and the extended report", () =>
        {
            var copy = new HMProfileBuilder().FromProfile(P("steam-controller-2")).Id("user-triton").Build().Inner;
            return (copy.FeatureStubs?.Reports?.Count > 0 && copy.ValueSources?.ContainsKey("steamUpdater") == true
                    && copy.RequiresUsbipBackend && copy.ExtendedReport != null, "");
        });
        c.Run("the copy of dualsense names sony-dualsense, as the catalog profile does", () =>
        {
            var copy = new HMProfileBuilder().FromProfile(P("dualsense")).Id("user-ds").Build();
            return (Sources(copy)["answers"] == "named:sony-dualsense", Sources(copy)["answers"]);
        });
        // Replacing the descriptor of a builder that holds one drops every key
        // the old descriptor's report format governs (HMProfileBuilder.Descriptor),
        // so a copy given any valid descriptor builds. At 629cf512 a copy kept
        // its layout, extended reports, answers and USB configuration, and
        // Build refused most catalog sources (b22324e's copy dropped them,
        // and built).
        var all = ctx.AllProfiles.ToList();
        foreach (var donor in new[] { "xbox-360-wired", "heusinkveld-ultimate-pedals" })
            c.Run($"every one of the {all.Count} catalog profiles, copied and given {donor}'s descriptor, builds", () =>
            {
                string d = P(donor).DescriptorHex!;
                var failed = new List<string>();
                foreach (var src in all)
                {
                    try { new HMProfileBuilder().FromProfile(src).Id("user-copy").DescriptorHex(d).Build(); }
                    catch (Exception e) { failed.Add($"{src.Id}: {(Api.ProfileError(e) is { } err ? $"{err.Path}: {err.Reason}" : e.Message)}"); }
                }
                return (failed.Count == 0, $"{failed.Count} refused" + string.Concat(failed.Take(6).Select(f => "\n      " + f)));
            });
        foreach (var id in new[] { "steam-controller-2", "dualsense-bt", "dualshock-3-full", "xbox-series-xs", "logitech-g29" })
            c.Run($"a copy of {id} given another descriptor drops every key the old report format governs and keeps the rest", () =>
            {
                var src = P(id);
                var q = new HMProfileBuilder().FromProfile(src).Id("user-copy").DescriptorHex(P("heusinkveld-ultimate-pedals").DescriptorHex!).Build().Inner;
                var kept = new List<string>();
                if (q.Layout != null) kept.Add("layout");
                if (q.ExtendedReport != null) kept.Add("extendedReport");
                if (q.ExtendedOutputReport != null) kept.Add("extendedOutputReport");
                if (q.AxisCenters != null) kept.Add("axisCenters");
                if (q.InputDefaults != null) kept.Add("inputDefaults");
                if (q.SdlMapping != null) kept.Add("sdlMapping");
                if (q.SixaxisCompatible == true) kept.Add("sixaxisCompatible");
                if (q.NativeDescriptor != null) kept.Add("nativeDescriptor");
                if (q.UsbConfiguration != null) kept.Add("usbConfiguration");
                if (q.RequiresUsbipBackend) kept.Add("backend");
                if (q.InputReportSize != null) kept.Add("inputReportSize");
                if (q.DataReportId != null) kept.Add("dataReportId");
                if (q.ButtonMap != null) kept.Add("buttonMap");
                if (q.TriggerButtons != null) kept.Add("triggerButtons");
                if (q.AxisMap != null) kept.Add("axisMap");
                if (q.TriggerMode != null) kept.Add("triggerMode");
                if (q.FeatureStubs != null) kept.Add("featureStubs");
                if (q.ValueSources != null) kept.Add("valueSources");
                var s0 = src.Inner;
                bool rest = q.Name == s0.Name && q.Vid == s0.Vid && q.Pid == s0.Pid && q.ProductString == s0.ProductString
                            && q.DriverMode == s0.DriverMode && q.DriverPid == s0.DriverPid && q.VersionNumber == s0.VersionNumber
                            && q.DeviceModelFrom == s0.DeviceModelFrom && q.Connection == s0.Connection;
                return (kept.Count == 0 && rest, kept.Count == 0 ? (rest ? "" : "an identity key changed") : "kept " + string.Join(", ", kept));
            });
        c.Run("a key set after the new descriptor is kept", () =>
        {
            var q = new HMProfileBuilder().FromProfile(P("dualsense")).Id("user-copy")
                .DescriptorHex(P("heusinkveld-ultimate-pedals").DescriptorHex!).InputReportSize(20).Build().Inner;
            return (q.InputReportSize == 20, $"{q.InputReportSize}");
        });
        c.Run("setting the descriptor a copy already holds drops nothing", () =>
        {
            var src = P("dualsense-bt");
            var same = new HMProfileBuilder().FromProfile(src).DescriptorHex(src.DescriptorHex!).Build();
            // The same bytes in another hex case are the same descriptor.
            var upper = new HMProfileBuilder().FromProfile(src).DescriptorHex(src.DescriptorHex!.ToUpperInvariant()).Build();
            // And in the spaced form a profile's descriptor may take, which
            // GetDescriptorBytes reads as the same bytes.
            string spacedHex = string.Join(" ", Enumerable.Range(0, src.DescriptorHex!.Length / 2).Select(i => src.DescriptorHex!.Substring(i * 2, 2)));
            var spaced = new HMProfileBuilder().FromProfile(src).DescriptorHex(spacedHex).Build();
            return (Json(same.Inner) == Json(src.Inner) && upper.Inner.Layout != null && upper.Inner.ExtendedReport != null
                    && spaced.Inner.Layout != null && spaced.Inner.ExtendedReport != null
                    && (upper.Inner.FeatureStubs != null) == (src.Inner.FeatureStubs != null), "");
        });
        c.Run("a builder's first descriptor drops nothing set before it", () =>
        {
            var q = new HMProfileBuilder().Id("user-new").Vid(0x1234).Pid(0x5678).InputReportSize(9)
                .DescriptorHex(P("heusinkveld-ultimate-pedals").DescriptorHex!).Build().Inner;
            return (q.InputReportSize == 9, $"{q.InputReportSize}");
        });
        c.Run("answers set after a new descriptor that declares no feature report are refused with the path", () =>
        {
            try
            {
                new HMProfileBuilder().FromProfile(P("steam-controller-2")).Id("user-triton-bad")
                    .DescriptorHex(P("ps-classic").DescriptorHex!).Answers(Json(P("steam-controller-2").Inner.FeatureStubs!)).Build();
                return (false, "built");
            }
            catch (Exception e) when (Api.ProfileError(e) is { } err)
            {
                return (err.Path.StartsWith("$.featureStubs", StringComparison.Ordinal), $"{err.Path}: {err.Reason}");
            }
        });
        c.Run("after ClearDeviceModel a copy of dualsense given PID 09CC takes sony-dualshock4 by identity", () =>
        {
            var copy = new HMProfileBuilder().FromProfile(P("dualsense")).Id("user-ds4").Pid(0x09CC).ClearDeviceModel().Build();
            return (Sources(copy)["answers"] == "identity:sony-dualshock4" && copy.Inner.DeviceModelFrom == null, Sources(copy)["answers"]);
        });
        c.Run("ClearDeviceModel keeps xbox-series-xs's driverPid and drops steam-controller-2's answers and value source", () =>
        {
            var x = new HMProfileBuilder().FromProfile(P("xbox-series-xs")).Id("user-x").ClearDeviceModel().Build();
            var t = new HMProfileBuilder().FromProfile(P("steam-controller-2")).Id("user-t").ClearDeviceModel().Build();
            return (x.Inner.ResolvedDriverPid == "0x02FF" && t.Inner.FeatureStubs == null && t.Inner.ValueSources == null
                    && Sources(t)["answers"] == "none", $"{x.Inner.ResolvedDriverPid} {Sources(t)["answers"]}");
        });
        c.Run("DriverPid and FeatureAnswers set the profile's own presentation and answers", () =>
        {
            var answers = P("steam-controller-composite").Inner.FeatureStubs!;
            var p = new HMProfileBuilder().Id("user-own").Vid(0x1234).Pid(0x5678)
                .DescriptorHex(P("steam-controller-composite").DescriptorHex!)
                .DriverPid("0x0B12").Answers(Json(answers)).Build();
            return (Sources(p)["answers"] == "own" && Sources(p)["presentation"] == "own" && p.Inner.ResolvedDriverPid == "0x0B12",
                    $"{Sources(p)["answers"]} {Sources(p)["presentation"]}");
        });
        c.Run("two profiles built from one builder share no object", () =>
        {
            var b = new HMProfileBuilder().FromProfile(P("steam-controller-2")).Id("user-twice");
            var one = b.Build();
            var two = b.Build();
            return (!ReferenceEquals(one.Inner.FeatureStubs, two.Inner.FeatureStubs) && !ReferenceEquals(one.Inner.ExtendedReport, two.Inner.ExtendedReport), "");
        });
        // The builder's public surface names no type of HIDMaestro.Internal,
        // takes the answers as the JSON the profile key holds, and says
        // "deviceModel": "none" as a file does. At 5fa368e3 FeatureAnswers
        // took FeatureStubSpec and no setter said "none".
        c.Run("no public HMProfileBuilder member takes or returns a type of HIDMaestro.Internal", () =>
        {
            var hits = typeof(HMProfileBuilder).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                .Where(m => m.GetParameters().Select(x => x.ParameterType).Append(m.ReturnType)
                    .Any(t => (Nullable.GetUnderlyingType(t) ?? t).Namespace == "HIDMaestro.Internal"))
                .Select(m => m.Name).ToList();
            return (hits.Count == 0, string.Join(", ", hits));
        });
        c.Run("FeatureAnswers with malformed JSON throws HMProfileException at the path inside featureStubs", () =>
        {
            try
            {
                new HMProfileBuilder().Answers("{\"reports\":[{\"id\":\"0x05\",\"sise\":41}]}");
                return (false, "accepted");
            }
            catch (System.Reflection.TargetInvocationException e) when (Api.ProfileError(e.InnerException!) is { } err)
            {
                return (err.Path == "$.featureStubs.reports[0].sise", $"{err.Path}: {err.Reason}");
            }
        });
        string sony = P("dualsense").DescriptorHex!;
        c.Run("a Sony identity built in code takes sony-dualsense, and DeviceModel(\"none\") takes nothing", () =>
        {
            HMProfileBuilder B() => new HMProfileBuilder().Id("user-sony").Vid(0x054C).Pid(0x0CE6).DescriptorHex(sony);
            var with = B().Build();
            var none = B().Model("none").Build();
            return (Sources(with)["answers"] == "identity:sony-dualsense" && Sources(none)["answers"] == "none"
                    && none.Inner.Resolved.Model.FeatureStubs == null, $"{Sources(with)["answers"]} {Sources(none)["answers"]}");
        });
        c.Run("DeviceModel with another value throws HMProfileException at $.deviceModel", () =>
        {
            try { new HMProfileBuilder().Id("user-bad-model").Vid(0x1234).Pid(0x5678).DescriptorHex(sony).Model("some").Build(); return (false, "built"); }
            catch (Exception e) when (Api.ProfileError(e is System.Reflection.TargetInvocationException t ? t.InnerException! : e) is { } err)
            {
                return (err.Path == "$.deviceModel", $"{err.Path}: {err.Reason}");
            }
        });
        c.Run("ControllerProfile.BuiltInCode keeps its public init accessor", () =>
        {
            var set = typeof(ControllerProfile).GetProperty("BuiltInCode")!.SetMethod;
            bool init = set != null && set.IsPublic
                        && set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
            return (init, set == null ? "no setter" : set.IsPublic ? "public, not init" : "not public");
        });
        foreach (var (id, want) in new[] { ("switch2-pro-controller-composite", true), ("switch2-pro-controller", false), ("dualsense-bt", false), ("steam-controller-2", false) })
            c.Run($"{id} {(want ? "builds" : "does not build")} its own reports", () =>
            {
                bool got = (bool)Api.Prop(P(id), "BuildsItsOwnReports")!;
                return (got == want, got.ToString());
            });
        // A null descriptor clears the copy's, as it did before v2.0.0, with
        // the keys it governed, and the profile builds with none, as one
        // never given a descriptor does (its create refuses it). At 9dac4573
        // the setter threw NullReferenceException.
        c.Run("DescriptorHex(null) on a copy clears its descriptor and the keys it governed", () =>
        {
            try
            {
                var p = new HMProfileBuilder().FromProfile(P("dualsense")).Id("user-no-descriptor").DescriptorHex(null!).Build();
                return (p.DescriptorHex == null && p.AxisMap == null, $"descriptor {p.DescriptorHex ?? "null"}, axisMap {(p.AxisMap == null ? "null" : "kept")}");
            }
            catch (Exception e) { return (false, $"{e.GetType().Name}: {e.Message}"); }
        });
        // A copy of a USB/IP persona given another PID builds, with the PID
        // in its USB device descriptor, and the source keeps its own. At
        // 31efc70f the copy's descriptor kept the source's PID and its
        // check refused the copy.
        foreach (var p in ctx.AllProfiles.Where(x => x.Inner.RequiresUsbipBackend && x.Inner.UsbConfiguration != null))
            c.Run($"a FromProfile copy of {p.Id} given PID 0x1234 builds, with 0x1234 in its USB device descriptor", () =>
            {
                string before = p.Inner.UsbConfiguration!.DeviceDescriptorHex!;
                try
                {
                    var copy = new HMProfileBuilder().FromProfile(p).Id(p.Id + "-pid").Pid(0x1234).Build();
                    var d = Convert.FromHexString(copy.Inner.UsbConfiguration!.DeviceDescriptorHex!);
                    bool ok = d[10] == 0x34 && d[11] == 0x12 && copy.ProductId == 0x1234
                              && p.Inner.UsbConfiguration!.DeviceDescriptorHex == before;
                    return (ok, Convert.ToHexString(d));
                }
                catch (HMProfileException e) { return (false, $"{e.JsonPath}: {e.Reason}"); }
            });
    }
}
