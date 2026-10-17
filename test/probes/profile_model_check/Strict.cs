// Strict profile loading (design note, "Where errors appear").
//
// The loader reads profile JSON through the SDK's source-generated context
// with unmapped members and repeated properties refused, in ordinal order
// of the files' paths. Every refused file is an entry of
// HMContext.ProfileLoadIssues with the file, the JSON path and the reason,
// and is not loaded. At 9bd6279 the first three cases below loaded without a
// word and the rest were skipped silently (ControllerProfile.cs:891-895,
// HMContext.cs:296-312), and a create threw ArgumentException for a profile
// with no descriptor and HMLayoutValidationException for a layout that did
// not fit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Strict
{
    /// <summary>A catalog profile with a plain descriptor and no device
    /// model, which the user files below start from.</summary>
    const string BaseId = "heusinkveld-ultimate-pedals";

    public static int Run()
    {
        var c = new Checks("strict");
        CatalogLoadGuard(c);
        Catalog(c);
        UserFiles(c);
        Paths(c);
        Exceptions(c);
        try { EarlierTags(c); }
        catch (Exception e) { c.Check("earlier tags' profile files go through the loader", false, $"threw {e.GetType().Name}: {e.Message}"); }
        return c.Failures;
    }

    /// <summary>A catalog profile looked up by name from inside the
    /// catalog's own load would load it again on that thread, or wait on
    /// the load's lock from one of its parallel workers, so the lookup
    /// refuses with a message (4a42386d refused nothing). A lookup from any
    /// other thread during the load waits for it and gets the profile: at
    /// e7a7a2e4 the flag was process-wide, so a Build on the caller's
    /// thread while HMContext's constructor loaded the catalog in the
    /// background, or a directory load's parallel workers, were refused.</summary>
    static void CatalogLoadGuard(Checks c)
    {
        const System.Reflection.BindingFlags Statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var flag = typeof(ProfileDatabase).GetField("s_loadingEmbedded", Statics)
            ?? throw new MissingFieldException("ProfileDatabase", "s_loadingEmbedded");
        var cached = typeof(ProfileDatabase).GetField("s_cachedEmbedded", Statics)!;
        var lookup = typeof(ProfileDatabase).GetMethod("EmbeddedProfile", Statics)!;
        // The flag as this SDK keeps it: a process-wide bool, or a value
        // that flows only into the load's own work.
        void Set(bool on)
        {
            if (flag.FieldType == typeof(bool)) flag.SetValue(null, on);
            else ((System.Threading.AsyncLocal<bool>)flag.GetValue(null)!).Value = on;
        }
        (bool Ok, string Detail) Look(bool wantRefused)
        {
            try
            {
                var p = (ControllerProfile?)lookup.Invoke(null, new object[] { "dualsense" });
                return wantRefused ? (false, "ran") : (p?.Id == "dualsense", p?.Id ?? "null");
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException io)
            {
                return (wantRefused && io.Message.Contains("catalog", StringComparison.Ordinal), io.Message);
            }
        }
        var keep = cached.GetValue(null);
        c.Run("a catalog lookup made during the catalog's own load is refused, not run", () =>
        {
            cached.SetValue(null, null);
            Set(true);
            try { return Look(wantRefused: true); }
            finally { Set(false); cached.SetValue(null, keep); }
        });
        c.Run("a catalog lookup from another thread while the catalog loads waits for the load and gets the profile", () =>
        {
            cached.SetValue(null, null);
            Set(true);
            try
            {
                (bool, string) result = (false, "did not run");
                // A thread that does not inherit this one's execution
                // context, as the caller's thread does not inherit
                // HMContext's background load.
                System.Threading.Thread t;
                using (System.Threading.ExecutionContext.SuppressFlow())
                {
                    t = new System.Threading.Thread(() => result = Look(wantRefused: false));
                    t.Start();
                }
                t.Join();
                return result;
            }
            finally { Set(false); cached.SetValue(null, keep); }
        });
    }

    // ── The catalog ──────────────────────────────────────────────────────

    static void Catalog(Checks c)
    {
        string profilesDir = Path.Combine(Program.RepoRoot(), "profiles");
        string models = Path.Combine(profilesDir, "models") + Path.DirectorySeparatorChar;
        // The profile files: every JSON file with an id, but schema.json and
        // the models. The folder also keeps descriptor collections, which
        // have none.
        var files = Directory.EnumerateFiles(profilesDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("schema.json", StringComparison.OrdinalIgnoreCase)
                        && !f.StartsWith(models, StringComparison.OrdinalIgnoreCase)
                        && JsonNode.Parse(File.ReadAllText(f), documentOptions: new JsonDocumentOptions
                               { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })?["id"] != null)
            .ToList();
        var db = ProfileDatabase.LoadEmbedded();
        c.Run("the embedded catalog loads with no issue", () =>
        {
            var issues = Api.Issues(db, "Issues");
            return (issues.Count == 0, string.Join("; ", issues.Take(8)));
        });
        c.Run($"every one of the {files.Count} profile files under profiles\\ loads from the embedded catalog", () =>
            (db.All.Count == files.Count, $"{db.All.Count} loaded"));
        c.Run("the profiles directory loads through ProfileDatabase.Load with the same ids, its descriptor collections listed as not profiles", () =>
        {
            var disk = ProfileDatabase.Load(profilesDir);
            bool same = disk.All.Select(p => p.Id).SequenceEqual(db.All.Select(p => p.Id));
            // The folder's models are the catalog's, which the embedded
            // catalog already holds, so a load of the folder refuses them as
            // repeats. Every other file is a profile or not one.
            string modelsDir = Path.Combine(profilesDir, "models");
            var issues = Api.Issues(disk, "Issues")
                .Where(i => !i.File.StartsWith(modelsDir, StringComparison.OrdinalIgnoreCase)).ToList();
            return (issues.All(i => i.Kind == "NotAProfile") && same,
                    $"{disk.All.Count} loaded, {issues.Count} issue(s): {string.Join("; ", issues.Take(8))}");
        });
        c.Run("nothing under profiles\\models is read as a profile", () =>
        {
            var modelFiles = Directory.Exists(models)
                ? Directory.EnumerateFiles(models, "*.json", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string?>();
            var hit = db.All.Where(p => modelFiles.Contains(p.Id)).Select(p => p.Id).ToList();
            return (hit.Count == 0, hit.Count == 0 ? $"{modelFiles.Count} model file(s)" : string.Join(", ", hit));
        });
        c.Run("xbox-360-wired and xbox-360-wireless carry no nativeDescriptor, and no code reads the key", () =>
        {
            var w = db.All.First(p => p.Id == "xbox-360-wired");
            var r = db.All.First(p => p.Id == "xbox-360-wireless");
            return (Api.Prop(w, "NativeDescriptor") == null && Api.Prop(r, "NativeDescriptor") == null, "");
        });
    }

    // ── A user's directory ───────────────────────────────────────────────

    /// <summary>One user file: its name, its text, and what the load must
    /// list for it (null for a file that loads).</summary>
    sealed record Case(string File, string Text, string? Id, string? Kind, string? Path, string? Reason);

    static JsonObject BaseJson()
    {
        var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == BaseId);
        return JsonNode.Parse(JsonSerializer.Serialize(p, HMJsonContext.Default.ControllerProfile))!.AsObject();
    }

    static string WithId(string id, Action<JsonObject>? edit = null)
    {
        var o = BaseJson();
        o["id"] = id;
        edit?.Invoke(o);
        return o.ToJsonString();
    }

    /// <summary>The same profile with its id's key spelled ID: the first
    /// "id" key in the text, which is the profile's own.</summary>
    static string SpelledID(string json)
    {
        int at = json.IndexOf("\"id\":", StringComparison.Ordinal);
        return json[..at] + "\"ID\":" + json[(at + 5)..];
    }

    static List<Case> Cases()
    {
        string extended = "\"extendedReport\":{\"reportId\":\"0x01\",\"size\":8,\"fields\":[]}";
        string Spliced(string id, string extra)
        {
            string json = WithId(id);
            return json.Insert(json.Length - 1, "," + extra);
        }
        return new List<Case>
        {
            new("a-misspelled.json", Spliced("user-misspelled", "\"inptuReports\":{}"), "user-misspelled",
                "Refused", "$.inptuReports", "inptuReports"),
            new("b-nested.json", Spliced("user-nested", "\"extendedReport\":{\"reportId\":\"0x01\",\"size\":8,\"fieldz\":[]}"), "user-nested",
                "Refused", "$.extendedReport.fieldz", "fieldz"),
            new("c-twice.json", Spliced("user-twice", extended + "," + extended), "user-twice",
                "Refused", "$.extendedReport", "extendedReport"),
            new("d-broken.json", "{ \"id\": \"user-broken\", \"name\": ", null,
                "Refused", null, null),
            new("d-not-a-profile.json", "{ \"_meta\": { \"source\": \"a descriptor collection\" } }", null,
                "NotAProfile", "$.id", "no id"),
            new("e-dup-1.json", WithId("user-dup"), null, null, null, null),
            new("e-dup-2.json", WithId("user-dup"), "user-dup",
                "Refused", "$.id", "e-dup-1.json"),
            new("f-catalog-copy.json", WithId(BaseId), BaseId,
                "Refused", "$.id", "catalog:"),
            // A repeated id is refused at the key the file writes. At
            // 7d62b08f it read $.id.
            new("e-dup-3.json", SpelledID(WithId("user-dup")), "user-dup",
                "Refused", "$.ID", "e-dup-1.json"),
            new("g-no-id.json", "{ \"name\": \"no id here\" }", null,
                "NotAProfile", "$.id", "no id"),
            new("h-bad-value.json", WithId("user-bad-value", o => o["dataReportId"] = "0x7E"), "user-bad-value",
                "Refused", "$.dataReportId", "0x7E"),
            new("i-bad-layout.json", WithId("user-bad-layout", o => o["layout"] = JsonNode.Parse(
                "{\"kind\":\"pedals\",\"pedals\":[{\"axis\":\"Slider\",\"role\":\"accelerator\",\"type\":\"load_cell\"}]}")), "user-bad-layout",
                "Refused", "$.layout", "Slider"),
            // A codec parameter v2.0.0 requires, refused at load and not
            // first at create.
            new("i-no-max.json", WithId("user-no-max", o => o["extendedReport"] = JsonNode.Parse(
                "{\"reportId\":\"0x01\",\"size\":8,\"fields\":[{\"byte\":1,\"type\":\"uint16-trigger\",\"semantic\":\"leftTrigger\"}]}")), "user-no-max",
                "Refused", "$.extendedReport.fields[0]", "max is required"),
            new("j-good.json", WithId("user-good"), null, null, null, null),
        };
    }

    /// <summary>The id a case's text names, read as text, since some of
    /// the cases do not parse.</summary>
    static string? IdOf(string text)
    {
        int at = text.IndexOf("\"id\"", StringComparison.Ordinal);
        if (at < 0) return null;
        int open = text.IndexOf('"', text.IndexOf(':', at) + 1);
        int close = text.IndexOf('"', open + 1);
        return open < 0 || close < 0 ? null : text[(open + 1)..close];
    }

    static void UserFiles(Checks c)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hm-profile-model-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            var cases = Cases();
            foreach (var k in cases) File.WriteAllText(Path.Combine(dir, k.File), k.Text);
            // A models folder holds models, never profiles: a named model
            // there loads as one, and nothing in it becomes a profile.
            Directory.CreateDirectory(Path.Combine(dir, "models"));
            File.WriteAllText(Path.Combine(dir, "models", "k-in-models.json"),
                "{ \"id\": \"user-in-models\", \"driverPid\": \"0x02FF\" }");

            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            int added = ctx.LoadProfilesFromDirectory(dir);
            var loaded = ctx.AllProfiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var issues = new List<Api.Issue>();
            c.Run("HMContext.ProfileLoadIssues reads", () => { issues.AddRange(Api.Issues(ctx, "ProfileLoadIssues")); return true; });
            foreach (var issue in issues) Console.WriteLine($"    issue: {issue}");

            foreach (var k in cases)
            {
                string full = Path.Combine(dir, k.File);
                var mine = issues.Where(i => string.Equals(i.File, full, StringComparison.OrdinalIgnoreCase)).ToList();
                if (k.Kind == null)
                {
                    c.Check($"{k.File} loads, with no issue", mine.Count == 0 && loaded.Contains(JsonNode.Parse(k.Text)!["id"]!.GetValue<string>()),
                            string.Join("; ", mine.Select(i => i.ToString())));
                    continue;
                }
                c.Run($"{k.File}: one {k.Kind} issue{(k.Path != null ? $" at {k.Path}" : "")}{(k.Reason != null ? $" naming {k.Reason}" : "")}", () =>
                {
                    bool ok = mine.Count == 1 && mine[0].Kind == k.Kind
                              && (k.Path == null || mine[0].Path == k.Path || mine[0].Path.StartsWith(k.Path + ".", StringComparison.Ordinal))
                              && (k.Reason == null || mine[0].Reason.Contains(k.Reason, StringComparison.Ordinal))
                              && mine[0].Id == k.Id;
                    return (ok, string.Join("; ", mine.Select(i => $"{i.Kind} {i.Id} {i.Path} {i.Reason}")));
                });
                string? refusedId = IdOf(k.Text);
                if (refusedId != null && refusedId != BaseId && refusedId != "user-dup")
                    c.Check($"{k.File}: {refusedId} is not loaded", !loaded.Contains(refusedId));
            }
            c.Run("the catalog's own heusinkveld-ultimate-pedals stays the catalog's", () => ctx.GetProfile(BaseId) is { } b
                    && Api.Prop(b.Inner, "SourceFile") is string src && src.StartsWith("catalog:", StringComparison.Ordinal));
            c.Run("user-dup is the first file's, in ordinal order of paths", () => ctx.GetProfile("user-dup") is { } d
                    && Api.Prop(d.Inner, "SourceFile") is string f && Path.GetFileName(f) == "e-dup-1.json");
            c.Check("nothing in the models folder is read as a profile", !loaded.Contains("user-in-models")
                    && !issues.Any(i => i.File.Contains("k-in-models", StringComparison.Ordinal)));
            c.Check("LoadProfilesFromDirectory counts the two profiles it added", added == 2, $"{added}");
            // Across loads too: a later directory's copy of a catalog id that
            // spells its key ID. At 7d62b08f it was listed at $.id.
            c.Run("a later load's repeat of a catalog id is refused at the key the file writes", () =>
            {
                string later = Path.Combine(dir, "later");
                Directory.CreateDirectory(later);
                string file = Path.Combine(later, "catalog-copy-spelled.json");
                File.WriteAllText(file, SpelledID(WithId(BaseId)));
                ctx.LoadProfilesFromDirectory(later);
                var mine = Api.Issues(ctx, "ProfileLoadIssues").Where(i => string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
                return (mine.Count == 1 && mine[0].Path == "$.ID" && mine[0].Reason.Contains("catalog:", StringComparison.Ordinal),
                        string.Join("; ", mine.Select(i => $"{i.Kind} {i.Path} {i.Reason}")));
            });
            c.Run("loading the catalog again adds nothing and lists nothing", () =>
            {
                int before = Api.Issues(ctx, "ProfileLoadIssues").Count;
                int again = ctx.LoadDefaultProfiles();
                int after = Api.Issues(ctx, "ProfileLoadIssues").Count;
                return (again == 0 && after == before, $"{again} added, {after - before} issue(s)");
            });
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── The path is the one the author wrote ─────────────────────────────

    /// <summary>A refusal inside a layout names the file's own path: the
    /// layout's prefix, each array index, and each key as the file spells
    /// it, since the reader matches keys without regard to case. The reason
    /// does not repeat the path. At 08fd21f3 a key the layout's reader did
    /// not map was named from inside the layout (<c>$.pedalz</c>), and a
    /// layout check named <c>$.layout.sticks[].click_button</c>, no index
    /// and not the key the file holds.</summary>
    static void Paths(Checks c)
    {
        JsonObject From(string id)
        {
            var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == id);
            return JsonNode.Parse(JsonSerializer.Serialize(p, HMJsonContext.Default.ControllerProfile))!.AsObject();
        }
        var cases = new List<(string Name, string Text, string Path)>();
        void Add(string name, string id, Func<JsonObject, string> edit, string path)
        {
            var o = From(id);
            o["id"] = "user-path";
            cases.Add((name, edit(o), path));
        }
        Add("a key the layout's reader does not map", BaseId, o =>
        {
            o["layout"]!.AsObject()["pedalz"] = new JsonArray();
            return o.ToJsonString();
        }, "$.layout.pedalz");
        Add("a misspelled key in the first pedal", BaseId, o =>
        {
            o["layout"]!["pedals"]![0]!.AsObject()["axs"] = "X";
            return o.ToJsonString();
        }, "$.layout.pedals[0].axs");
        Add("a pedal on an axis the descriptor lacks", BaseId, o =>
        {
            o["layout"]!["pedals"]![1]!["axis"] = "Slider";
            return o.ToJsonString();
        }, "$.layout.pedals[1].axis");
        Add("the second stick's click on a button the descriptor lacks", "xbox-360-wired", o =>
        {
            o["layout"]!["sticks"]![1]!["clickButton"] = 99;
            return o.ToJsonString();
        }, "$.layout.sticks[1].clickButton");
        Add("the same, with the keys spelled in another case", "xbox-360-wired", o =>
        {
            o["layout"]!["sticks"]![1]!["clickButton"] = 99;
            return o.ToJsonString().Replace("\"sticks\"", "\"Sticks\"").Replace("\"clickButton\"", "\"ClickButton\"")
                                   .Replace("\"layout\"", "\"Layout\"");
        }, "$.Layout.Sticks[1].ClickButton");
        Add("a dpad hat on an axis the descriptor lacks", "xbox-360-wired", o =>
        {
            o["layout"]!["dpad"]!["hatAxis"] = "Slider";
            return o.ToJsonString();
        }, "$.layout.dpad.hatAxis");
        Add("a face button past the descriptor's buttons", "xbox-360-wired", o =>
        {
            o["layout"]!["faceButtons"]![2]!["buttonIndex"] = 40;
            return o.ToJsonString();
        }, "$.layout.faceButtons[2].buttonIndex");
        // A map's key keeps its path whatever it holds, written as
        // System.Text.Json writes one. At d69bfd0a a dash ended the path at
        // the map and the reason repeated it, and a dot split the key.
        Add("a dance pad key with a dash, written null", "xbox-360-dance-pad", o =>
        {
            o["layout"]!["pads"]!.AsObject()["up-left"] = null;
            return o.ToJsonString();
        }, "$.layout.pads.up-left");
        Add("a dance pad key with a space, on a button the descriptor lacks", "xbox-360-dance-pad", o =>
        {
            o["layout"]!["pads"]!.AsObject()["up left"] = JsonNode.Parse("{\"role\":\"dpad_up\",\"buttonIndex\":99}");
            return o.ToJsonString();
        }, "$.layout.pads['up left'].buttonIndex");
        Add("a shifter gear key with a dot, written null", "thrustmaster-th8a-shifter", o =>
        {
            o["layout"]!["hPatternGears"]!.AsObject()["1.5"] = null;
            return o.ToJsonString();
        }, "$.layout.hPatternGears['1.5']");
        Add("a codec key v2.0.0 requires, under keys spelled in another case", BaseId, o =>
        {
            o["extendedReport"] = JsonNode.Parse(
                "{\"reportId\":\"0x01\",\"size\":8,\"fields\":[{\"byte\":1,\"type\":\"uint16-trigger\",\"semantic\":\"leftTrigger\"}]}");
            return o.ToJsonString().Replace("\"extendedReport\"", "\"ExtendedReport\"").Replace("\"fields\"", "\"Fields\"");
        }, "$.ExtendedReport.Fields[0]");
        // An axisMap names each usage once, with a name a slot takes. At
        // c3697a89 a second spelling of 0x32 put the field in two slots,
        // and a name no slot takes left it in none.
        Add("a second spelling of an axisMap usage", "dualsense", o =>
        {
            o["axisMap"]!.AsObject()["0X32"] = "leftTrigger";
            return o.ToJsonString();
        }, "$.axisMap.0X32");
        Add("an axisMap name no slot takes", "dualsense", o =>
        {
            o["axisMap"]!["0x32"] = "rightStickx2";
            return o.ToJsonString();
        }, "$.axisMap.0x32");
        // A usbConfiguration the descriptor set cannot hold is refused at
        // load on a persona with no protocol or WinUSB package too. At
        // 8debad63 it loaded and failed at create, after the lease.
        Add("a USB/IP persona's interface with its alternate settings null", "steam-controller-2", o =>
        {
            o["usbConfiguration"]!["interfaces"]![0]!.AsObject()["altSettings"] = null;
            return o.ToJsonString();
        }, "$.usbConfiguration.interfaces[0].altSettings");
        // A patch with no source is refused at its source whatever else the
        // entry holds. At c19d24a1 an ascii patch was refused at its
        // encoding, and an entry with a CRC at its crc32, as if the null
        // were a file.
        Add("an ascii patch with no source", "steam-controller-2", o =>
        {
            var p = o["featureStubs"]!["reports"]![0]!["patch"]![0]!.AsObject();
            p["source"] = null;
            p["encoding"] = "ascii";
            p.Remove("key");
            return o.ToJsonString();
        }, "$.featureStubs.reports[0].patch[0].source");
        Add("a patch with no source on an entry with a CRC", "steam-controller-2", o =>
        {
            var r = o["featureStubs"]!["reports"]![0]!.AsObject();
            r["crc32"] = JsonNode.Parse("{\"seed\":\"A3\"}");
            r["patch"]![0]!["source"] = null;
            return o.ToJsonString();
        }, "$.featureStubs.reports[0].patch[0].source");
        // A VID or PID that is no 16-bit hex number is refused at load at its
        // key. At 08fd21f3 the identity match met the bad VID first and
        // refused the file at $, and a bad PID loaded and failed at create.
        Add("a VID that is not hex", BaseId, o => { o["vid"] = "0xZZZZ"; return o.ToJsonString(); }, "$.vid");
        Add("a VID past 16 bits", BaseId, o => { o["vid"] = "0x12345"; return o.ToJsonString(); }, "$.vid");
        Add("a PID that is not hex", BaseId, o => { o["pid"] = "0x1G"; return o.ToJsonString(); }, "$.pid");
        Add("no VID", BaseId, o => { o.Remove("vid"); return o.ToJsonString(); }, "$.vid");
        Add("an empty PID, spelled PID", BaseId, o => { o.Remove("pid"); o["PID"] = ""; return o.ToJsonString(); }, "$.PID");
        // The reader takes "ID" as the id, so a file that spells it so is a
        // profile and its defect is listed as one. At 313a54c2 the loader
        // looked for the id of a file that did not read with a
        // case-sensitive match and listed it as no profile.
        Add("a misspelled key in a file that spells its id ID", BaseId, o =>
        {
            o.Remove("id");
            o["ID"] = "user-path";
            o["inptuReports"] = new JsonObject();
            return o.ToJsonString();
        }, "$.inptuReports");
        foreach (var (name, text, path) in cases)
        {
            c.Run($"{name}: refused at {path}, and the reason does not repeat the path", () =>
            {
                var db = ProfileDatabase.LoadTexts(new[] { ("user-path.json", text) }, ModelRegistry.Catalog);
                var issues = Api.Issues(db, "Issues");
                bool ok = issues.Count == 1 && issues[0].Path == path && issues[0].Kind == "Refused" && issues[0].Id == "user-path"
                          && !issues[0].Reason.Contains("layout/", StringComparison.Ordinal)
                          && !issues[0].Reason.StartsWith(path, StringComparison.Ordinal);
                return (ok, string.Join("; ", issues.Select(i => $"{i.Kind} {i.Id} {i.Path}: {i.Reason}")));
            });
        }
    }

    // ── One exception type for a bad profile ─────────────────────────────

    static void Exceptions(Checks c)
    {
        var basis = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == BaseId);
        ControllerProfile Copy(Action<ControllerProfile> edit)
        {
            var p = JsonSerializer.Deserialize(JsonSerializer.Serialize(basis, HMJsonContext.Default.ControllerProfile),
                HMJsonContext.Default.ControllerProfile)!;
            p.Id = "user-code-built";
            edit(p);
            return p;
        }
        using var ctx = new HMContext();
        void Create(string name, ControllerProfile p, string path)
        {
            c.Run($"CreateController on a code-built profile with {name} throws HMProfileException at {path} before anything machine-wide", () =>
            {
                try
                {
                    using var ctrl = ctx.CreateController(new HMProfile(p));
                    return (false, "created");
                }
                catch (Exception e) when (Api.ProfileError(e) is var (jp, reason, id))
                {
                    return (jp.StartsWith(path, StringComparison.Ordinal) && id == p.Id && e is InvalidOperationException,
                            $"{jp}: {reason}");
                }
                catch (Exception e)
                {
                    return (false, $"{e.GetType().Name}: {e.Message}");
                }
            });
        }
        Create("a layout naming an axis the descriptor lacks", Copy(p => p.Layout = JsonSerializer.Deserialize(
            "{\"kind\":\"pedals\",\"pedals\":[{\"axis\":\"Slider\",\"role\":\"accelerator\",\"type\":\"load_cell\"}]}", HMJsonContext.Default.HMLayout)), "$.layout");
        Create("no descriptor", Copy(p => p.Descriptor = null), "$.descriptor");
        Create("an extended report too large for the lane", Copy(p => p.ExtendedReport = new ExtendedReportSpec
        {
            ReportId = "0x01", Size = 200, AlwaysArmed = true,
        }), "$.extendedReport");

        c.Run("HMProfileBuilder.Build on a descriptor that is not hex throws HMProfileException at $.descriptor", () =>
        {
            try
            {
                new HMProfileBuilder().Id("user-builder").Vid(0x1234).Pid(0x5678).DescriptorHex("05010905zz").Build();
                return (false, "built");
            }
            catch (Exception e) when (Api.ProfileError(e) is var (jp, reason, _)) { return (jp == "$.descriptor", $"{jp}: {reason}"); }
            catch (Exception e) { return (false, $"{e.GetType().Name}: {e.Message}"); }
        });
        c.Run("HMProfileBuilder.Build without a VID throws HMProfileException at $.vid", () =>
        {
            try { new HMProfileBuilder().Id("user-builder").Pid(0x5678).Build(); return (false, "built"); }
            catch (Exception e) when (Api.ProfileError(e) is var (jp, reason, _)) { return (jp == "$.vid", $"{jp}: {reason}"); }
            catch (Exception e) { return (false, $"{e.GetType().Name}: {e.Message}"); }
        });
    }

    // ── Earlier tags' catalogs ───────────────────────────────────────────

    /// <summary>The removed spellings a v2.0.0 load refuses by name: the
    /// codec's ds3-battery type and featureStubs' steamFirmwareStamp, and the
    /// codec keys v2.0.0 requires where v1 assumed a value: max on a
    /// uint16-trigger or uint16-pressure and invert on an int16 Y.</summary>
    static readonly string[] RemovedSpellings = { "ds3-battery", "steamFirmwareStamp", "max is required", "invert is required" };

    /// <summary>What each removed spelling's refusal names in its
    /// place.</summary>
    static readonly Dictionary<string, string> Replacement = new()
    {
        ["ds3-battery"] = "uint8-level-map",
        ["steamFirmwareStamp"] = "patch",
        ["max is required"] = "\"max\": 32767",
        ["invert is required"] = "\"invert\": true",
    };

    /// <summary>Every distinct profile file of the v1.x tags, read through
    /// git, loads but for a removed spelling or a required key, which is
    /// refused by name with its replacement. Skipped when git or the tags are not here.</summary>
    static void EarlierTags(Checks c)
    {
        string repo = Program.RepoRoot();
        List<string> tags;
        try { tags = Git(repo, "tag -l v1.*").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList(); }
        catch (Exception e) { Console.WriteLine($"  [SKIP] strict: earlier tags not read ({e.Message})"); return; }
        if (tags.Count == 0) { Console.WriteLine("  [SKIP] strict: no v1.x tags in this clone"); return; }

        var blobs = new Dictionary<string, (string Tag, string Path)>(StringComparer.Ordinal);
        foreach (string tag in tags)
            foreach (string line in Git(repo, $"ls-tree -r {tag} profiles").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // <mode> blob <id>\t<path>
                int tab = line.IndexOf('\t');
                string path = line[(tab + 1)..].Trim();
                string[] head = line[..tab].Split(' ');
                if (head[1] != "blob" || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/schema.json", StringComparison.OrdinalIgnoreCase) || path.StartsWith("profiles/models/", StringComparison.Ordinal))
                    continue;
                blobs.TryAdd(head[2], (tag, path));
            }
        int loaded = 0, notProfiles = 0;
        var removed = new List<string>();
        var refused = new List<string>();
        foreach (var (blob, (tag, path)) in blobs.OrderBy(b => b.Value.Tag, StringComparer.Ordinal).ThenBy(b => b.Value.Path, StringComparer.Ordinal))
        {
            string text = Git(repo, $"cat-file blob {blob}");
            var issue = ParseOne($"{tag}:{path}", text);
            if (issue == null) { loaded++; continue; }
            if (issue.Kind == "NotAProfile") { notProfiles++; continue; }
            string? spelling = RemovedSpellings.FirstOrDefault(s => issue.Reason.Contains(s, StringComparison.Ordinal));
            if (spelling != null && Replacement.TryGetValue(spelling, out var with) && issue.Reason.Contains(with, StringComparison.Ordinal))
                removed.Add($"{tag}:{path} {issue.Path} ({spelling}, naming {with})");
            else refused.Add($"{tag}:{path} {issue.Path}: {issue.Reason}");
        }
        foreach (var x in removed) Console.WriteLine($"    removed spelling: {x}");
        foreach (var x in refused) Console.WriteLine($"    refused: {x}");
        c.Check($"the {blobs.Count} distinct profile files of {tags.Count} v1.x tags load, but for a removed spelling or a required key",
                refused.Count == 0, $"{loaded} loaded, {removed.Count} refused for a removed spelling or a required key, {notProfiles} not profiles, {refused.Count} refused otherwise");
    }

    /// <summary>One file through the loader's own parse and checks:
    /// null when it loads, else its issue.</summary>
    static Api.Issue? ParseOne(string file, string text)
    {
        var parse = typeof(ProfileDatabase).GetMethod("Parse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException("ProfileDatabase", "Parse");
        object parsed = parse.Invoke(null, new object[] { file, text })!;
        object? issue = Api.Prop(parsed, "Issue");
        return issue == null ? null
            : new Api.Issue(Api.Prop(issue, "Kind")!.ToString()!, file, (string?)Api.Prop(issue, "ProfileId"),
                            (string)Api.Prop(issue, "JsonPath")!, (string)Api.Prop(issue, "Reason")!);
    }

    static string Git(string repo, string args)
    {
        var psi = new ProcessStartInfo("git", $"-C \"{repo}\" {args}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args}: {err.Result.Trim()}");
        return output;
    }
}
