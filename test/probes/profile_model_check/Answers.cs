// Answer tables (design note, mechanism 4), the SDK side.
//
// One table for every answer a device gives on request: plain entries by
// the report ID a GET arrives on, message entries by the last SET on their
// own report, pages served in turn, an exact parameter before none, sizes,
// a minimum length, an echo, the request's report ID in byte 0, a CRC and a
// feature-read notification. The Sony answers live in six named models the
// catalog's Sony profiles name, and the Valve composites carry their own
// entries. Here: matching on a profile that carries Sony report-ID answers
// and a DualSense-style test channel together, a parameter at an offset of
// its own, the Sony models, each refusal at load with its JSON path, the
// blob the UMDF2 driver reads, and the identity defaults for a profile
// outside the catalog. The base fails every check: it has one match rule a
// table and its Sony answers in code, loads each refused table below, and
// skips the Valve table with its parameters moved.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Answers
{
    public static int Run()
    {
        var c = new Checks("answers");
        Section(c, "matching", Matching);
        Section(c, "a parameter at its own offset", ParamOffset);
        Section(c, "the Sony models", SonyModels);
        Section(c, "refusals", Refusals);
        Section(c, "the blob", Blob);
        Section(c, "identity defaults", Identity);
        Section(c, "the feature-read notification", Notify);
        return c.Failures;
    }

    /// <summary>Runs one group of checks. An SDK without the members the
    /// group calls fails the group once, naming what it lacks.</summary>
    static void Section(Checks c, string name, Action<Checks> body)
    {
        try { body(c); }
        catch (Exception e) when (e is TypeLoadException or MissingMemberException)
        {
            c.Check($"{name}: the SDK has the members these checks call", false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    static JsonObject FromRepo(string file, string id)
    {
        string path = Path.Combine(Program.RepoRoot(), "profiles", file);
        var o = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        o["id"] = id;
        return o;
    }

    /// <summary>A profile loaded from JSON through the loader, so its own
    /// keys meet the load checks. Null, with the issue, when refused.</summary>
    static (HMProfile? Profile, Api.Issue? Issue) Load(JsonObject json)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hm-profile-answers-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "p.json"), json.ToJsonString());
            using var ctx = new HMContext();
            ctx.LoadProfilesFromDirectory(dir);
            List<Api.Issue> issues;
            try { issues = Api.Issues(ctx, "ProfileLoadIssues"); }
            catch (MissingMemberException) { issues = new(); }
            return (ctx.GetProfile(json["id"]!.GetValue<string>()), issues.FirstOrDefault(i => i.Kind == "Refused"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static byte[]? Get(FeatureStubTable t, byte id, int length, out bool notify)
    {
        int n = t.Lookup(id, length, out notify);
        return n < 0 ? null : t.Answer(n).ToArray();
    }

    static string Hex(byte[]? b) => b == null ? "stall" : Convert.ToHexString(b);

    // ── Matching ─────────────────────────────────────────────────────────

    /// <summary>dualsense with its own table: two of the Sony report-ID
    /// answers and dualsense-tester's test channel (ff340fe ds.util.ts:
    /// SET 0x80 [device][action], then GET 0x81 polled, each answer
    /// 81 device action status and 56 bytes, status 3 for a page with more
    /// to come and 2 for the last, ds.util.ts:164-176, 223-240 and
    /// ds.type.ts:240-246).</summary>
    static JsonObject TestChannelProfile()
    {
        var o = FromRepo(@"sony\dualsense.json", "user-test-channel");
        o.Remove("deviceModelFrom");
        string Page(byte status, byte fill) => "81" + "01" + "04" + status.ToString("X2") + string.Concat(Enumerable.Repeat(fill.ToString("X2"), 4));
        o["featureStubs"] = new JsonObject
        {
            ["reports"] = new JsonArray(
                new JsonObject { ["id"] = "0x09", ["size"] = 20, ["minLength"] = 20, ["data"] = "0902484D000000", ["notify"] = true,
                                 ["patch"] = new JsonArray(new JsonObject { ["at"] = 6, ["length"] = 1, ["encoding"] = "uint8", ["source"] = "index" }) },
                new JsonObject { ["id"] = "0x05", ["size"] = 41, ["minLength"] = 41, ["data"] = "05AABB", ["notify"] = true },
                new JsonObject
                {
                    ["id"] = "0x81", ["size"] = 64,
                    ["message"] = new JsonObject { ["onReport"] = "0x80", ["at"] = 1, ["value"] = "0104" },
                    ["pages"] = new JsonArray(Page(3, 0x11), Page(3, 0x22), Page(2, 0x33)),
                },
                new JsonObject
                {
                    ["id"] = "0x81", ["size"] = 64,
                    ["message"] = new JsonObject { ["onReport"] = "0x80", ["at"] = 1, ["value"] = "0102" },
                    ["data"] = "81010202",
                }),
        };
        return o;
    }

    static void Matching(Checks c)
    {
        var (p, issue) = Load(TestChannelProfile());
        c.Check("a profile with Sony report-ID answers and a test channel loads", p != null, issue?.ToString() ?? "");
        if (p == null) return;
        FeatureStubTable? t = null;
        c.Run("its table builds for index 7", () => (t = FeatureStubTable.From(p.Inner, 7, null, _ => null)) != null);
        if (t == null) return;
        c.Run("report 0x09 answers with the index in its MAC and publishes", () =>
        {
            var a = Get(t, 0x09, 64, out bool n);
            return (a != null && a.Length == 20 && a[6] == 7 && n, Hex(a));
        });
        c.Run("report 0x81 stalls before any SET", () => (Get(t, 0x81, 64, out _) == null, ""));
        c.Run("SET 0x80 01 04, then three GETs of 0x81 serve the three pages, and a fourth the last again", () =>
        {
            t.OnSetFeature(0x80, new byte[] { 0x80, 0x01, 0x04, 0x00 });
            var a = Get(t, 0x81, 64, out _); var b = Get(t, 0x81, 64, out _);
            var d = Get(t, 0x81, 64, out _); var e = Get(t, 0x81, 64, out _);
            bool ok = a?[3] == 3 && a[4] == 0x11 && b?[3] == 3 && b[4] == 0x22 && d?[3] == 2 && d[4] == 0x33 && e?[4] == 0x33;
            return (ok, $"{Hex(a?[..8])} {Hex(b?[..8])} {Hex(d?[..8])} {Hex(e?[..8])}");
        });
        c.Run("a SET on 0x80 starts the pages again", () =>
        {
            t.OnSetFeature(0x80, new byte[] { 0x80, 0x01, 0x04 });
            var a = Get(t, 0x81, 64, out _);
            return (a?[4] == 0x11, Hex(a?[..8]));
        });
        c.Run("another action answers its own entry", () =>
        {
            t.OnSetFeature(0x80, new byte[] { 0x80, 0x01, 0x02 });
            var a = Get(t, 0x81, 64, out _);
            return (a != null && a[2] == 0x02 && a[3] == 0x02, Hex(a?[..8]));
        });
        c.Run("a SET on another report leaves the channel's message", () =>
        {
            t.OnSetFeature(0x05, new byte[] { 0x05, 0x01, 0x04 });
            var a = Get(t, 0x81, 64, out _);
            return (a != null && a[2] == 0x02, Hex(a?[..8]));
        });
        c.Run("an action no entry names stalls, and report 0x05 still answers", () =>
        {
            t.OnSetFeature(0x80, new byte[] { 0x80, 0x09, 0x09 });
            return (Get(t, 0x81, 64, out _) == null && Get(t, 0x05, 64, out _) is { Length: 41 }, "");
        });
        c.Run("report 0x05 refuses a buffer shorter than its minLength", () => (t.Lookup(0x05, 40, out _) == -2, ""));

        // An unnumbered channel: steam-deck-composite as the catalog ships
        // it, its message at byte 0 of a SET on report 0.
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var deck = FeatureStubTable.From(ctx.GetProfile("steam-deck-composite")!.Inner, 0, null, _ => null)!;
        c.Run("a descriptor without report IDs: SET 0x83 on report 0, GET report 0 answers 0x83's attributes", () =>
        {
            deck.OnSetFeature(0, new byte[] { 0x83, 0x00 });
            var a = Get(deck, 0, 64, out bool n);
            return (a != null && a[0] == 0x83 && a[1] == 0x2D && n, Hex(a?[..4]));
        });
        c.Run("and a GET naming another report is report 0's on that descriptor", () =>
        {
            var a = Get(deck, 0x42, 64, out _);
            return (a != null && a[0] == 0x83, Hex(a?[..4]));
        });
    }

    // ── A parameter at an offset of its own ──────────────────────────────

    static void ParamOffset(Checks c)
    {
        // steam-deck-composite with its 0xAE entries' parameters at byte 3,
        // one past Valve's framing.
        var o = FromRepo(@"valve\steam-deck-composite.json", "user-param-at-3");
        foreach (var r in o["featureStubs"]!["reports"]!.AsArray())
            if (r!["param"] is JsonObject param) param["at"] = 3;
        var (p, issue) = Load(o);
        c.Check("a table with its parameters at byte 3 loads", p != null, issue?.ToString() ?? "");
        if (p == null) return;
        c.Run("SET AE 14 FF 01 answers index 1, read at byte 3, and SET AE 14 01 FF the entry for any index", () =>
        {
            var t = FeatureStubTable.From(p.Inner, 0, null, _ => null)!;
            t.OnSetFeature(0, new byte[] { 0xAE, 0x14, 0xFF, 0x01 });
            var a = Get(t, 0, 64, out _);
            t.OnSetFeature(0, new byte[] { 0xAE, 0x14, 0x01, 0xFF });
            var b = Get(t, 0, 64, out _);
            return (a?[2] == 0x01 && b?[2] == 0xFF, $"{Hex(a?[..4])} {Hex(b?[..4])}");
        });
    }

    // ── The Sony models ──────────────────────────────────────────────────

    /// <summary>Each Sony catalog profile names its model and answers the
    /// seven Sony report IDs: the index at byte 6 of 0x09 and 0x12, and on
    /// Bluetooth a CRC-32 over A3 and the answer in the last four bytes of
    /// 0x05, 0x09 and 0x20. Byte for byte against 9bd6279's tables is the
    /// oracle's (case 4 on USB/IP, case 5 on UMDF2).</summary>
    static void SonyModels(Checks c)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var sony = ctx.AllProfiles.Where(p => p.VendorId == 0x054C && p.Inner.Resolved.Model.FeatureStubs != null).ToList();
        c.Check("sixteen Sony catalog profiles carry answers", sony.Count == 16, string.Join(", ", sony.Select(p => p.Id)));
        foreach (var p in sony)
        {
            c.Run($"{p.Id}: takes its answers from the model it names", () =>
            {
                var src = (IReadOnlyDictionary<string, string>)Api.Prop(p, "DeviceModelSource")!;
                return (src["answers"].StartsWith("named:sony-", StringComparison.Ordinal), src["answers"]);
            });
            if (!p.Inner.GetOrBuildReportMap().UsesReportIds)
            {
                // HidClass asks a descriptor without report IDs on report 0
                // alone, which no Sony entry answers, as no Sony table did.
                c.Run($"{p.Id}: its descriptor has no report IDs, so every GET is report 0's and stalls", () =>
                {
                    var t = FeatureStubTable.From(p.Inner, 0, null, _ => null)!;
                    return new byte[] { 0x00, 0x02, 0x05, 0x09, 0x12, 0x20, 0x22, 0xA3 }.All(id => t.Lookup(id, 64, out _) == -1);
                });
                continue;
            }
            bool bt = string.Equals(p.Inner.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase);
            c.Run($"{p.Id}: index at byte 6 of 0x09 and 0x12{(bt ? ", CRC over A3 on 0x05, 0x09 and 0x20" : "")}", () =>
            {
                foreach (int index in new[] { 0, 1, 15 })
                {
                    var t = FeatureStubTable.From(p.Inner, index, null, _ => null)!;
                    foreach (byte id in new byte[] { 0x02, 0x05, 0x09, 0x12, 0x20, 0x22, 0xA3 })
                    {
                        var a = Get(t, id, 64, out bool n);
                        if (a == null || a[0] != id || !n) return (false, $"index {index} report 0x{id:X2}: {Hex(a)}");
                        if (id is 0x09 or 0x12 && a[6] != index) return (false, $"index {index} report 0x{id:X2} byte 6 is {a[6]}");
                        if (bt && id is 0x05 or 0x09 or 0x20)
                        {
                            uint crc = 0xFFFFFFFF;
                            foreach (byte x in new byte[] { 0xA3 }.Concat(a.Take(a.Length - 4)))
                            {
                                crc ^= x;
                                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                            }
                            if (BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(a.Length - 4)) != ~crc)
                                return (false, $"index {index} report 0x{id:X2}: CRC");
                        }
                    }
                }
                return (true, "");
            });
        }
    }

    // ── Refusals at load ─────────────────────────────────────────────────

    static void Refusals(Checks c)
    {
        var cases = new List<(string Name, Action<JsonObject> Edit, string Path, string Reason)>
        {
            ("an id the descriptor does not declare", o => Reports(o)[1]!["id"] = "0x7E", "$.featureStubs.reports[1].id", "does not declare"),
            ("an onReport the descriptor does not declare", o => Reports(o)[2]!["message"]!["onReport"] = "0x7E", "$.featureStubs.reports[2].message.onReport", "not a feature report"),
            ("a message past a SET's 64 bytes", o => Reports(o)[2]!["message"]!["at"] = 63, "$.featureStubs.reports[2].message.at", "outside a SET's 64 bytes"),
            ("a message past its report's size", o => { Reports(o)[2]!["message"]!["onReport"] = "0x05"; Reports(o)[2]!["message"]!["at"] = 40; },
             "$.featureStubs.reports[2].message.at", "are not inside report 0x05's 41 bytes"),
            ("pages on a plain entry", o => { Reports(o)[1]!.AsObject().Remove("data"); Reports(o)[1]!["pages"] = new JsonArray("05"); }, "$.featureStubs.reports[1].pages", "message entry"),
            ("a CRC on four bytes", o => { Reports(o)[1]!["size"] = 4; Reports(o)[1]!["minLength"] = 4; Reports(o)[1]!["data"] = "05"; Reports(o)[1]!["crc32"] = new JsonObject { ["seed"] = "A3" }; }, "$.featureStubs.reports[1].crc32", "five bytes"),
            ("a CRC with sizes", o => { Reports(o)[0]!.AsObject().Remove("minLength"); Reports(o)[0]!["sizes"] = new JsonArray(20, 16); Reports(o)[0]!["crc32"] = new JsonObject { ["seed"] = "A3" }; }, "$.featureStubs.reports[0].crc32", "sizes"),
            ("a minLength past the size", o => Reports(o)[1]!["minLength"] = 42, "$.featureStubs.reports[1].minLength", "not 0 to"),
            ("an echo with data", o => Reports(o)[3]!["echo"] = true, "$.featureStubs.reports[3].echo", "takes no data"),
            ("a replyHead it does not know", o => Reports(o)[1]!["replyHead"] = "reportId", "$.featureStubs.reports[1].replyHead", "requestReportId"),
            ("a key a message does not take", o => Reports(o)[2]!["message"]!["value2"] = "01", "$.featureStubs.reports[2].message.value2", "value2"),
            ("a parameter on a plain entry", o => Reports(o)[1]!["param"] = new JsonObject { ["at"] = 3, ["value"] = "01" }, "$.featureStubs.reports[1].param", "message"),
            ("five reports carrying messages", o =>
            {
                foreach (string on in new[] { "0x08", "0x09", "0x20", "0x22" })
                    Reports(o).Add(new JsonObject { ["id"] = "0x81", ["size"] = 64, ["data"] = "81",
                        ["message"] = new JsonObject { ["onReport"] = on, ["at"] = 1, ["value"] = "01" } });
            }, "$.featureStubs.reports", "past the 4"),
            ("sixty-five answers", o =>
            {
                for (int i = 0; i < 61; i++) Reports(o).Add(new JsonObject { ["id"] = "0x22", ["size"] = 64, ["data"] = "22" });
            }, "$.featureStubs.reports", "past the 64"),
        };
        foreach (var (name, edit, path, reason) in cases)
        {
            var o = TestChannelProfile();
            o["id"] = "user-refused";
            edit(o);
            var (p, issue) = Load(o);
            c.Check($"{name}: refused at {path}, naming {reason}", p == null && issue != null && issue.Path == path
                    && issue.Reason.Contains(reason, StringComparison.Ordinal), issue == null ? "loaded" : $"{issue.Path}: {issue.Reason}");
        }
        // A taken model keeps an answer for a report the descriptor lacks.
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        c.Run("ps-classic takes sony-dualsense though its descriptor has no report IDs, with each of the seven answers noted once and kept", () =>
        {
            var p = ctx.GetProfile("ps-classic")!;
            var r = p.Inner.Resolved;
            return (r.Problem == null && r.Model.FeatureStubs != null && r.Notes.Count == 7, $"{r.Notes.Count} notes");
        });
    }

    static JsonArray Reports(JsonObject o) => o["featureStubs"]!["reports"]!.AsArray();

    // ── The blob ─────────────────────────────────────────────────────────

    static void Blob(Checks c)
    {
        var (p, _) = Load(TestChannelProfile());
        if (p == null) { c.Check("the blob's profile loads", false); return; }
        c.Run("the FeatureAnswers blob: header, entries and arena as driver.h lays them out", () =>
        {
            var t = FeatureStubTable.From(p.Inner, 7, null, _ => null)!;
            byte[] b = t.ToBlob();
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(b);
            uint format = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8));
            bool head = length == b.Length && format == 4 && count == 4 && b[10] == 1 && b[12] == 0x80;
            var e2 = b.AsSpan(16 + 2 * 16, 16);
            bool entry = e2[0] == 0x81 && e2[1] == 0x01 && e2[2] == 0 && e2[3] == 1 && e2[4] == 2 && e2[7] == 3 && e2[8] == 64;
            int arena = 16 + 4 * 16;
            var e0 = b.AsSpan(16, 16);
            int off0 = BinaryPrimitives.ReadUInt16LittleEndian(e0[12..]);
            bool mac = e0[0] == 0x09 && b[arena + off0 + 6] == 7 && e0[8] == 20 && e0[9] == 20 && (e0[1] & 0x10) != 0;
            return (head && entry && mac, $"length {length} format {format} count {count}");
        });
        c.Run("a profile with no answers writes the empty blob: the header alone", () =>
        {
            byte[] b = FeatureStubTable.EmptyBlob();
            return b.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(b) == 16 && BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)) == 4;
        });
    }

    // ── Identity defaults for a profile outside the catalog ──────────────

    static void Identity(Checks c)
    {
        HMProfile Build(ushort vid, ushort pid, string connection) => new HMProfileBuilder()
            .Id($"user-{vid:x4}-{pid:x4}-{connection}").Vid(vid).Pid(pid).Connection(connection)
            .Descriptor(ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "dualsense").GetDescriptorBytes()!).Build();
        foreach (var (vid, pid, conn, model) in new (ushort, ushort, string, string)[]
                 {
                     (0x054C, 0x0CE6, "usb", "sony-dualsense"), (0x054C, 0x0CE6, "bluetooth", "sony-dualsense-bt"),
                     (0x054C, 0x09CC, "usb", "sony-dualshock4"), (0x054C, 0x05C4, "bluetooth", "sony-dualshock4-bt"),
                     (0x054C, 0x0BA0, "usb", "sony-dualshock4"), (0x054C, 0x0DF2, "usb", "sony-dualsense-edge"),
                     (0x054C, 0x0DF2, "bluetooth", "sony-dualsense-edge-bt"), (0x054C, 0x0268, "usb", "sony-dualsense"),
                 })
        {
            c.Run($"a profile built in code as {vid:X4}:{pid:X4} over {conn} takes {model} by identity", () =>
            {
                var p = Build(vid, pid, conn);
                var src = (IReadOnlyDictionary<string, string>)Api.Prop(p, "DeviceModelSource")!;
                string want = model.EndsWith("-bt", StringComparison.Ordinal) ? model[..^3] : model;
                return (src["answers"] == $"identity:{model}", src["answers"]);
            });
        }
        c.Run("ResolveDeviceModel names the model a bare Sony profile's identity gives, in a new profile with its notes naming the entry", () =>
        {
            var p = Build(0x054C, 0x09CC, "bluetooth");
            var r = HMDeviceExtractor.ResolveDeviceModel(p);
            return (!ReferenceEquals(r, p) && r.Inner.DeviceModelFrom == "sony-dualshock4-bt"
                    && (r.Notes ?? "").Contains("sony-dualshock4-bt", StringComparison.Ordinal)
                    && ((IReadOnlyDictionary<string, string>)Api.Prop(r, "DeviceModelSource")!)["answers"] == "named:sony-dualshock4-bt"
                    && HMDeviceExtractor.ToJson(r).Contains("\"deviceModelFrom\": \"sony-dualshock4-bt\"", StringComparison.Ordinal),
                    r.Inner.DeviceModelFrom ?? "none");
        });
        c.Run("a profile outside the catalog with no Sony identity takes no answers", () =>
        {
            var p = Build(0x1234, 0x0CE6, "usb");
            return (!(bool)Api.Prop(p, "HasDeviceModel")!, "");
        });
    }

    // ── The feature-read notification ────────────────────────────────────

    /// <summary>Through v1.13.0 the usbip backend published a feature-read
    /// notification on every answered GET, and a table had no notify key.
    /// notify defaults to true on usbip, so a user's table from v1.7 to
    /// v1.13 keeps its notifications, and to false on UMDF2, where the
    /// driver notified only the Sony reads the Sony models now name. At
    /// b2797055 a usbip table without notify published none.</summary>
    static void Notify(Checks c)
    {
        // A v1.13 usbip table: plain answers keyed by report ID, no notify.
        JsonObject V1(string id, bool? notify)
        {
            var o = FromRepo(@"sony\dualsense-composite.json", id);
            o.Remove("deviceModelFrom");
            string table = "{\"reports\":[{\"id\":\"0x05\",\"size\":41,\"data\":\"05" + new string('0', 80) + "\""
                + (notify is bool n ? $",\"notify\":{(n ? "true" : "false")}" : "")
                + "},{\"id\":\"0x20\",\"size\":64,\"data\":\"20" + new string('0', 126) + "\"}]}";
            o["featureStubs"] = JsonNode.Parse(table);
            return o;
        }
        var (p, issue) = Load(V1("user-v1-table", null));
        c.Run("a usbip table with no notify keys loads", () => (p != null, issue?.ToString() ?? ""));
        if (p == null) return;
        var t = FeatureStubTable.From(p.Inner, 0, null, _ => null)!;
        c.Run("every GET it answers publishes the feature-read notification, as v1.13.0's did", () =>
        {
            bool a5 = Get(t, 0x05, 64, out bool n5) != null, a20 = Get(t, 0x20, 64, out bool n20) != null;
            return (a5 && a20 && n5 && n20, $"0x05 answered {a5} notify {n5}, 0x20 answered {a20} notify {n20}");
        });
        var (q, _) = Load(V1("user-v1-off", false));
        c.Run("notify: false on usbip publishes none", () =>
        {
            var u = FeatureStubTable.From(q!.Inner, 0, null, _ => null)!;
            return (Get(u, 0x05, 64, out bool notify) != null && !notify, "");
        });
        c.Run("on UMDF2 an answer without notify publishes none", () =>
        {
            var umdf = FromRepo(@"heusinkveld\ultimate-pedals.json", "user-umdf-answer");
            umdf["descriptor"] = FromRepo(@"sony\dualsense.json", "x")["descriptor"]!.DeepClone();
            umdf["featureStubs"] = JsonNode.Parse("{\"reports\":[{\"id\":\"0x05\",\"size\":41,\"data\":\"" + new string('0', 80) + "\"}]}");
            umdf.Remove("layout");
            var (u, why) = Load(umdf);
            if (u == null) return (false, why?.ToString() ?? "not loaded");
            var ut = FeatureStubTable.From(u.Inner, 0, null, _ => null)!;
            return (Get(ut, 0x05, 64, out bool notify) != null && !notify, "");
        });
    }
}
