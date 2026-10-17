// HMProfileBuilder and the device model (design note, "Profiles made
// outside the catalog" and decision (d)).
//
// FromProfile copies the whole profile, its own model keys and the model it
// names included, and not what an identity default gave it, which the copy
// resolves again under its own identity. A copy whose descriptor no longer
// fits its model is refused at Build with the JSON path. ClearDeviceModel
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
        c.Run("a copy of steam-controller-2 given ps-classic's descriptor, which declares no feature report, is refused with the path", () =>
        {
            try
            {
                new HMProfileBuilder().FromProfile(P("steam-controller-2")).Id("user-triton-bad")
                    .DescriptorHex(P("ps-classic").DescriptorHex!).Build();
                return (false, "built");
            }
            catch (Exception e) when (Api.ProfileError(e) is { } err)
            {
                return (err.Path.StartsWith("$.", StringComparison.Ordinal), $"{err.Path}: {err.Reason}");
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
                .DriverPid("0x0B12").FeatureAnswers(answers).Build();
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
        foreach (var (id, want) in new[] { ("switch2-pro-controller-composite", true), ("switch2-pro-controller", false), ("dualsense-bt", false), ("steam-controller-2", false) })
            c.Run($"{id} {(want ? "builds" : "does not build")} its own reports", () =>
            {
                bool got = (bool)Api.Prop(P(id), "BuildsItsOwnReports")!;
                return (got == want, got.ToString());
            });
    }
}
