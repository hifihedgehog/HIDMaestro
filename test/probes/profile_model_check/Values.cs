// Value sources and patches (design note, mechanism 5).
//
// A feature answer's per-controller bytes come from a named source:
// index, serial, or a keyedHexFile the profile declares, which reads a
// KEY:HEX line of a file found under the first root that exists. Steam's
// updater config is that file for steam-controller-2's firmware stamp,
// which SteamHardwareUpdater read in code at 9bd6279 and the removed
// steamFirmwareStamp key named. Here: the parser against 9bd6279's
// ParseStamp on its corpus and on generated lines, every encoding and
// source through a table, the file cache, and each refusal at load with
// its JSON path. A profile that keeps steamFirmwareStamp is refused naming
// the patch that replaces it. The base loads every one of these profiles,
// patches and sources and all, without a word.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Values
{
    public static int Run()
    {
        var c = new Checks("values");
        Refusals(c);
        c.Run("ParseKeyedHex reads every corpus and generated line as 9bd6279's ParseStamp did", ParserMatches);
        Patches(c);
        FileCache(c);
        return c.Failures;
    }

    // ── Refusals at load ─────────────────────────────────────────────────

    /// <summary>A catalog profile as the repository's file holds it, as JSON
    /// to edit, read from disk so the cases are the same whichever SDK
    /// loads them.</summary>
    static JsonObject FromRepo(string file, string id)
    {
        string path = Path.Combine(Program.RepoRoot(), "profiles", file);
        var o = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        o["id"] = id;
        return o;
    }

    static JsonObject Triton() => FromRepo(@"valve\steam-controller-2.json", "user-triton");

    static JsonObject Pedals() => FromRepo(@"heusinkveld\ultimate-pedals.json", "user-pedals");

    static JsonObject Entry0(JsonObject o) => o["featureStubs"]!["reports"]![0]!.AsObject();

    static JsonArray Patch(int at, int length, string encoding, string source, string? key = null, int add = 0)
    {
        var p = new JsonObject { ["at"] = at, ["length"] = length, ["encoding"] = encoding, ["source"] = source };
        if (key != null) p["key"] = key;
        if (add != 0) p["add"] = add;
        return new JsonArray(p);
    }

    static void Refusals(Checks c)
    {
        var cases = new List<(string Name, JsonObject Json, string Path, string Reason)>();
        void Add(string name, Func<JsonObject> make, string path, string reason) => cases.Add((name, make(), path, reason));

        Add("steamFirmwareStamp, the removed key", () =>
        {
            var o = Triton();
            var e = Entry0(o);
            e.Remove("patch");
            e["steamFirmwareStamp"] = new JsonObject { ["offset"] = 19, ["key"] = "TRITON_FW_TS" };
            return o;
        }, "$.featureStubs.reports[0].steamFirmwareStamp", "patch");
        Add("a keyedHexFile source on a UMDF2 profile", () =>
        {
            var o = Pedals();
            o["valueSources"] = Triton()["valueSources"]!.DeepClone();
            return o;
        }, "$.valueSources.steamUpdater", "usbip backend only");
        Add("an encoding that is not one", () => { var o = Triton(); Entry0(o)["patch"] = Patch(19, 4, "uint32-be", "steamUpdater", "TRITON_FW_TS"); return o; },
            "$.featureStubs.reports[0].patch[0].encoding", "uint32-be");
        Add("a length its encoding does not write", () => { var o = Triton(); Entry0(o)["patch"] = Patch(19, 2, "uint32-le", "steamUpdater", "TRITON_FW_TS"); return o; },
            "$.featureStubs.reports[0].patch[0].length", "writes 4");
        Add("a patch past the data", () => { var o = Triton(); Entry0(o)["patch"] = Patch(26, 4, "uint32-le", "steamUpdater", "TRITON_FW_TS"); return o; },
            "$.featureStubs.reports[0].patch[0].at", "not inside");
        Add("a source nothing declares", () => { var o = Triton(); Entry0(o)["patch"] = Patch(19, 4, "uint32-le", "steamUpdatr", "TRITON_FW_TS"); return o; },
            "$.featureStubs.reports[0].patch[0].source", "steamUpdatr");
        Add("a keyedHexFile value with no key", () => { var o = Triton(); Entry0(o)["patch"] = Patch(19, 4, "uint32-le", "steamUpdater"); return o; },
            "$.featureStubs.reports[0].patch[0].key", "needs its key");
        Add("the serial as a number", () => { var o = Triton(); Entry0(o)["patch"] = Patch(19, 1, "uint8", "serial"); return o; },
            "$.featureStubs.reports[0].patch[0].encoding", "ascii");
        Add("the index as text", () => { var o = Triton(); Entry0(o)["patch"] = Patch(4, 3, "ascii", "index"); return o; },
            "$.featureStubs.reports[0].patch[0].encoding", "ascii-decimal");
        Add("a patch on an echoed answer", () =>
        {
            var o = Triton();
            var echo = o["featureStubs"]!["reports"]!.AsArray().First(r => r!["echo"]?.GetValue<bool>() == true)!.AsObject();
            echo["patch"] = Patch(0, 1, "uint8", "index");
            return o;
        }, "$.featureStubs.reports[6].patch[0]", "echoed");
        Add("a source named like a built-in", () =>
        {
            var o = Triton();
            o["valueSources"]!["index"] = o["valueSources"]!["steamUpdater"]!.DeepClone();
            return o;
        }, "$.valueSources.index", "built-in");
        // instanceSerial varies the answer's own captured text. Report 2 is
        // Triton's 0xAE index 1, the unit serial FXA9960200001 at bytes 4 to
        // 16 of 01 AE 14 01 <serial>.
        JsonObject Unit(JsonObject o) => o["featureStubs"]!["reports"]![2]!.AsObject();
        Add("an instance serial as a number", () => { var o = Triton(); Unit(o)["patch"] = Patch(4, 1, "uint8", "instanceSerial"); return o; },
            "$.featureStubs.reports[2].patch[0].encoding", "answer's own text");
        Add("an instance serial with an add", () => { var o = Triton(); Unit(o)["patch"] = Patch(4, 13, "ascii", "instanceSerial", add: 1); return o; },
            "$.featureStubs.reports[2].patch[0].add", "takes no add");
        Add("an instance serial with a key", () => { var o = Triton(); Unit(o)["patch"] = Patch(4, 13, "ascii", "instanceSerial", "TRITON_FW_TS"); return o; },
            "$.featureStubs.reports[2].patch[0].key", "only a keyedHexFile");
        Add("an instance serial over bytes that are not printable", () => { var o = Triton(); Unit(o)["patch"] = Patch(2, 5, "ascii", "instanceSerial"); return o; },
            "$.featureStubs.reports[2].patch[0].at", "printable ASCII");
        Add("an instance serial whose text ends in no digit", () => { var o = Triton(); Unit(o)["patch"] = Patch(4, 3, "ascii", "instanceSerial"); return o; },
            "$.featureStubs.reports[2].patch[0].length", "digit");
        Add("a source named like the instanceSerial built-in", () =>
        {
            var o = Triton();
            o["valueSources"]!["instanceSerial"] = o["valueSources"]!["steamUpdater"]!.DeepClone();
            return o;
        }, "$.valueSources.instanceSerial", "built-in");
        Add("a keyedHexFile with no root", () =>
        {
            var o = Triton();
            o["valueSources"]!["steamUpdater"]!["roots"] = new JsonArray();
            return o;
        }, "$.valueSources.steamUpdater.roots", "root");
        // A source's name is written into the path as System.Text.Json
        // writes a key. At f00f0482 a dot split it into two segments.
        Add("a keyedHexFile with no root, its name holding a dot", () =>
        {
            var o = Triton();
            var s = o["valueSources"]!["steamUpdater"]!.DeepClone().AsObject();
            s["roots"] = new JsonArray();
            o["valueSources"]!["steam.updater"] = s;
            return o;
        }, "$.valueSources['steam.updater'].roots", "root");

        string dir = Path.Combine(Path.GetTempPath(), $"hm-profile-values-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < cases.Count; i++)
                File.WriteAllText(Path.Combine(dir, $"case{i:D2}.json"), cases[i].Json.ToJsonString());
            using var ctx = new HMContext();
            ctx.LoadProfilesFromDirectory(dir);
            var issues = new List<Api.Issue>();
            c.Run("HMContext.ProfileLoadIssues reads", () => { issues.AddRange(Api.Issues(ctx, "ProfileLoadIssues")); return true; });
            for (int i = 0; i < cases.Count; i++)
            {
                var (name, json, path, reason) = cases[i];
                string file = Path.Combine(dir, $"case{i:D2}.json");
                var mine = issues.Where(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
                c.Check($"{name}: refused at {path}, naming {reason}", mine.Count == 1 && mine[0].Kind == "Refused"
                        && mine[0].Path == path && mine[0].Reason.Contains(reason, StringComparison.Ordinal),
                        string.Join("; ", mine.Select(x => $"{x.Path}: {x.Reason}")));
                c.Check($"{name}: the profile is not loaded", ctx.AllProfiles.Count == 0 || ctx.GetProfile(json["id"]!.GetValue<string>()) == null);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── The parser ───────────────────────────────────────────────────────

    /// <summary>SteamHardwareUpdater.ParseStamp at 9bd6279
    /// (sdk/HIDMaestro.Core/Internal/Usbip/SteamHardwareUpdater.cs:101-117),
    /// copied verbatim as the oracle the general parser must equal.</summary>
    static uint? ParseStamp9bd6279(string text, string key)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().SequenceEqual(key)) continue;

            var value = line.AsSpan(colon + 1).Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            return value.Length is > 0 and <= 8
                   && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint stamp)
                   && stamp != 0
                ? stamp : null;
        }
        return null;
    }

    static (bool, string) ParserMatches()
    {
        const string Shipped = "MUST_UPDATE_TRITON_FW_TS:6A18D057\r\nMUST_UPDATE_PROTEUS_FW_TS:6A18D053\r\n"
                             + "TRITON_FW_TS:6A628345\r\nPROTEUS_FW_TS:6A628359";
        var texts = new List<string>
        {
            Shipped, Shipped.Replace("\r\n", "\n"), "", ":6A628345", "TRITON_FW_TS 6A628345", "TRITON_FW_TS=6A628345",
            "TRITON_FW_TS:6A628345:7", "X_TRITON_FW_TS:5\nTRITON_FW_TS:6", "TRITON_FW_TS:zz\nTRITON_FW_TS:2",
        };
        // Generated: each key spelling, value form and surrounding.
        string[] keys = { "TRITON_FW_TS", "triton_fw_ts", "MUST_UPDATE_TRITON_FW_TS", "PROTEUS_FW_TS" };
        string[] values = { "6A628345", "6a628345", "0x6A628345", "0X6a628345", "0x", "", "1", "0", "00000000", "FFFFFFFF",
                            "0FFFFFFFF", "123456789", "zz", "-1", "+1", "6A62 8345", "\u00006A628345", "\t6A628345\t" };
        string[] pads = { "", " ", "  ", "\t" };
        foreach (string k in keys)
            foreach (string v in values)
                foreach (string pad in pads)
                {
                    texts.Add($"{pad}{k}{pad}:{pad}{v}{pad}");
                    texts.Add($"MUST_UPDATE_{k}:6A18D057\r\n{pad}{k}:{v}\r\n");
                }
        int compared = 0;
        foreach (string text in texts)
            foreach (string key in new[] { "TRITON_FW_TS", "PROTEUS_FW_TS", "NEREID_FW_TS", "triton_fw_ts", "MUST_UPDATE_TRITON_FW_TS" })
            {
                compared++;
                if (ValueSources.ParseKeyedHex(text, key) != ParseStamp9bd6279(text, key))
                    return (false, $"'{text.Replace("\r", "\\r").Replace("\n", "\\n")}' key {key}");
            }
        return (true, $"{compared} (text, key) pairs");
    }

    // ── Patches through a table ──────────────────────────────────────────

    static void Patches(Checks c)
    {
        var baseTriton = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "steam-controller-2");
        // The answer to Steam's message on feature report 1, as Steam asks.
        byte[]? Answer(ControllerProfile p, int index, string? serial, string? file, byte message = 0x83)
        {
            var t = FeatureStubTable.From(p, index, serial, _ => file);
            if (t == null) return null;
            t.OnSetFeature(1, new byte[] { 0x01, message, 0x00 });
            int n = t.Lookup(1, 64, out _);
            return n < 0 ? null : t.Answer(n).ToArray();
        }
        uint Stamp(byte[]? a) => a == null ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(19));

        c.Run("steam-controller-2: with Steam's config, bytes 19 to 22 carry its TRITON_FW_TS", () =>
        {
            var a = Answer(baseTriton, 0, null, "MUST_UPDATE_TRITON_FW_TS:6A18D057\r\nTRITON_FW_TS:6A628345\r\nPROTEUS_FW_TS:6A628359");
            return (Stamp(a) == 0x6A628345u, $"0x{Stamp(a):X8}");
        });
        c.Run("without the file, or without the key, the captured build stands", () =>
            Stamp(Answer(baseTriton, 0, null, null)) == 0x6A18D057u && Stamp(Answer(baseTriton, 0, null, "PROTEUS_FW_TS:1")) == 0x6A18D057u);
        c.Run("and every other byte is the capture's", () =>
        {
            var a = Answer(baseTriton, 0, null, "TRITON_FW_TS:6A628345")!;
            var b = Answer(baseTriton, 0, null, null)!;
            return Enumerable.Range(0, 64).All(i => (i >= 19 && i < 23) || a[i] == b[i]);
        });

        // A test persona: steam-controller-2 with an answer of every
        // encoding and source.
        var o = JsonNode.Parse(JsonSerializer.Serialize(baseTriton, HMJsonContext.Default.ControllerProfile))!.AsObject();
        o["id"] = "user-patches";
        var e = Entry0(o);
        e["data"] = "0183" + new string('0', 2 * 30);
        var patches = new JsonArray
        {
            new JsonObject { ["at"] = 2, ["length"] = 1, ["encoding"] = "uint8", ["source"] = "index" },
            new JsonObject { ["at"] = 3, ["length"] = 1, ["encoding"] = "uint8", ["source"] = "index", ["add"] = 0x30 },
            new JsonObject { ["at"] = 4, ["length"] = 2, ["encoding"] = "uint16-le", ["source"] = "index", ["add"] = 0x1000 },
            new JsonObject { ["at"] = 6, ["length"] = 8, ["encoding"] = "ascii", ["source"] = "serial" },
            new JsonObject { ["at"] = 14, ["length"] = 4, ["encoding"] = "ascii-decimal", ["source"] = "index" },
            new JsonObject { ["at"] = 18, ["length"] = 4, ["encoding"] = "uint32-le", ["source"] = "steamUpdater", ["key"] = "TRITON_FW_TS", ["add"] = 1 },
        };
        e["patch"] = patches;
        var p = JsonSerializer.Deserialize(o.ToJsonString(), HMJsonContext.Default.ControllerProfile)!;
        c.Run("every encoding and source writes its bytes, for index 15 and serial FXA996020000016", () =>
        {
            var a = Answer(p, 15, "FXA996020000016", "TRITON_FW_TS:0x41424344");
            if (a == null) return (false, "no answer");
            var want = new byte[64];
            want[0] = 0x01; want[1] = 0x83;
            want[2] = 15; want[3] = 0x3F;
            BinaryPrimitives.WriteUInt16LittleEndian(want.AsSpan(4), 0x100F);
            Encoding.ASCII.GetBytes("FXA99602").CopyTo(want, 6);
            Encoding.ASCII.GetBytes("15").CopyTo(want, 14);
            BinaryPrimitives.WriteUInt32LittleEndian(want.AsSpan(18), 0x41424345);
            return (a.AsSpan().SequenceEqual(want), Convert.ToHexString(a));
        });
        c.Run("a short serial is zero padded and an index of four digits fills its field", () =>
        {
            var a = Answer(p, 1234, "AB", null);
            return (a != null && a[2] == (byte)(1234 & 0xFF) && a[6] == (byte)'A' && a[7] == (byte)'B'
                    && a.AsSpan(8, 6).IndexOfAnyExcept((byte)0) < 0 && Encoding.ASCII.GetString(a, 14, 4) == "1234"
                    && BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(18)) == 0,
                    a == null ? "no answer" : Convert.ToHexString(a.AsSpan(0, 24)));
        });

        // instanceSerial (issue #72): the captured text, varied by the
        // serial variant the table is given, not by the index.
        string? UnitSerial(FeatureStubTable? t, byte channel)
        {
            if (t == null) return null;
            int at = channel == 0 ? 0 : 1;
            var set = new byte[at + 3];
            set[0] = channel;
            set[at] = 0xAE;
            set[at + 2] = 1;
            t.OnSetFeature(channel, set);
            int n = t.Lookup(channel, 64, out _);
            if (n < at + 3) return null;
            var a = t.Answer(n);
            int end = at + 3;
            while (end < a.Length && a[end] != 0) end++;
            return Encoding.ASCII.GetString(a[(at + 3)..end]);
        }
        c.Run("instanceSerial follows the variant it is given, not the index", () =>
        {
            string? a = UnitSerial(FeatureStubTable.From(baseTriton, 0, null, _ => null, 41), 1);
            string? b = UnitSerial(FeatureStubTable.From(baseTriton, 41, null, _ => null, 0), 1);
            string? d = UnitSerial(FeatureStubTable.From(baseTriton, 7, null, _ => null), 1);
            return (a == "FXA9960200042" && b == "FXA9960200001" && d == "FXA9960200008", $"{a} {b} {d}");
        });
        c.Run("instanceSerial carries across the digit run and keeps the text's length", () =>
        {
            string? a = UnitSerial(FeatureStubTable.From(baseTriton, 0, null, _ => null, 999_999), 1);
            return (a == "FXA9961200000", a ?? "stalled");
        });
        var deck = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "steam-deck-composite");
        c.Run("a keyed identity's Deck answers the unit serial its USB serial carries", () =>
        {
            var identity = DeviceIdentity.ForKey("profile-model-check-values", 0);
            var set = new UsbDescriptorSet(deck, 0, identity);
            string? unit = UnitSerial(FeatureStubTable.From(deck, 0, set.SerialString, _ => null, set.SerialVariant), 0);
            return (set.SerialVariant == identity.SerialVariant && identity.SerialVariant > 0
                    && unit == set.SerialString && unit != "FX1A00000001",
                    $"variant {identity.SerialVariant}, USB {set.SerialString}, 0xAE {unit}");
        });
        c.Run("the UMDF2 blob writes instanceSerial with the index as its variant, the rule HM-CTL-<index> follows", () =>
        {
            string b0 = Encoding.ASCII.GetString(DeviceOrchestrator.FeatureAnswersBlob(deck, 0));
            string b3 = Encoding.ASCII.GetString(DeviceOrchestrator.FeatureAnswersBlob(deck, 3));
            return (b0.Contains("FX1A00000001") && b0.Contains("MHDA00000001")
                    && b3.Contains("FX1A00000004") && b3.Contains("MHDA00000004") && !b3.Contains("FX1A00000001"), "");
        });
    }

    // ── The file cache ───────────────────────────────────────────────────

    static void FileCache(Checks c)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hm-profile-cache-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "updater.cfg");
        try
        {
            c.Run("a file read again only after it changes on disk: write time or length", () =>
            {
                File.WriteAllText(file, "TRITON_FW_TS:00000001");
                var t0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(file, t0);
                string? a = ValueSources.ReadPath(file);
                // Same length and write time: the cached text stands.
                File.WriteAllText(file, "TRITON_FW_TS:00000002");
                File.SetLastWriteTimeUtc(file, t0);
                string? b = ValueSources.ReadPath(file);
                File.SetLastWriteTimeUtc(file, t0.AddSeconds(1));
                string? d = ValueSources.ReadPath(file);
                File.WriteAllText(file, "TRITON_FW_TS:3");
                File.SetLastWriteTimeUtc(file, t0.AddSeconds(1));
                string? e = ValueSources.ReadPath(file);
                File.Delete(file);
                string? f = ValueSources.ReadPath(file);
                return (a == "TRITON_FW_TS:00000001" && b == a && d == "TRITON_FW_TS:00000002" && e == "TRITON_FW_TS:3" && f == null,
                        $"{a} {b} {d} {e} {f ?? "absent"}");
            });
            c.Run("a file written in the last two seconds is read again, whatever its write time and length", () =>
            {
                File.WriteAllText(file, "TRITON_FW_TS:0000000A");
                string? a = ValueSources.ReadPath(file);
                var t = File.GetLastWriteTimeUtc(file);
                File.WriteAllText(file, "TRITON_FW_TS:0000000B");
                File.SetLastWriteTimeUtc(file, t);
                string? b = ValueSources.ReadPath(file);
                return (a == "TRITON_FW_TS:0000000A" && b == "TRITON_FW_TS:0000000B", $"{a} {b}");
            });
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
