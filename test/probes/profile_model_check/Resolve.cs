// Device model resolution (design note, "Profiles made outside the
// catalog").
//
// A profile's model resolves at load, group by group: its own keys, else
// the model its deviceModelFrom names, else the identity default its
// identity matches, else nothing, and "deviceModel": "none" takes nothing
// by name or identity. A name nothing holds, a named profile with no
// model, or a named model that does not fit the profile leaves the
// identity default and sets DeviceModelProblem, listed in
// ProfileLoadIssues. A directory's models folder carries named models and
// an identity-defaults.json, tried before the catalog's for profiles
// outside the catalog, with no rebuild. No catalog profile takes a group
// from an identity default. The base has none of these keys: it loads
// every profile below without a word and gives each only its own keys.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Resolve
{
    public static int Run()
    {
        var c = new Checks("resolve");
        Catalog(c);
        Directory(c);
        return c.Failures;
    }

    /// <summary>DeviceModelSource, or no groups at all on an SDK without
    /// it, so the checks below report what such an SDK does.</summary>
    static IReadOnlyDictionary<string, string> Sources(HMProfile p)
    {
        try { return (IReadOnlyDictionary<string, string>)Api.Prop(p, "DeviceModelSource")!; }
        catch (MissingMemberException) { return new Dictionary<string, string>(); }
    }

    static void Catalog(Checks c)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        c.Run("no catalog profile takes a group from an identity default", () =>
        {
            var hits = ctx.AllProfiles.Where(p => Sources(p).Values.Any(s => s.StartsWith("identity:", StringComparison.Ordinal)))
                .Select(p => $"{p.Id} ({string.Join(", ", Sources(p).Select(kv => $"{kv.Key}={kv.Value}"))})").ToList();
            return (hits.Count == 0, hits.Count == 0 ? $"{ctx.AllProfiles.Count} profiles" : string.Join("; ", hits));
        });
        c.Run("no catalog profile has a model problem", () =>
        {
            var hits = ctx.AllProfiles.Where(p => Api.Prop(p, "DeviceModelProblem") != null).Select(p => p.Id).ToList();
            return (hits.Count == 0, string.Join(", ", hits));
        });
        c.Run("every catalog profile with own feature answers or a driver PID reports them as its own", () =>
        {
            var bad = ctx.AllProfiles.Where(p =>
                (p.Inner.FeatureStubs != null && Sources(p).GetValueOrDefault("answers") != "own")
                || (p.Inner.DriverPid != null && Sources(p).GetValueOrDefault("presentation") != "own")).Select(p => p.Id).ToList();
            return (bad.Count == 0, string.Join(", ", bad));
        });
        c.Run("switch-pro and dualshock-3-full match their own identities' entries and take nothing from them", () =>
        {
            var sw = ctx.GetProfile("switch-pro")!.Inner.Resolved;
            var ds3 = ctx.GetProfile("dualshock-3-full")!.Inner.Resolved;
            return (sw.IdentityEntry == "identity:nintendo-switch-pro" && ds3.IdentityEntry == "identity:sony-sixaxis"
                    && sw.Sources.Values.All(s => s is "none" or "own") && ds3.Sources.Values.All(s => s is "none" or "own"),
                    $"{sw.IdentityEntry} {ds3.IdentityEntry}");
        });
    }

    /// <summary>A catalog profile as the repository's file holds it, as
    /// JSON to edit.</summary>
    static JsonObject FromRepo(string file, string id, Action<JsonObject>? edit = null)
    {
        string path = Path.Combine(Program.RepoRoot(), "profiles", file);
        var o = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        o["id"] = id;
        edit?.Invoke(o);
        return o;
    }

    static void Directory(Checks c)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hm-profile-resolve-{Environment.ProcessId}");
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "models"));
        try
        {
            // The models folder: a presentation model, a model that does not
            // fit a UMDF2 profile (a file-backed value source), a repeated
            // model id, and an identity table with one entry.
            File.WriteAllText(Path.Combine(dir, "models", "a-pid.json"),
                "{ \"id\": \"user-pid-model\", \"comment\": \"xinputhid's 02FF\", \"driverPid\": \"0x02FF\" }");
            File.WriteAllText(Path.Combine(dir, "models", "b-pid-again.json"),
                "{ \"id\": \"user-pid-model\", \"driverPid\": \"0x0B00\" }");
            var triton = FromRepo(@"valve\steam-controller-2.json", "unused");
            var fileModel = new JsonObject
            {
                ["id"] = "user-file-model",
                ["valueSources"] = triton["valueSources"]!.DeepClone(),
                ["featureStubs"] = triton["featureStubs"]!.DeepClone(),
            };
            File.WriteAllText(Path.Combine(dir, "models", "c-file-model.json"), fileModel.ToJsonString());
            File.WriteAllText(Path.Combine(dir, "models", "identity-defaults.json"), new JsonObject
            {
                ["comment"] = "a user's table",
                ["entries"] = new JsonArray(new JsonObject
                {
                    ["name"] = "user-1234",
                    ["match"] = new JsonObject { ["vid"] = "0x1234", ["pid"] = new JsonArray("0x5678", "0x5679") },
                    ["model"] = "user-pid-model",
                }),
            }.ToJsonString());

            JsonObject Pedals(string id, Action<JsonObject>? edit = null) => FromRepo(@"heusinkveld\ultimate-pedals.json", id, edit);
            var files = new (string Name, JsonObject Json)[]
            {
                ("named.json", Pedals("user-named", o => o["deviceModelFrom"] = "user-pid-model")),
                ("by-identity.json", Pedals("user-identity", o => { o["vid"] = "0x1234"; o["pid"] = "0x5679"; })),
                ("none.json", Pedals("user-none", o => { o["vid"] = "0x1234"; o["pid"] = "0x5678"; o["deviceModel"] = "none"; })),
                ("missing.json", Pedals("user-missing", o => { o["vid"] = "0x1234"; o["pid"] = "0x5678"; o["deviceModelFrom"] = "no-such-model"; })),
                ("no-model.json", Pedals("user-no-model", o => o["deviceModelFrom"] = "heusinkveld-ultimate-pedals")),
                ("misfit.json", Pedals("user-misfit", o => { o["vid"] = "0x1234"; o["pid"] = "0x5678"; o["deviceModelFrom"] = "user-file-model"; })),
                ("own-and-name.json", Pedals("user-own", o => { o["driverPid"] = "0x0B12"; o["deviceModelFrom"] = "user-pid-model"; })),
                ("none-and-name.json", Pedals("user-both", o => { o["deviceModel"] = "none"; o["deviceModelFrom"] = "user-pid-model"; })),
                ("bad-none.json", Pedals("user-bad-none", o => o["deviceModel"] = "some")),
            };
            foreach (var (name, json) in files) File.WriteAllText(Path.Combine(dir, name), json.ToJsonString());

            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var before = ctx.AllProfiles.ToDictionary(p => p.Id, p => string.Join(",", Sources(p).Select(kv => kv.Key + "=" + kv.Value)));
            ctx.LoadProfilesFromDirectory(dir);
            List<Api.Issue> issues;
            try { issues = Api.Issues(ctx, "ProfileLoadIssues"); }
            catch (MissingMemberException) { issues = new List<Api.Issue>(); }
            foreach (var i in issues) Console.WriteLine($"    issue: {i}");
            HMProfile? P(string id) => ctx.GetProfile(id);
            string? Pid(string id) => P(id)?.Inner.ResolvedDriverPid;
            string Src(string id, string group) => P(id) is { } p ? (Sources(p).TryGetValue(group, out var v) ? v : "no source") : "absent";
            string? Problem(string id)
            {
                if (P(id) is not { } p) return "absent";
                try { return (string?)Api.Prop(p, "DeviceModelProblem"); }
                catch (MissingMemberException) { return null; }
            }

            c.Run("a profile naming a directory's model takes it: named:user-pid-model, driver PID 02FF", () =>
                (Src("user-named", "presentation") == "named:user-pid-model" && Pid("user-named") == "0x02FF",
                 $"{Src("user-named", "presentation")} {Pid("user-named")}"));
            c.Run("a profile whose identity matches the directory's table takes its model: identity:<file>#user-1234", () =>
            {
                string want = $"identity:{Path.Combine(dir, "models", "identity-defaults.json")}#user-1234";
                return (Src("user-identity", "presentation") == want && Pid("user-identity") == "0x02FF"
                        && (bool)Api.Prop(P("user-identity")!, "HasDeviceModel")!, Src("user-identity", "presentation"));
            });
            c.Run("deviceModel none takes nothing by identity", () =>
                (P("user-none") != null && Src("user-none", "presentation") == "none" && Pid("user-none") == null
                 && !(bool)Api.Prop(P("user-none")!, "HasDeviceModel")!, Src("user-none", "presentation")));
            c.Run("a name nothing holds: loaded, the identity default taken, the problem named and listed", () =>
                (Src("user-missing", "presentation").StartsWith("identity:", StringComparison.Ordinal)
                 && Problem("user-missing") is string pr && pr.Contains("no-such-model", StringComparison.Ordinal)
                 && issues.Any(i => i.Id == "user-missing" && i.Kind == "ModelProblem"), Problem("user-missing") ?? "no problem"));
            c.Run("a named catalog profile with no model: loaded, the problem named", () =>
                (P("user-no-model") != null && Problem("user-no-model") is string pr && pr.Contains("carries no device model", StringComparison.Ordinal)
                 && issues.Any(i => i.Id == "user-no-model" && i.Kind == "ModelProblem"), Problem("user-no-model") ?? "no problem"));
            c.Run("a named model that does not fit (a file source on UMDF2): loaded, the identity default taken, the problem names the path", () =>
                (Src("user-misfit", "answers") == "none" && Src("user-misfit", "presentation").StartsWith("identity:", StringComparison.Ordinal)
                 && Problem("user-misfit") is string pr && pr.Contains("$.valueSources.steamUpdater", StringComparison.Ordinal),
                 Problem("user-misfit") ?? "no problem"));
            c.Run("own keys win over a name, and the unused name is noted", () =>
                (Src("user-own", "presentation") == "own" && Pid("user-own") == "0x0B12"
                 && P("user-own")!.Inner.Resolved.Notes.Any(n => n.Contains("unused", StringComparison.Ordinal)),
                 string.Join("; ", P("user-own")?.Inner.Resolved.Notes ?? Array.Empty<string>())));
            c.Run("deviceModel none beside a name is refused at $.deviceModel", () =>
                (P("user-both") == null && issues.Any(i => i.Id == "user-both" && i.Path == "$.deviceModel" && i.Kind == "Refused"), ""));
            c.Run("deviceModel other than none is refused at $.deviceModel", () =>
                (P("user-bad-none") == null && issues.Any(i => i.Id == "user-bad-none" && i.Path == "$.deviceModel"), ""));
            c.Run("a repeated model id is refused naming the first", () =>
                (issues.Any(i => i.File.EndsWith("b-pid-again.json", StringComparison.Ordinal) && i.Kind == "Refused"
                                 && i.Reason.Contains("a-pid.json", StringComparison.Ordinal)), ""));
            c.Run("no catalog profile's resolution changes after the directory load", () =>
            {
                var changed = ctx.AllProfiles.Where(p => before.ContainsKey(p.Id)
                        && before[p.Id] != string.Join(",", Sources(p).Select(kv => kv.Key + "=" + kv.Value)))
                    .Select(p => p.Id).ToList();
                return (changed.Count == 0, string.Join(", ", changed));
            });
            c.Run("ResolveDeviceModel returns the same instance for a profile that names or carries its model", () =>
                ReferenceEquals(HMDeviceExtractor.ResolveDeviceModel(P("user-named")!), P("user-named"))
                && ReferenceEquals(HMDeviceExtractor.ResolveDeviceModel(P("user-own")!), P("user-own"))
                && ReferenceEquals(HMDeviceExtractor.ResolveDeviceModel(P("user-none")!), P("user-none")));
            c.Run("a later load's profile names the earlier directory's model with no rebuild", () =>
            {
                string later = Path.Combine(dir, "later");
                System.IO.Directory.CreateDirectory(later);
                File.WriteAllText(Path.Combine(later, "later.json"), Pedals("user-later", o => o["deviceModelFrom"] = "user-pid-model").ToJsonString());
                ctx.LoadProfilesFromDirectory(later);
                return (Src("user-later", "presentation") == "named:user-pid-model", Src("user-later", "presentation"));
            });
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
