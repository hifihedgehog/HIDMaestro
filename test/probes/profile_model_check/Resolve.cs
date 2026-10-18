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
// from an identity default. The protocol group comes through a catalog
// profile: an identity default naming switch-pro, switch-pro-usb or
// dualshock-3-full gives its protocol, state block and report programs
// when they fit the profile's descriptor. The base has none of these keys: it loads
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
        Concurrent(c);
        ThroughCatalog(c);
        RepeatedEntry(c);
        Protocol(c);
        return c.Failures;
    }

    /// <summary>The protocol group by identity: a profile outside the
    /// catalog with no protocol keys of its own takes the protocol, state
    /// block and report programs of the catalog profile its identity
    /// default names, as 9bd6279's code gave every 057E:2009 profile the
    /// Switch Pro's responder and every sixaxisCompatible profile the
    /// DualShock 3's form. A model whose programs do not fit the profile's
    /// descriptor is not taken, and says so.</summary>
    static void Protocol(Checks c)
    {
        string Text(string id, Action<JsonObject>? edit = null)
        {
            var basis = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == id);
            // The SDK's own JSON leaves the model groups out.
            var o = JsonNode.Parse(JsonSerializer.Serialize(basis, HMJsonContext.Default.ControllerProfile))!.AsObject();
            o["id"] = "user-" + id;
            edit?.Invoke(o);
            return o.ToJsonString();
        }
        ControllerProfile Load(string text) => ProfileDatabase.LoadTexts(new[] { ("user.json", text) }, ModelRegistry.Catalog).All.Single();
        string Source(ControllerProfile p) => p.Resolved.Sources.TryGetValue("protocol", out var s) ? s : "absent";

        var sw = Load(Text("switch-pro"));
        c.Check("a Switch Pro outside the catalog takes switch-pro's protocol by identity",
                Source(sw) == "identity:nintendo-switch-pro" && sw.Protocol != null && sw.BuildsItsOwnReports && sw.ProgramsTaken,
                $"{Source(sw)}, protocol {(sw.Protocol != null)}");
        var usb = Load(Text("switch-pro-usb"));
        c.Check("one on USB with the USB descriptor takes switch-pro-usb's",
                Source(usb) == "identity:nintendo-switch-pro-usb" && usb.GetOrCompileModel().Layouts.Single().Size == 64,
                Source(usb));
        var mixed = Load(Text("switch-pro", o => o["connection"] = "usb"));
        c.Check("one on USB with the Bluetooth descriptor takes no protocol, and names the misfit",
                Source(mixed) == "none" && mixed.Protocol == null && mixed.Resolved.Problem?.Contains("switch-pro-usb", StringComparison.Ordinal) == true,
                $"{Source(mixed)}: {mixed.Resolved.Problem}");
        var ds3 = Load(Text("dualshock-3-full"));
        c.Check("a sixaxisCompatible profile outside the catalog takes dualshock-3-full's form",
                Source(ds3) == "identity:sony-sixaxis" && ds3.LiveFeatureReports is { Count: 1 } && ds3.Protocol != null,
                Source(ds3));
        var none = Load(Text("switch-pro", o => o["deviceModel"] = "none"));
        c.Check("deviceModel none takes no protocol", Source(none) == "none" && none.Protocol == null, Source(none));
        var own = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == "switch-pro");
        c.Check("the catalog's switch-pro carries its own", Source(own) == "own" && !own.ProgramsTaken, Source(own));
        var copy = sw.Clone();
        copy.ResolveForCreate();
        c.Check("a copy resolved again for a create takes the protocol again, not as its own",
                Source(copy) == "identity:nintendo-switch-pro" && copy.ProgramsTaken && copy.Protocol != null, Source(copy));
    }

    /// <summary>An identity default whose name another loaded table holds
    /// is refused at the key its file writes. At 7d62b08f it read
    /// $.entries[0].name whatever the file spelled.</summary>
    static void RepeatedEntry(Checks c)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hm-profile-entry-{Environment.ProcessId}");
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "models"));
        try
        {
            string table = Path.Combine(dir, "models", "identity-defaults.json");
            File.WriteAllText(table,
                "{ \"Entries\": [ { \"Name\": \"nintendo-switch-pro\", \"match\": { \"vid\": \"0x1234\" }, \"model\": \"switch-pro\" } ] }");
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.LoadProfilesFromDirectory(dir);
            var mine = Api.Issues(ctx, "ProfileLoadIssues").Where(i => string.Equals(i.File, table, StringComparison.OrdinalIgnoreCase)).ToList();
            c.Check("a repeated identity default name is refused at the key the file writes",
                    mine.Count == 1 && mine[0].Kind == "Refused" && mine[0].Path == "$.Entries[0].Name",
                    string.Join("; ", mine.Select(i => $"{i.Kind} {i.Path} {i.Reason}")));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A model taken through a catalog profile, whose own model
    /// came from a model file, is listed in that model file when it does
    /// not fit. dualsense takes its answers from models/sony-dualsense.json
    /// by identity, and a profile with a PID block on one of those answers'
    /// report IDs names dualsense. At 6cae4b02 the problem was listed in
    /// dualsense's profile file, which has no featureStubs key.</summary>
    static void ThroughCatalog(Checks c)
    {
        c.Run("a model that does not fit, taken through a catalog profile, is listed in the model file that holds the key", () =>
        {
            foreach (var basis in ProfileDatabase.LoadEmbedded().All)
            {
                if (basis.RequiresUsbipBackend || string.IsNullOrEmpty(basis.Descriptor)) continue;
                var o = JsonNode.Parse(JsonSerializer.Serialize(basis, HMJsonContext.Default.ControllerProfile))!.AsObject();
                o["id"] = "user-through-catalog";
                o["deviceModelFrom"] = "dualsense";
                o.Remove("deviceModel");
                var db = ProfileDatabase.LoadTexts(new[] { ("through.json", o.ToJsonString()) }, ModelRegistry.Catalog);
                var hit = Api.Issues(db, "Issues").FirstOrDefault(i => i.Kind == "ModelProblem" && i.Reason.Contains("PID block", StringComparison.Ordinal));
                if (hit == null) continue;
                return (hit.File.Replace('\\', '/').EndsWith("models/sony-dualsense.json", StringComparison.OrdinalIgnoreCase)
                        && hit.Path.StartsWith("$.featureStubs", StringComparison.Ordinal),
                        $"{basis.Id}: {hit.File} {hit.Path}");
            }
            return (false, "no catalog profile with a PID block on a DualSense answer's report ID");
        });
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
        // The SDK finds an embedded model by its file name and parses it
        // when a name asks for it, so each file's id is its name.
        c.Run("every model the SDK carries parses, with its file's name as its id", () =>
        {
            try
            {
                var all = ModelRegistry.Catalog.AllModels.ToList();
                var off = all.Where(m => !m.Source.EndsWith("/" + m.File.Id + ".json", StringComparison.OrdinalIgnoreCase)).Select(m => m.Source).ToList();
                return (all.Count > 0 && off.Count == 0, off.Count == 0 ? $"{all.Count} models" : string.Join("; ", off));
            }
            catch (InvalidOperationException e) { return (false, e.Message); }
        });
        // An identity default's keys match finds a profile key by the name
        // its JSON gives it, read from the type rather than the context's
        // metadata. The two give one set of names, each for one property.
        // The context also lists the ignored properties, with no getter,
        // which a keys match never read.
        c.Run("an identity default's keys name the profile's keys as its JSON context does", () =>
        {
            var context = HMJsonContext.Default.ControllerProfile.Properties.Where(x => x.Get != null)
                .ToDictionary(x => x.Name, x => x.AttributeProvider as System.Reflection.PropertyInfo, StringComparer.OrdinalIgnoreCase);
            var mine = ProfileKeys.All;
            var missing = context.Keys.Where(k => !mine.ContainsKey(k)).ToList();
            var extra = mine.Keys.Where(k => !context.ContainsKey(k)).ToList();
            var other = context.Where(kv => mine.TryGetValue(kv.Key, out var pi) && pi != kv.Value).Select(kv => kv.Key).ToList();
            return (missing.Count == 0 && extra.Count == 0 && other.Count == 0 && context.Count > 0,
                    $"{context.Count} keys, missing {string.Join(",", missing)}, extra {string.Join(",", extra)}, other property {string.Join(",", other)}");
        });
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
            File.WriteAllText(Path.Combine(dir, "models", "b-pid-again-spelled.json"),
                "{ \"ID\": \"user-pid-model\", \"driverPid\": \"0x0B01\" }");
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
                }, new JsonObject
                {
                    ["name"] = "user-misfit-id",
                    ["match"] = new JsonObject { ["vid"] = "0x1234", ["pid"] = "0x5680" },
                    ["model"] = "user-file-model",
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
                ("ident-misfit.json", Pedals("user-ident-misfit", o => { o["vid"] = "0x1234"; o["pid"] = "0x5680"; })),
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
            // A listed model problem names the value that went wrong where
            // its file holds it: the profile's deviceModelFrom for a name
            // that finds no model, and the model's own key for a model that
            // does not fit. At 08fd21f3 every model problem was listed at
            // the profile's $.deviceModelFrom, one the profile taking a
            // model by identity does not have.
            Api.Issue? ProblemIssue(string id) => issues.FirstOrDefault(i => i.Id == id && i.Kind == "ModelProblem");
            string modelFile = Path.Combine(dir, "models", "c-file-model.json");
            c.Run("a name nothing holds is listed at the profile's $.deviceModelFrom", () =>
                (ProblemIssue("user-missing") is { } i && i.Path == "$.deviceModelFrom" && i.File == Path.Combine(dir, "missing.json"),
                 ProblemIssue("user-missing")?.ToString() ?? "none"));
            c.Run("a named model that does not fit is listed at the model file's key that does not fit", () =>
                (ProblemIssue("user-misfit") is { } i && i.Path == "$.valueSources.steamUpdater" && i.File == modelFile
                 && i.Reason.Contains("user-misfit", StringComparison.Ordinal),
                 ProblemIssue("user-misfit") is { } x ? $"{x.File} {x.Path} {x.Reason}" : "none"));
            c.Run("an identity default's model that does not fit is listed at the model file's key, naming the entry and the profile", () =>
                (ProblemIssue("user-ident-misfit") is { } i && i.Path == "$.valueSources.steamUpdater" && i.File == modelFile
                 && i.Reason.Contains("user-misfit-id", StringComparison.Ordinal) && i.Reason.Contains("user-ident-misfit", StringComparison.Ordinal),
                 ProblemIssue("user-ident-misfit") is { } x ? $"{x.File} {x.Path} {x.Reason}" : "none"));
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
            // At 7d62b08f a repeated model id was listed at $.id however the
            // file spelled the key.
            c.Run("a repeated model id is refused at the key the file writes", () =>
                (issues.FirstOrDefault(i => i.File.EndsWith("b-pid-again-spelled.json", StringComparison.Ordinal)) is { } i
                 && i.Kind == "Refused" && i.Path == "$.ID",
                 issues.FirstOrDefault(i => i.File.EndsWith("b-pid-again-spelled.json", StringComparison.Ordinal))?.ToString() ?? "none"));
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
            // A directory's models stay with its profiles through a builder
            // copy and ResolveDeviceModel. At c1b7475d both resolved against
            // the catalog's registry alone: the copy lost its named model and
            // ResolveDeviceModel named one it could not find.
            c.Run("a FromProfile copy of a profile naming a directory's model keeps it", () =>
            {
                var copy = new HMProfileBuilder().FromProfile(P("user-named")!).Id("user-named-copy").Build();
                string src = Sources(copy).TryGetValue("presentation", out var v) ? v : "none";
                return (src == "named:user-pid-model" && copy.Inner.ResolvedDriverPid == "0x02FF" && (string?)Api.Prop(copy, "DeviceModelProblem") == null,
                        $"{src} {copy.Inner.ResolvedDriverPid} {(string?)Api.Prop(copy, "DeviceModelProblem")}");
            });
            c.Run("ResolveDeviceModel on a profile the directory's identity default gave a model names a model that resolves", () =>
            {
                var r = HMDeviceExtractor.ResolveDeviceModel(P("user-identity")!);
                string src = Sources(r).TryGetValue("presentation", out var v) ? v : "none";
                return (r.Inner.DeviceModelFrom == "user-pid-model" && src == "named:user-pid-model"
                        && (string?)Api.Prop(r, "DeviceModelProblem") == null,
                        $"{r.Inner.DeviceModelFrom} {src} {(string?)Api.Prop(r, "DeviceModelProblem")}");
            });
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

    /// <summary>Two directories loaded at once into one context each keep
    /// their models: a profile of a third load names both. At c1b7475d
    /// each load read the context's registry, loaded, and wrote its own
    /// back, so the later write dropped the earlier directory's models.</summary>
    static void Concurrent(Checks c)
    {
        string root = Path.Combine(Path.GetTempPath(), $"hm-profile-concurrent-{Environment.ProcessId}");
        try
        {
            string Dir(string name, string model, string pid)
            {
                string d = Path.Combine(root, name);
                System.IO.Directory.CreateDirectory(Path.Combine(d, "models"));
                File.WriteAllText(Path.Combine(d, "models", model + ".json"),
                    new JsonObject { ["id"] = model, ["driverPid"] = pid }.ToJsonString());
                File.WriteAllText(Path.Combine(d, name + ".json"), FromRepo(@"heusinkveld\ultimate-pedals.json", "user-" + name).ToJsonString());
                return d;
            }
            string a = Dir("dir-a", "user-model-a", "0x02FF"), b = Dir("dir-b", "user-model-b", "0x0B00");
            string later = Path.Combine(root, "later");
            System.IO.Directory.CreateDirectory(later);
            File.WriteAllText(Path.Combine(later, "uses-a.json"),
                FromRepo(@"heusinkveld\ultimate-pedals.json", "user-uses-a", o => o["deviceModelFrom"] = "user-model-a").ToJsonString());
            File.WriteAllText(Path.Combine(later, "uses-b.json"),
                FromRepo(@"heusinkveld\ultimate-pedals.json", "user-uses-b", o => o["deviceModelFrom"] = "user-model-b").ToJsonString());
            int lost = 0;
            const int Rounds = 40;
            for (int round = 0; round < Rounds; round++)
            {
                using var ctx = new HMContext();
                ctx.LoadDefaultProfiles();
                using var go = new System.Threading.Barrier(2);
                var ta = System.Threading.Tasks.Task.Run(() => { go.SignalAndWait(); ctx.LoadProfilesFromDirectory(a); });
                var tb = System.Threading.Tasks.Task.Run(() => { go.SignalAndWait(); ctx.LoadProfilesFromDirectory(b); });
                System.Threading.Tasks.Task.WaitAll(ta, tb);
                ctx.LoadProfilesFromDirectory(later);
                if (ctx.GetProfile("user-uses-a")?.Inner.ResolvedDriverPid != "0x02FF"
                    || ctx.GetProfile("user-uses-b")?.Inner.ResolvedDriverPid != "0x0B00") lost++;
            }
            c.Check($"two directories loaded at once keep both models, in each of {Rounds} rounds", lost == 0, $"{lost} round(s) lost one");
        }
        catch (Exception e)
        {
            c.Check("two directories loaded at once keep both models", false, $"threw {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
