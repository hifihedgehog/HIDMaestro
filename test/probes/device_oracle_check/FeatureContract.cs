// feature-contract: the UMDF2 driver's FeatureAnswers under the version
// contract (the design note's mechanism 4 and decision (c)), natively.
//
// Both harnesses run scripts (native\harness\hm_script.c). The Sony matrix
// of case 5 (every UMDF2 Sony profile and the control, Bluetooth off and
// on, indexes 0, 1 and 15, every declared and Sony-coded feature ID at
// every buffer size from 0 to 65) runs on 9bd6279's driver with its
// configuration and Bluetooth value, and on the tree's driver at protocols
// 4 to 4 with the device node's identity tag and the FeatureAnswers the
// tree's SDK compiles for that index from the model the profile's identity
// gives it over that connection. The two must agree output for output, but
// for the outputs the contract adds: the version section's records, the
// instance ID query and where the hardware key opens. A mutation run, one
// byte of one answer changed, must not.
//
// Then the contract's cases, each held to what the design states and run
// on 9bd6279's driver too, which fails them:
//   - no section, a section of protocols 2 to 3 or 5 to 5, beside a
//     FeatureAnswers a killed program left: no GET is answered from it, each
//     Sony ID fails with STATUS_NOT_SUPPORTED and nothing is published
//   - protocols 4 to 4 with another token's tag: the start fails with
//     STATUS_DEVICE_CONFIGURATION_ERROR
//   - protocols 4 to 5 with the device node's tag: the blob answers
//   - the driver writes DriverProtocol 4 and DriverProtocolMin 4 at add
//   - the index before the first start: an identity entry naming index 1
//     and no hardware value gives index 1 and FunctionMode from Controller1
//   - an entry whose section carries another tag, with no hardware value,
//     fails the start and reads no v2.0.0 value
//   - a device node with no index or no Controller<index> key fails the
//     start in both drivers, and a key without a report descriptor the
//     HID driver's
//   - no entry and a hardware value whose section carries another tag fails
//     the start
//   - a blob truncated, of format 3 or past a limit, and no
//     CachedInputReportIds, fail the start with DriverConfigError naming it
//   - a DualSense-style channel (SET 0x80, then three pages on 0x81) and the
//     Deck's unnumbered channel as HID class hands it over, with no leading
//     0, its parameter at the offset the entry names
//   - a GET no entry answers goes on to the PID block
//   - an extended frame goes out only at its report's declared length
//   - with a cached-ID list, the polled GET_INPUT_REPORT keeps listed frames
//   - the key a dualsense-bt then an xbox-360-wired create leave on one
//     index answers no Sony byte
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
using static Case5Driver;

internal static class FeatureContract
{
    const uint NotSupported = 0xC00000BB, ConfigError = 0xC0000182, TooSmall = 0xC0000023;
    static readonly byte[] SonyIds = { 0x02, 0x05, 0x09, 0x12, 0x20, 0x22, 0xA3 };

    sealed class Checks
    {
        public int Failures;
        public void Check(string name, bool ok, string detail = "")
        {
            if (!ok) Failures++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] feature-contract: {name}{(detail.Length > 0 ? "  " + detail : "")}");
        }
        public void Info(string line) => Console.WriteLine($"    {line}");
    }

    public static int Run(string nativeDir, string goldenDir)
    {
        string tree = Path.Combine(nativeDir, "driver_harness_tree.exe"), oracle = Path.Combine(nativeDir, "driver_harness_oracle.exe");
        foreach (var exe in new[] { tree, oracle })
            if (!File.Exists(exe)) { Console.WriteLine($"  [FAIL] feature-contract: {exe} is not built"); return 1; }
        string work = Path.Combine(AppContext.BaseDirectory, "feature-contract", $"run-{Environment.ProcessId}");
        Directory.CreateDirectory(work);
        var c = new Checks();
        try
        {
            var catalog = ProfileDatabase.LoadEmbedded().All;
            var keptPlan = File.ReadAllText(Path.Combine(goldenDir, "case5-plan.txt"), Encoding.UTF8);
            Matrix(c, catalog, keptPlan, tree, oracle, work);
            Cases(c, catalog, tree, oracle, work);
            Unconfigured(c, catalog, tree, oracle, work);
            InputLengths(c, catalog, tree, oracle, work);
        }
        catch (Exception e)
        {
            c.Check("runs", false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"  feature-contract: {(c.Failures == 0 ? "PASS" : $"FAIL, {c.Failures}")}");
        return c.Failures;
    }

    /// <summary>The driver's own walk of each descriptor's input reports
    /// (driver.c BuildInputLengths) against the SDK's (HidReportMap's
    /// FrameBytes), on every UMDF2 catalog profile: an extended-lane frame of
    /// each input report at the SDK's length must go out at that length. A
    /// shape the two walks read differently drops every extended frame of
    /// that report on UMDF2. The DualShock 3 sixaxis form is left out: the
    /// driver reads its native report apart. So is the Switch Pro, whose
    /// input the driver's responder serves. A report longer than the
    /// extended lane's 80 bytes never travels it. At e34be99b the harness
    /// ran the walk on four profiles (case 5's extended-lengths).</summary>
    static void InputLengths(Checks c, IReadOnlyList<ControllerProfile> catalog, string tree, string oracle, string work)
    {
        // The Switch Pro identity's driver serves its input from the
        // protocol responder's stream, never from the extended lane
        // (driver.c SwitchFillLatestState), until DD-5 moves the responder
        // to data.
        var profiles = catalog.Where(p => !p.RequiresUsbipBackend && p.HasDescriptor && p.SixaxisCompatible != true
                                          && p.Resolved.IdentityEntry != "identity:nintendo-switch-pro")
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var config = Case5Driver.InstanceConfig(profiles, work);
        var plan = new StringBuilder();
        var script = new StringBuilder();
        // One stream a frame: the declared length must go out, and one byte
        // more must not, so a walk that read the report longer fails too.
        var asked = new List<(string Stream, string Profile, byte Id, int Length, bool Declared)>();
        foreach (var p in profiles)
        {
            plan.Append(Block("plain", p, config[p.Id], p.Id));
            var map = HidReportMap.Parse(p.GetDescriptorBytes()!);
            foreach (var r in map.Reports.Where(r => r.Kind == HidReportKind.Input).GroupBy(r => r.ReportId).Select(g => g.First()))
            {
                int len = r.FrameBytes;
                if (len <= 0 || len > 80) continue;
                byte first = map.UsesReportIds ? r.ReportId : (byte)0x5A;
                string Frame(int n) => string.Concat(Enumerable.Range(0, n).Select(i => i == 0 ? first.ToString("X2") : ((byte)(i * 7)).ToString("X2")));
                foreach (int n in len < 80 ? new[] { len, len + 1 } : new[] { len })
                {
                    string name = $"lengths/{p.Id}/{first:X2}/{n}";
                    script.Append($"stream {name}\ndevice {p.Id} 0 driver\nread {n}\nframe - {Frame(n)}\nremove\n");
                    asked.Add((name, p.Id, first, n, n == len));
                }
            }
        }
        var run = RunScript(tree, plan.ToString(), script.ToString(), work, "lengths");
        var dropped = new List<string>();
        var leaked = new List<string>();
        foreach (var (name, profile, id, n, declared) in asked)
        {
            var done = (run.TryGetValue(name, out var outs) ? Of(outs, "read") : new List<byte[]>()).Where(x => Status(x) == 0).ToList();
            if (declared && !(done.Count == 1 && Info(done[0]) == n && Body(done[0])[0] == id))
                dropped.Add($"{profile} 0x{id:X2} at {n}: {(done.Count == 0 ? "no read" : $"{Info(done[0])} bytes")}");
            if (!declared && done.Count != 0)
                leaked.Add($"{profile} 0x{id:X2} at {n}: {Info(done[0])} bytes went out");
        }
        int declaredCount = asked.Count(a => a.Declared);
        c.Check($"each UMDF2 catalog profile's input reports, {declaredCount} on {profiles.Count} profiles, go out on the extended lane at the length the SDK reads",
                dropped.Count == 0, dropped.Count == 0 ? "" : $"{dropped.Count} dropped: {string.Join("; ", dropped.Take(8))}");
        c.Check($"and a frame one byte longer, {asked.Count - declaredCount} of them, goes out for none", leaked.Count == 0,
                leaked.Count == 0 ? "" : $"{leaked.Count} went out: {string.Join("; ", leaked.Take(8))}");
        // The control: 9bd6279's driver had no length bound and sends the
        // longer frames, so the check above tells a bound from none.
        var o = RunScript(oracle, plan.ToString(), script.ToString(), work, "lengths-oracle");
        int oracleLeaks = asked.Count(a => !a.Declared && o.TryGetValue(a.Stream, out var outs) && Of(outs, "read").Any(x => Status(x) == 0));
        c.Check("9bd6279's driver, which has no bound, sends the longer frames, so the check fails without one", oracleLeaks > 0,
                $"{oracleLeaks} of {asked.Count - declaredCount} went out");
    }

    // ── plans and scripts ────────────────────────────────────────────────

    /// <summary>The values a plan holds for each profile, by name.</summary>
    static Dictionary<string, List<RegValue>> PlanValues(string plan)
    {
        var map = new Dictionary<string, List<RegValue>>(StringComparer.Ordinal);
        List<RegValue>? cur = null;
        foreach (var raw in plan.Split('\n'))
        {
            var w = raw.TrimEnd('\r').Split(' ');
            if (w[0] == "profile") cur = map.TryAdd(w[2], new()) ? map[w[2]] : null;
            else if (w[0] == "value" && cur != null)
                cur.Add(new RegValue(w[1], int.Parse(w[2]), w[3] == "-" ? Array.Empty<byte>() : Convert.FromHexString(w[3])));
            else if (w[0] == "end") cur = null;
        }
        return map;
    }

    static List<RegValue> With(List<RegValue> values, string name, int kind, byte[]? data)
    {
        var list = values.Where(v => v.Name != name).ToList();
        if (data != null) list.Add(new RegValue(name, kind, data));
        return list;
    }

    const int RegBinary = 3, RegDword = 4;

    static string Block(string group, ControllerProfile p, List<RegValue> values, string name, params string[] flags)
    {
        var sb = new StringBuilder();
        AddProfile(sb, group, p, values, name);
        string text = sb.ToString();
        return flags.Length == 0 ? text : text[..^"end\n".Length] + string.Concat(flags.Select(f => f + "\n")) + "end\n";
    }

    /// <summary>The harness on a plan and a script, its streams read whole.</summary>
    static Dictionary<string, List<(string Tag, byte[] Data)>> RunScript(string exe, string plan, string script, string work, string label)
    {
        string planFile = Path.Combine(work, label + ".plan"), scriptFile = Path.Combine(work, label + ".script"), output = Path.Combine(work, label + ".bin");
        File.WriteAllText(planFile, "hm-driver-plan 1\n" + plan, new UTF8Encoding(false));
        File.WriteAllText(scriptFile, "hm-driver-script 1\n" + script, new UTF8Encoding(false));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in new[] { "--script", planFile, scriptFile, output }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}: {err.Result.Trim()}");
        var streams = new Dictionary<string, List<(string, byte[])>>(StringComparer.Ordinal);
        using var br = new BinaryReader(File.OpenRead(output), Encoding.ASCII);
        List<(string, byte[])>? cur = null;
        for (;;)
        {
            int kind = br.BaseStream.ReadByte();
            if (kind == 'S') { string name = Encoding.ASCII.GetString(br.ReadBytes(br.ReadUInt16())); streams[name] = cur = new(); }
            else if (kind == 'O')
            {
                string tag = Encoding.ASCII.GetString(br.ReadBytes(br.ReadByte()));
                cur!.Add((tag, br.ReadBytes(checked((int)br.ReadUInt32()))));
            }
            else if (kind == 'N') { br.ReadBytes(br.ReadByte()); br.ReadBytes(checked((int)br.ReadUInt32())); }
            else if (kind == 'E') break;
            else throw new InvalidDataException($"{output}: record kind {kind}");
        }
        return streams;
    }

    /// <summary>The IDs case 5's Sony matrix asks, in its order
    /// (hm_case_sony.c CollectIds): the Sony-coded IDs, then every other
    /// declared feature ID and report 0, ascending.</summary>
    static List<byte> MatrixIds(ControllerProfile p)
    {
        var map = p.GetOrBuildReportMap();
        var rest = new SortedSet<byte>(map.Reports.Where(r => r.Kind == HidReportKind.Feature).Select(r => r.ReportId)) { 0 };
        foreach (var id in SonyIds) rest.Remove(id);
        return SonyIds.Concat(rest).ToList();
    }

    static string GetFeature(ControllerProfile p, byte id, int size) =>
        $"getfeature {(p.GetOrBuildReportMap().UsesReportIds ? id : (byte)0):X2} {size} {id:X2}\n";

    /// <summary>A copy of a catalog profile that takes the model its
    /// identity gives it over a connection, as one made outside the catalog
    /// does.</summary>
    static ControllerProfile OverConnection(ControllerProfile p, bool bluetooth)
    {
        var copy = p.Clone();
        copy.Connection = bluetooth ? "bluetooth" : "usb";
        copy.DeviceModelFrom = null;
        copy.ResolvedCache = ModelRegistry.Resolve(copy, ModelRegistry.Catalog, outsideCatalog: true);
        return copy;
    }

    // Outputs the contract adds or moves, left out of the comparison.
    static readonly HashSet<string> Bookkeeping = new(StringComparer.Ordinal)
        { "section.version", "section.ack", "fdo.queryproperty", "registry.open" };

    static List<(string Tag, byte[] Data)> Compared(List<(string Tag, byte[] Data)> outs) =>
        outs.Where(o => !Bookkeeping.Contains(o.Tag)).ToList();

    // ── the Sony matrix ──────────────────────────────────────────────────

    static void Matrix(Checks c, IReadOnlyList<ControllerProfile> catalog, string keptPlan, string tree, string oracle, string work)
    {
        var kept = PlanValues(keptPlan);
        var sony = catalog.Where(p => p.VendorId == 0x054C && !p.RequiresUsbipBackend).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var control = catalog.First(p => p.Id == "heusinkveld-ultimate-pedals");
        var treeConfig = InstanceConfig(sony.Append(control).ToList(), work);
        var oPlan = new StringBuilder();
        var tPlan = new StringBuilder();
        var script = new StringBuilder();
        var tScript = new StringBuilder();
        var subjects = sony.Select(p => (p, Group: "sony")).Append((control, Group: "control")).ToList();
        string? mutated = null;
        foreach (var (p, group) in subjects)
        {
            if (!kept.TryGetValue(p.Id, out var keptValues)) throw new InvalidOperationException($"the kept plan has no {p.Id}");
            foreach (int bt in group == "control" ? new[] { 0 } : new[] { 0, 1 })
            {
                oPlan.Append(Block(group, p, With(keptValues, "Bluetooth", RegDword, BitConverter.GetBytes(bt)), $"{p.Id}#bt{bt}"));
                var variant = group == "control" ? p : OverConnection(p, bt == 1);
                foreach (int index in group == "control" ? new[] { 0 } : new[] { 0, 1, 15 })
                {
                    byte[] blob = DeviceOrchestrator.FeatureAnswersBlob(variant, index);
                    string name = $"{p.Id}#bt{bt}#idx{index}";
                    tPlan.Append(Block(group, p, With(treeConfig[p.Id], "FeatureAnswers", RegBinary, blob), name));
                    string stream = group == "control" ? $"sony/{p.Id}/control-nopid-idx{index}" : $"sony/{p.Id}/bt{bt}-idx{index}";
                    var body = new StringBuilder();
                    foreach (byte id in MatrixIds(p))
                        for (int size = 0; size <= 65; size++) body.Append(GetFeature(p, id, size));
                    body.Append("remove\n");
                    script.Append($"stream {stream}\ndevice {p.Id}#bt{bt} {index} driver\n").Append(body);
                    tScript.Append($"stream {stream}\nversion tag-own\ndevice {name} {index} driver\n").Append(body);
                    if (p.Id == "dualsense" && bt == 0 && index == 0)
                    {
                        // The mutation: the first byte of the last page in
                        // the arena, 0xA3's last byte, flipped.
                        var bad = (byte[])blob.Clone();
                        bad[^1] ^= 0xFF;
                        mutated = Block(group, p, With(treeConfig[p.Id], "FeatureAnswers", RegBinary, bad), name)
                            + $"\n#stream {stream}\nversion tag-own\ndevice {name} {index} driver\n" + body;
                    }
                }
            }
        }
        var o = RunScript(oracle, oPlan.ToString(), script.ToString(), work, "matrix-oracle");
        var t = RunScript(tree, tPlan.ToString(), tScript.ToString(), work, "matrix-tree");
        int same = 0, outputs = 0;
        var differ = new List<string>();
        // A descriptor that declares no feature report has every GET_FEATURE
        // refused by HidClass before a driver sees it (commit 1c126ed), so
        // 9bd6279's answers there, which the harness reaches through the
        // caller's first buffer byte, are no device's. The tree's driver
        // answers none of them.
        var unreachable = subjects.Where(x => !x.Item1.GetOrBuildReportMap().Reports.Any(r => r.Kind == HidReportKind.Feature))
            .Select(x => x.Item1.Id).ToHashSet(StringComparer.Ordinal);
        var refusedAll = new List<string>();
        foreach (var (name, os) in o)
        {
            if (!t.TryGetValue(name, out var ts)) { differ.Add($"{name}: not in the tree's run"); continue; }
            if (unreachable.Contains(name.Split('/')[1]))
            {
                var g = Of(ts, "getfeature");
                if (g.All(x => Status(x) == NotSupported || (Status(x) == TooSmall && Info(x) == 0)) && Of(ts, "ring").Count == 0)
                    refusedAll.Add(name);
                else differ.Add($"{name}: the tree answers a GET on a descriptor with no feature report");
                continue;
            }
            var a = Compared(os);
            var b = Compared(ts);
            int at = Enumerable.Range(0, Math.Min(a.Count, b.Count)).FirstOrDefault(i => a[i].Tag != b[i].Tag || !a[i].Data.AsSpan().SequenceEqual(b[i].Data), -1);
            int opensA = os.Count(x => x.Tag == "registry.open"), opensB = ts.Count(x => x.Tag == "registry.open");
            if (at < 0 && a.Count == b.Count && opensA == opensB) { same++; outputs += a.Count; continue; }
            differ.Add(at >= 0 ? $"{name}: output {at} {a[at].Tag} {Convert.ToHexString(a[at].Data)} against {b[at].Tag} {Convert.ToHexString(b[at].Data)}"
                               : $"{name}: {a.Count} outputs against {b.Count}, hardware key opened {opensA} and {opensB} times");
        }
        foreach (var d in differ.Take(8)) c.Info(d);
        c.Check($"9bd6279's Sony table and the tree's FeatureAnswers under the contract agree on every stream of the matrix whose descriptor declares a feature report",
                differ.Count == 0 && o.Count == t.Count && o.Count == subjects.Sum(s => s.Group == "control" ? 1 : 6) && same > 0,
                $"{same} identical, {outputs} outputs compared, {differ.Count} differ");
        c.Check($"on {string.Join(" and ", unreachable.OrderBy(x => x, StringComparer.Ordinal))}, whose descriptors declare no feature report, the tree answers no GET and publishes nothing",
                refusedAll.Count == unreachable.Count * 6, $"{refusedAll.Count} streams");

        // The mutation must show.
        var parts = mutated!.Split("\n#", 2);
        var m = RunScript(tree, parts[0], parts[1], work, "matrix-mutation");
        var (mn, ms) = m.Single();
        var ok = Compared(o[mn]);
        var mm = Compared(ms);
        int firstBad = Enumerable.Range(0, Math.Min(ok.Count, mm.Count)).FirstOrDefault(i => !ok[i].Data.AsSpan().SequenceEqual(mm[i].Data), -1);
        c.Check("one answer byte changed in dualsense's blob: the comparison fails", firstBad >= 0,
                firstBad >= 0 ? $"{mn} output {firstBad} differs" : "no difference");
    }

    // ── the contract's cases ─────────────────────────────────────────────

    static uint Status(byte[] completion) => BinaryPrimitives.ReadUInt32LittleEndian(completion.AsSpan(4));
    static uint Info(byte[] completion) => BinaryPrimitives.ReadUInt32LittleEndian(completion.AsSpan(8));
    static ReadOnlySpan<byte> Body(byte[] completion) => completion.AsSpan(12);

    static List<byte[]> Of(List<(string Tag, byte[] Data)> s, string tag) => s.Where(x => x.Tag == tag).Select(x => x.Data).ToList();

    static uint Added(List<(string Tag, byte[] Data)> s) => BinaryPrimitives.ReadUInt32LittleEndian(Of(s, "device.add")[0]);

    /// <summary>A profile loaded from JSON the check writes, as a program's
    /// own file loads.</summary>
    static ControllerProfile Load(string work, JsonObject json)
    {
        string dir = Path.Combine(work, "profiles-" + json["id"]!.GetValue<string>());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "p.json"), json.ToJsonString());
        using var ctx = new HMContext();
        ctx.LoadProfilesFromDirectory(dir);
        var issue = ctx.ProfileLoadIssues.FirstOrDefault();
        return ctx.GetProfile(json["id"]!.GetValue<string>())?.Inner
            ?? throw new InvalidOperationException($"{json["id"]} did not load: {issue?.JsonPath} {issue?.Reason}");
    }

    static JsonObject Repo(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Program.ProbeDir(), "..", "..", "..", "profiles", file)),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();

    /// <summary>FeatureAnswers with its header's length written again.</summary>
    static byte[] Sized(byte[] blob)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)blob.Length);
        return blob;
    }

    /// <summary>A blob of 65 plain one-byte answers, one past the driver's
    /// entries.</summary>
    static byte[] PastEntries()
    {
        const int n = 65;
        var blob = new byte[16 + n * 16 + n];
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), n);
        for (int i = 0; i < n; i++)
        {
            var e = blob.AsSpan(16 + i * 16, 16);
            e[0] = 0x05; e[7] = 1; e[8] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], (ushort)i);
        }
        return Sized(blob);
    }

    static void Cases(Checks c, IReadOnlyList<ControllerProfile> catalog, string tree, string oracle, string work)
    {
        var ds = catalog.First(p => p.Id == "dualsense-bt");
        var xbox = catalog.First(p => p.Id == "xbox-360-wired");
        var config = InstanceConfig(new List<ControllerProfile> { ds, xbox }, work);
        var dsValues = config[ds.Id];
        byte[] dsBlob = DeviceOrchestrator.FeatureAnswersBlob(ds, 1);
        var vIdx1 = With(With(dsValues, "FeatureAnswers", RegBinary, dsBlob), "FunctionMode", RegDword, BitConverter.GetBytes(1));
        byte[] format3 = (byte[])dsBlob.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(format3.AsSpan(4), 3);

        // The test channel and the Deck's table (Answers.cs in
        // profile_model_check holds the same shapes on the SDK side).
        var chan = Repo(@"sony\dualsense.json");
        chan["id"] = "test-channel";
        chan.Remove("deviceModelFrom");
        string Page(byte status, byte fill) => "810104" + status.ToString("X2") + string.Concat(Enumerable.Repeat(fill.ToString("X2"), 4));
        chan["featureStubs"] = new JsonObject
        {
            ["reports"] = new JsonArray(
                new JsonObject { ["id"] = "0x05", ["size"] = 41, ["minLength"] = 41, ["data"] = "05AABB", ["notify"] = true },
                new JsonObject
                {
                    ["id"] = "0x81", ["size"] = 64,
                    ["message"] = new JsonObject { ["onReport"] = "0x80", ["at"] = 1, ["value"] = "0104" },
                    ["pages"] = new JsonArray(Page(3, 0x11), Page(3, 0x22), Page(2, 0x33)),
                }),
        };
        var channel = Load(work, chan);
        var deckJson = Repo(@"valve\steam-deck-composite.json");
        var deck = new JsonObject
        {
            ["id"] = "test-deck-channel", ["name"] = "Deck channel test", ["vendor"] = "Valve", ["vid"] = "0x28DE", ["pid"] = "0x1205",
            ["type"] = "gamepad", ["descriptor"] = deckJson["descriptor"]!.GetValue<string>(),
            ["featureStubs"] = deckJson["featureStubs"]!.DeepClone(),
        };
        var deckProfile = Load(work, deck);
        var chanConfig = InstanceConfig(new List<ControllerProfile> { channel, deckProfile }, work);

        // The key a dualsense-bt create and then an xbox-360-wired create
        // leave on one index.
        var after = KeyAfter(new[] { ds, xbox }, work);

        var plan = new StringBuilder()
            .Append(Block("sony", ds, dsValues, "ds"))
            .Append(Block("sony", ds, With(dsValues, "FeatureAnswers", RegBinary, DeviceOrchestrator.FeatureAnswersBlob(ds, 0)), "ds0"))
            .Append(Block("sony", ds, vIdx1, "ds-nohw", "nohwindex"))
            .Append(Block("sony", ds, vIdx1, "ds-noid", "noidentity"))
            .Append(Block("sony", ds, With(dsValues, "FeatureAnswers", RegBinary, Sized(dsBlob[..^1])), "ds-truncated"))
            .Append(Block("sony", ds, With(dsValues, "FeatureAnswers", RegBinary, format3), "ds-format3"))
            .Append(Block("sony", ds, With(dsValues, "FeatureAnswers", RegBinary, PastEntries()), "ds-past"))
            .Append(Block("sony", ds, With(dsValues, "CachedInputReportIds", RegBinary, null), "ds-nocached"))
            .Append(Block("sony", ds, With(dsValues, "CachedInputReportIds", RegBinary, new byte[] { 0x31 }), "ds-cached31"))
            .Append(Block("sony", channel, chanConfig[channel.Id], "channel"))
            .Append(Block("steam", deckProfile, chanConfig[deckProfile.Id], "deck"))
            .Append(Block("xusb", xbox, after, "xbox-after-ds"))
            .ToString();
        var s = new StringBuilder();
        string Asks(params byte[] ids) => string.Concat(ids.Select(id => $"getfeature {id:X2} 64 {id:X2}\n"));
        foreach (var v in new[] { "absent", "max3", "range5" })
            s.Append($"stream stale-{v}\nversion {v}\ndevice ds 0 driver\n").Append(Asks(SonyIds)).Append("remove\n");
        s.Append("stream tag-other\nversion tag-other\ndevice ds 0 driver\n");
        s.Append("stream range45\nversion range45\ndevice ds0 0 driver\n").Append(Asks(0x05)).Append("remove\n");
        s.Append("stream index1-no-hardware-value\nversion tag-own\ndevice ds-nohw 1 driver\n").Append(Asks(0x09)).Append("remove\n");
        s.Append("stream entry-other-tag\nversion tag-other\ndevice ds-nohw 1 driver\n");
        s.Append("stream no-entry-other-tag\nversion tag-other\ndevice ds-noid 1 driver\n");
        foreach (var bad in new[] { "ds-truncated", "ds-format3", "ds-past", "ds-nocached" })
            s.Append($"stream blob-{bad}\nversion tag-own\ndevice {bad} 0 driver\n");
        s.Append("stream channel\nversion tag-own\ndevice channel 0 driver\n")
         .Append("getfeature 81 64 81\nsetfeature 800104\n").Append(Asks(0x81, 0x81, 0x81, 0x81))
         .Append("setfeature 800104\n").Append(Asks(0x81)).Append("setfeature 800999\n").Append(Asks(0x81, 0x05)).Append("remove\n");
        s.Append("stream deck\nversion tag-own\ndevice deck 0 driver\n")
         .Append("setfeature 8300\ngetfeature 00 64 00\nsetfeature AE1401\ngetfeature 00 64 00\nsetfeature AE14FF\ngetfeature 00 64 00\nremove\n");
        string Ext(byte id, int len) => string.Concat(Enumerable.Range(0, len).Select(i => i == 0 ? id.ToString("X2") : ((byte)(i * 5)).ToString("X2")));
        s.Append("stream extended\nversion tag-own\ndevice ds0 0 driver\n")
         .Append($"read 78\nframe - {Ext(0x31, 78)}\nread 78\nframe - {Ext(0x31, 77)}\nframe - {Ext(0x32, 78)}\nremove\n");
        s.Append("stream cached\nversion tag-own\ndevice ds-cached31 0 driver\n")
         .Append($"frame 0102030405060708 -\ngetinput 01 78 01\nframe - {Ext(0x31, 78)}\ngetinput 01 78 01\nframe 0102030405060708 -\ngetinput 01 78 01\nremove\n");
        s.Append("stream xbox-after-ds\nversion tag-own\ndevice xbox-after-ds 0 driver\n").Append(Asks(0x05, 0x09, 0x20)).Append("remove\n");
        // A program that starts again on the index while the device stays:
        // the worker idles into a recycle, and the new program writes two
        // frames before the worker's next turn, the second with the SeqNo
        // the old program's last frame carried.
        s.Append("stream recycle-same-seq\nversion tag-own\ndevice ds0 0 driver\n")
         .Append("read 78\nframe 01A1000000000000 -\nread 78\nframe 01A2000000000000 -\nidle 8\nsdkrestart\n")
         .Append("read 78\nframequeued 01B1000000000000 -\nframe 01B2000000000000 -\ngetinput 01 78 01\nremove\n");
        // The same with no recycle between: the old program's owner pipe
        // closes and the new program starts at once, inside the idle window.
        s.Append("stream exit-same-seq\nversion tag-own\ndevice ds0 0 driver\n")
         .Append("read 78\nframe 01A1000000000000 -\nread 78\nframe 01A2000000000000 -\nsdkexit\nsdkrestart\n")
         .Append("read 78\nframequeued 01B1000000000000 -\nframe 01B2000000000000 -\ngetinput 01 78 01\nremove\n");

        var t = RunScript(tree, plan, s.ToString(), work, "cases-tree");
        var o = RunScript(oracle, plan, s.ToString(), work, "cases-oracle");
        int oracleFails = 0;
        foreach (var (label, run) in new[] { ("tree", t), ("9bd6279", o) })
        {
            bool isTree = label == "tree";
            var results = Expectations(run);
            foreach (var (name, ok, detail) in results)
            {
                if (isTree) c.Check(name, ok, detail);
                else if (!ok) oracleFails++;
            }
            if (!isTree)
                c.Check($"9bd6279's driver fails {oracleFails} of these {results.Count} cases", oracleFails > 0,
                        string.Join("; ", results.Where(r => r.Ok).Select(r => r.Name)) is { Length: > 0 } passed ? $"it passes: {passed}" : "");
        }
    }

    /// <summary>A device node without its own configuration (the design
    /// note's defects table): with no index, neither an identity entry nor a
    /// ControllerIndex in its hardware key, or with no Controller&lt;index&gt;
    /// key, both drivers fail the start with
    /// STATUS_DEVICE_CONFIGURATION_ERROR, where 9bd6279's read Controller0,
    /// which another controller owns, or started as an Xbox 360 pad on
    /// defaults, or read SOFTWARE\HIDMaestro. A key without a report
    /// descriptor fails the HID driver's start too, since no default one is
    /// left. The configured devices beside them start, in the same
    /// script.</summary>
    static void Unconfigured(Checks c, IReadOnlyList<ControllerProfile> catalog, string tree, string oracle, string work)
    {
        var ds = catalog.First(p => p.Id == "dualsense-bt");
        var xbox = catalog.First(p => p.Id == "xbox-360-wired");
        var config = InstanceConfig(new List<ControllerProfile> { ds, xbox }, work);
        var dsValues = config[ds.Id];
        var xboxValues = config[xbox.Id];
        var plan = new StringBuilder()
            .Append(Block("sony", ds, dsValues, "ds"))
            .Append(Block("sony", ds, dsValues, "ds-noindex", "nohwindex", "noidentity"))
            .Append(Block("sony", ds, dsValues, "ds-nokey", "nokey"))
            .Append(Block("sony", ds, With(dsValues, "FunctionMode", RegDword, BitConverter.GetBytes(1)), "ds-parent", "nokey", "parentkey"))
            .Append(Block("sony", ds, With(dsValues, "ReportDescriptor", RegBinary, null), "ds-nodescriptor"))
            .Append(Block("xusb", xbox, xboxValues, "xbox"))
            .Append(Block("xusb", xbox, xboxValues, "xbox-noindex", "nohwindex", "noidentity"))
            .Append(Block("xusb", xbox, xboxValues, "xbox-nokey", "nokey"))
            .Append(Block("xusb", xbox, xboxValues, "xbox-parent", "nokey", "parentkey"))
            .ToString();
        // IOCTL_HID_GET_DEVICE_ATTRIBUTES, HID_CTL_CODE(9) (hidport.h:190).
        const string Attributes = "ioctl 0x000B0027 - 32\n";
        var s = new StringBuilder();
        // A v1 program's device nodes (no version section), then the same
        // under the tree's SDK, which writes the contract.
        foreach (var v in new[] { "absent", "sdk" })
        {
            s.Append($"stream configured-{v}\nversion {v}\ndevice ds 2 driver\n").Append(Attributes).Append("remove\n");
            s.Append($"stream companion-configured-{v}\nversion {v}\ndevice xbox 2 companion\nremove\n");
            s.Append($"stream no-index-{v}\nversion {v}\ndevice ds-noindex 0 driver\n");
            s.Append($"stream companion-no-index-{v}\nversion {v}\ndevice xbox-noindex 0 companion\n");
            s.Append($"stream no-key-{v}\nversion {v}\ndevice ds-nokey 2 driver\n");
            s.Append($"stream companion-no-key-{v}\nversion {v}\ndevice xbox-nokey 2 companion\n");
        }
        s.Append("stream parent-key\nversion absent\ndevice ds-parent 2 driver\n");
        s.Append("stream companion-parent-key\nversion absent\ndevice xbox-parent 2 companion\n");
        s.Append("stream no-descriptor\nversion absent\ndevice ds-nodescriptor 2 driver\n");

        var t = RunScript(tree, plan, s.ToString(), work, "unconfigured-tree");
        var o = RunScript(oracle, plan, s.ToString(), work, "unconfigured-oracle");
        int oracleFails = 0;
        foreach (var (label, run) in new[] { ("tree", t), ("9bd6279", o) })
        {
            bool isTree = label == "tree";
            var results = UnconfiguredExpectations(run, ds);
            foreach (var (name, ok, detail) in results)
            {
                if (isTree) c.Check(name, ok, detail);
                else if (!ok) oracleFails++;
            }
            if (!isTree)
                c.Check($"9bd6279's driver fails {oracleFails} of these {results.Count} cases", oracleFails > 0,
                        string.Join("; ", results.Where(r => r.Ok).Select(r => r.Name)) is { Length: > 0 } passed ? $"it passes: {passed}" : "");
        }
    }

    static List<(string Name, bool Ok, string Detail)> UnconfiguredExpectations(Dictionary<string, List<(string Tag, byte[] Data)>> r, ControllerProfile ds)
    {
        var res = new List<(string, bool, string)>();
        void Add(string name, Func<(bool, string)> f)
        {
            try { var (ok, d) = f(); res.Add((name, ok, d)); }
            catch (Exception e) { res.Add((name, false, $"{e.GetType().Name}: {e.Message}")); }
        }
        string Pid(string stream)
        {
            var a = Of(r[stream], "ioctl")[0];
            return Status(a) == 0 ? $"{BinaryPrimitives.ReadUInt16LittleEndian(Body(a)[4..]):X4}:{BinaryPrimitives.ReadUInt16LittleEndian(Body(a)[6..]):X4}" : Status(a).ToString("X8");
        }
        string want = $"{ds.VendorId:X4}:{ds.ProductId:X4}";
        foreach (var (v, under) in new[] { ("absent", "a v1 program's device node"), ("sdk", "under the contract") })
        {
            Add($"{under}, configured at index 2: the driver starts with the profile's attributes {want}", () =>
                (Added(r[$"configured-{v}"]) == 0 && Pid($"configured-{v}") == want, $"add {Added(r[$"configured-{v}"]):X8}, attributes {Pid($"configured-{v}")}"));
            Add($"{under}, configured at index 2: the companion starts", () =>
                (Added(r[$"companion-configured-{v}"]) == 0, Added(r[$"companion-configured-{v}"]).ToString("X8")));
            Add($"{under}, no index beside Controller0: the driver's start fails with STATUS_DEVICE_CONFIGURATION_ERROR",
                () => (Added(r[$"no-index-{v}"]) == ConfigError, Added(r[$"no-index-{v}"]).ToString("X8")));
            Add($"{under}, no index beside Controller0: the companion's start fails with STATUS_DEVICE_CONFIGURATION_ERROR",
                () => (Added(r[$"companion-no-index-{v}"]) == ConfigError, Added(r[$"companion-no-index-{v}"]).ToString("X8")));
            Add($"{under}, index 2 and no Controller2: the driver's start fails with STATUS_DEVICE_CONFIGURATION_ERROR",
                () => (Added(r[$"no-key-{v}"]) == ConfigError, Added(r[$"no-key-{v}"]).ToString("X8")));
            Add($"{under}, index 2 and no Controller2: the companion's start fails with STATUS_DEVICE_CONFIGURATION_ERROR",
                () => (Added(r[$"companion-no-key-{v}"]) == ConfigError, Added(r[$"companion-no-key-{v}"]).ToString("X8")));
        }
        Add("the values and FunctionMode 1 in SOFTWARE\\HIDMaestro and no Controller2: the driver's start fails",
            () => (Added(r["parent-key"]) == ConfigError, Added(r["parent-key"]).ToString("X8")));
        Add("the values in SOFTWARE\\HIDMaestro and no Controller2: the companion's start fails",
            () => (Added(r["companion-parent-key"]) == ConfigError, Added(r["companion-parent-key"]).ToString("X8")));
        Add("Controller2 without ReportDescriptor: the start fails, with no default descriptor to fall back on",
            () => (Added(r["no-descriptor"]) == ConfigError, Added(r["no-descriptor"]).ToString("X8")));
        return res;
    }

    /// <summary>What each case of the script must show.</summary>
    static List<(string Name, bool Ok, string Detail)> Expectations(Dictionary<string, List<(string Tag, byte[] Data)>> r)
    {
        var res = new List<(string, bool, string)>();
        void Add(string name, Func<(bool, string)> f)
        {
            try { var (ok, d) = f(); res.Add((name, ok, d)); }
            catch (Exception e) { res.Add((name, false, $"{e.GetType().Name}: {e.Message}")); }
        }
        foreach (var v in new[] { "absent", "max3", "range5" })
            Add($"a section {v switch { "absent" => "absent", "max3" => "of protocols 2 to 3", _ => "of protocols 5 to 5" }} beside a left FeatureAnswers: every Sony ID fails with STATUS_NOT_SUPPORTED and nothing is published", () =>
            {
                var g = Of(r[$"stale-{v}"], "getfeature");
                bool ok = g.Count == SonyIds.Length && g.All(x => Status(x) == NotSupported) && Of(r[$"stale-{v}"], "ring").Count == 0;
                return (ok, string.Join(" ", g.Select(x => Status(x).ToString("X8"))));
            });
        Add("protocols 4 to 4 with another token's tag: the start fails with STATUS_DEVICE_CONFIGURATION_ERROR",
            () => (Added(r["tag-other"]) == ConfigError, Added(r["tag-other"]).ToString("X8")));
        Add("protocols 4 to 5 with the device node's tag: report 0x05 answers from the blob, 41 bytes", () =>
        {
            var g = Of(r["range45"], "getfeature").Single();
            return (Status(g) == 0 && Info(g) == 41 && Body(g)[0] == 0x05, $"{Status(g):X8} {Info(g)}");
        });
        Add("the driver writes DriverProtocol 4 and DriverProtocolMin 4 at add", () =>
        {
            var v = Of(r["range45"], "section.version")[0];
            int proto = BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(20)), min = BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(36));
            return (proto == 4 && min == 4, $"{proto} {min}");
        });
        Add("an identity entry naming index 1 and no hardware value: index 1 at the first add, its MAC byte 1, FunctionMode from Controller1", () =>
        {
            var s = r["index1-no-hardware-value"];
            var g = Of(s, "getfeature").Single();
            return (Added(s) == 0 && Status(g) == 0 && Body(g)[6] == 1 && Of(s, "fdo.filter").Count == 0,
                    $"add {Added(s):X8}, 0x09 {Status(g):X8} byte 6 {(Status(g) == 0 ? Body(g)[6] : -1)}, filter {Of(s, "fdo.filter").Count}");
        });
        // The entry is a killed program's, so the device node is a v1
        // program's, and its hardware key holds no index: it has no index of
        // its own and fails its start. Through 928d03b6 it read index 0 and
        // started on Controller0, no key of its own.
        Add("an identity entry whose section carries another token's tag, and no hardware value: the start fails and reads no v2.0.0 value", () =>
        {
            var s = r["entry-other-tag"];
            return (Added(s) == ConfigError && Of(s, "getfeature").Count == 0, $"add {Added(s):X8}");
        });
        Add("no identity entry and a hardware value whose section carries another tag: the start fails",
            () => (Added(r["no-entry-other-tag"]) == ConfigError, Added(r["no-entry-other-tag"]).ToString("X8")));
        foreach (var (bad, error) in new[] { ("ds-truncated", 1), ("ds-format3", 1), ("ds-past", 1), ("ds-nocached", 2) })
            Add($"{bad}: the start fails with STATUS_DEVICE_CONFIGURATION_ERROR and DriverConfigError {error}", () =>
            {
                var s = r[$"blob-{bad}"];
                var v = Of(s, "section.version")[0];
                int word = BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(56));
                return (Added(s) == ConfigError && word == error, $"add {Added(s):X8}, error word {word}");
            });
        Add("the channel: GET 0x81 before a SET goes to the PID block and fails, SET 0x80 01 04 then three pages and the last again, a SET restarts them, another action fails, 0x05 still answers", () =>
        {
            var g = Of(r["channel"], "getfeature");
            string Show(byte[] x) => Status(x) == 0 ? Convert.ToHexString(Body(x)[..5]) : Status(x).ToString("X8");
            var shown = g.Select(Show).ToList();
            bool ok = Status(g[0]) == NotSupported && shown[1] == "8101040311" && shown[2] == "8101040322" && shown[3] == "8101040233"
                   && shown[4] == "8101040233" && shown[5] == "8101040311" && Status(g[6]) == NotSupported && Status(g[7]) == 0 && Body(g[7])[0] == 0x05;
            return (ok, string.Join(" ", shown));
        });
        // HID class hands a minidriver an unnumbered report without the
        // caller's leading 0, in both directions (Wine 2df1ee2 hidclass.sys
        // device.c:485, vhidmini2 197ba21 vhidmini.c:765-767): SDL's
        // [00 83 00 ...] arrives as [83 00 ...], and a string request
        // [00 AE len index] as [AE len index], its index at byte 2 as the
        // Deck's table says. At 807a3d4a the driver dropped the first byte
        // of that buffer, so 0x83 at byte 0 never matched.
        Add("the Deck's unnumbered channel as HID class hands it over: 0x83 answers, and 0xAE answers by its index at byte 2", () =>
        {
            var g = Of(r["deck"], "getfeature");
            string Show(byte[] x) => Status(x) == 0 ? Convert.ToHexString(Body(x)[..4]) : Status(x).ToString("X8");
            bool ok = Status(g[0]) == 0 && Body(g[0])[0] == 0x83 && Body(g[0])[1] == 0x2D
                   && Status(g[1]) == 0 && Body(g[1])[2] == 0x01 && Status(g[2]) == 0 && Body(g[2])[2] == 0xFF;
            return (ok, string.Join(" ", g.Select(Show)));
        });
        // The recycle forgets the last SeqNo: the new program's second frame
        // carries the SeqNo the old program's last did, and is new data.
        // At efe5b20a the recycle reset SharedMemSeqNo while the gate read
        // SeenSeqNo, so that frame was skipped and its read stayed pending.
        Add("a recycle after a program starts again on the index forgets the last SeqNo: the frame that repeats it is read", () =>
        {
            var all = Of(r["recycle-same-seq"], "read");
            string Show(byte[] x) => Status(x) == 0 ? Convert.ToHexString(Body(x)[..Math.Min(4, Body(x).Length)]) : Status(x).ToString("X8");
            var g = Of(r["recycle-same-seq"], "getinput");
            bool Holds(byte[] x, byte b) => Status(x) == 0 && Body(x).IndexOf(b) is >= 0 and < 4;
            bool ok = all.Count == 3 && Holds(all[0], 0xA1) && Holds(all[1], 0xA2) && Holds(all[2], 0xB2)
                      && g.Count == 1 && Holds(g[0], 0xB2);
            return (ok, $"reads {string.Join(" ", all.Select(Show))}, polled {string.Join(" ", g.Select(Show))}");
        });
        // A program that ends, and one that starts on the index before any
        // recycle: the owner pipe's close forgets the last SeqNo. At
        // 811f796e only the recycle did, so the new program's frame that
        // repeated the old last SeqNo was skipped.
        Add("a program's end forgets the last SeqNo with no recycle: the next program's frame that repeats it is read", () =>
        {
            var all = Of(r["exit-same-seq"], "read");
            string Show(byte[] x) => Status(x) == 0 ? Convert.ToHexString(Body(x)[..Math.Min(4, Body(x).Length)]) : Status(x).ToString("X8");
            var g = Of(r["exit-same-seq"], "getinput");
            bool Holds(byte[] x, byte b) => Status(x) == 0 && Body(x).IndexOf(b) is >= 0 and < 4;
            bool ok = all.Count == 3 && Holds(all[0], 0xA1) && Holds(all[1], 0xA2) && Holds(all[2], 0xB2)
                      && g.Count == 1 && Holds(g[0], 0xB2);
            return (ok, $"reads {string.Join(" ", all.Select(Show))}, polled {string.Join(" ", g.Select(Show))}");
        });
        Add("an extended frame goes out only at its report's declared length: 0x31 at 78 does, 0x31 at 77 and 0x32 at 78 do not", () =>
        {
            var reads = Of(r["extended"], "read").Where(x => Status(x) == 0).ToList();
            bool ok = reads.Count == 1 && Info(reads[0]) == 78 && Body(reads[0])[0] == 0x31;
            return (ok, string.Join(" ", reads.Select(x => $"{Info(x)}:{(Info(x) > 0 ? Body(x)[0].ToString("X2") : "-")}")));
        });
        Add("with CachedInputReportIds 31 the polled GET_INPUT_REPORT answers the last 0x31 frame, never a 0x01 frame", () =>
        {
            var g = Of(r["cached"], "getinput");
            bool ok = g.Count == 3 && Status(g[0]) == NotSupported && Status(g[1]) == 0 && Body(g[1])[0] == 0x31
                   && Status(g[2]) == 0 && Body(g[2])[0] == 0x31;
            return (ok, string.Join(" ", g.Select(x => Status(x) == 0 ? Body(x)[0].ToString("X2") : Status(x).ToString("X8"))));
        });
        Add("the key dualsense-bt then xbox-360-wired leave on one index answers 0x05, 0x09 and 0x20 with no Sony byte", () =>
        {
            var g = Of(r["xbox-after-ds"], "getfeature");
            return (g.Count == 3 && g.All(x => Status(x) == NotSupported), string.Join(" ", g.Select(x => Status(x).ToString("X8"))));
        });
        return res;
    }

    // ── the key two creates leave ────────────────────────────────────────

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKeyW(string file, out IntPtr key, int samDesired, int options, int reserved);
    [DllImport("advapi32.dll")]
    static extern int RegOverridePredefKey(IntPtr key, IntPtr newKey);
    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr key);

    /// <summary>Controller0's values after WriteInstanceConfig runs for each
    /// profile in turn on index 0, in a hive of the check's own, as case 5
    /// runs it (Case5Driver.InstanceConfig).</summary>
    static List<RegValue> KeyAfter(ControllerProfile[] profiles, string work)
    {
        int rc = RegLoadAppKeyW(Path.Combine(work, "after.hive"), out IntPtr appKey, 0xF003F, 0, 0);
        if (rc != 0) throw new InvalidOperationException($"RegLoadAppKeyW failed with {rc}");
        try
        {
            if ((rc = RegOverridePredefKey(unchecked((IntPtr)(int)0x80000002), appKey)) != 0)
                throw new InvalidOperationException($"RegOverridePredefKey failed with {rc}");
            try
            {
                using (var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (machine != null) throw new InvalidOperationException("HKLM is not redirected: nothing was written");
                var write = typeof(DeviceOrchestrator).GetMethod("WriteInstanceConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                foreach (var p in profiles)
                {
                    string type = p.Type;
                    p.Type = "keyboard";
                    try { write.Invoke(null, new object[] { 0, p, DeviceIdentity.ForIndex(0) }); }
                    finally { p.Type = type; }
                }
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro\Controller0")!;
                var values = new List<RegValue>();
                foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
                {
                    var kind = key.GetValueKind(name);
                    object v = key.GetValue(name)!;
                    values.Add(kind switch
                    {
                        Microsoft.Win32.RegistryValueKind.DWord => new RegValue(name, RegDword, BitConverter.GetBytes((int)v)),
                        Microsoft.Win32.RegistryValueKind.Binary => new RegValue(name, RegBinary, (byte[])v),
                        Microsoft.Win32.RegistryValueKind.String => new RegValue(name, 1, Encoding.Unicode.GetBytes((string)v + "\0")),
                        _ => throw new InvalidOperationException($"{name} is {kind}"),
                    });
                }
                return values;
            }
            finally { RegOverridePredefKey(unchecked((IntPtr)(int)0x80000002), IntPtr.Zero); }
        }
        finally { RegCloseKey(appKey); }
    }
}
