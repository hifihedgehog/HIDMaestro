using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

/// <summary>The catalog's programs: every layout, decoder, live feature and
/// emitted report of the embedded profiles, bound to its generated kernel
/// through the hash computed at run time, three ways on random states and on
/// the inputs DD-0's recordings hold, and the binder's fallback and key-order
/// rules. A catalog profile added as JSON joins every part of this with no
/// code.</summary>
internal static unsafe class CatalogChecks
{
    static void Fail(string text) => Checks.Fail(text);

    public static int Run()
    {
        int before = Checks.Failures;
        var data = new Random(ProbeCorpusSeed.Value ^ 0xCA7A);
        Console.WriteLine("== Catalog: every program the embedded profiles hold, bound through the hash computed at run time, three ways on 100,000 random states each");
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var setKeys = new HashSet<string>(StringComparer.Ordinal);
        var models = new List<(ControllerProfile profile, CompiledModel model)>();
        foreach (var p in ProfileDatabase.LoadEmbedded().All)
        {
            CompiledModel m;
            try { m = p.GetOrCompileModel(); }
            catch (FormException e) { Fail($"{p.Id}: refused at {e.Path}: {e.Reason}"); continue; }
            var programs = m.AllPrograms.ToList();
            if (programs.Count == 0) continue;
            models.Add((p, m));
            // The naive evaluator reads each program from the profile's JSON
            // as its author wrote it.
            var authored = new NaiveJson(Root(p.Id));
            foreach (var (v, naive, _) in authored.Pair(m).GroupBy(x => x.Program.Hash).Select(g => g.First()))
            {
                hashes.Add(v.Hash);
                var k = ProgramBinder.Kernel(v.Hash);
                if (k == null) { Fail($"{p.Id} {v.Spec.Name}: hash {v.Hash[..16]} binds no generated kernel"); continue; }
                int d = Checks.Compare(v, 100000, data, out var first, kernel: k, naive: naive);
                if (d == 0) Console.WriteLine($"{p.Id} {v.Spec.Name}: hash {v.Hash[..16]}, bound to its generated kernel, 0 of 100000 differ");
                else Fail($"{p.Id} {v.Spec.Name}: {d} of 100000 differ, first {first}");
            }
            if (ProgramBinder.BindLayouts(m) is { } runner)
            {
                setKeys.Add(m.LayoutSet!.Key);
                if (!runner.Generated) Fail($"{p.Id}: its layout set binds the interpreter, not its generated set");
                else RunnerAgainstNaive(p.Id, m, authored, runner, data, 20000);
            }
            var decoders = ProgramBinder.BindDecoders(m);
            for (int i = 0; i < decoders.Length; i++)
                if (!decoders[i].Generated) Fail($"{p.Id} decoder {decoders[i].Form.Name}: binds the interpreter, not its generated kernel");
        }
        // The kernels the build generated are the programs the run time
        // reads, no more and no fewer.
        var built = CatalogKernels.ByHash.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var h in built.Except(hashes)) Fail($"the build generated kernel {h[..16]}, which no embedded profile's program has");
        foreach (var h in hashes.Except(built)) Fail($"program {h[..16]} of an embedded profile has no kernel from the build");
        if (CatalogKernels.SetCount != setKeys.Count) Fail($"the build generated {CatalogKernels.SetCount} layout sets, the embedded profiles have {setKeys.Count}");
        Console.WriteLine($"{models.Count} catalog profiles with programs, {hashes.Count} distinct programs, {setKeys.Count} layout sets; the build generated {built.Count} kernels and {CatalogKernels.SetCount} sets");

        Oracle(models, data);
        Binder(models, data);
        return Checks.Failures - before;
    }

    /// <summary>The device's layouts through the bound runner, the way the
    /// device clock and a GET_REPORT run them, against the naive evaluator,
    /// each layout on <paramref name="inputs"/> random states.</summary>
    static void RunnerAgainstNaive(string id, CompiledModel m, NaiveJson authored, LayoutRunner runner, Random data, int inputs)
    {
        var naive = authored.Pair(m).Take(m.Layouts.Length).Select(x => x.Naive).ToArray();
        var s = new byte[m.StateSize + Checks.StatePad];
        var a = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
        var regs = new uint[Checks.Registers]; var ctr = new uint[Checks.Counters];
        for (int layout = 0; layout < m.Layouts.Length; layout++)
        {
            var v = m.Layouts[layout].Program;
            int diff = 0; string? first = null;
            for (int n = 0; n < inputs; n++)
            {
                data.NextBytes(s);
                Checks.DrawRegisters(data, regs, ctr, n);
                Array.Fill(a, (byte)0xA5); Array.Fill(c, (byte)0xC3);
                fixed (byte* ps = s, pa = a)
                fixed (uint* pr = regs, pc = ctr)
                    runner.Run(layout, ps, pa, pr, pc);
                naive[layout](s, c, regs, ctr);
                if (!a.AsSpan(0, v.Spec.Size).SequenceEqual(c.AsSpan(0, v.Spec.Size)))
                {
                    diff++;
                    first ??= $"state {Convert.ToHexString(s, 0, m.StateSize)}: runner {Convert.ToHexString(a, 0, v.Spec.Size)} naive {Convert.ToHexString(c, 0, v.Spec.Size)}";
                }
            }
            if (diff == 0) Console.WriteLine($"{id} layout {m.Layouts[layout].Name}: the {(runner.Generated ? "generated" : "interpreted")} set and the naive evaluator agree on {inputs} states");
            else Fail($"{id} layout {m.Layouts[layout].Name}: the set and the naive evaluator differ on {diff} of {inputs}, first {first}");
        }
    }

    /// <summary>The directory of DD-0's golden files, found up the tree from
    /// the probe, or null on a machine that has only the probe's build.</summary>
    static string? GoldenDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string g = Path.Combine(dir.FullName, "test", "probes", "device_oracle_check", "golden");
            if (File.Exists(Path.Combine(g, "SHA256SUMS"))) return g;
            g = Path.Combine(dir.FullName, "device_oracle_check", "golden");
            if (File.Exists(Path.Combine(g, "SHA256SUMS"))) return g;
        }
        return null;
    }

    /// <summary>Every output a golden file records, by stream: the stream's
    /// name, the output's tag and its bytes. The text's SHA-256 must be the
    /// one its manifest lists, as device_oracle_check reads it
    /// (Golden.ReadVerified).</summary>
    static IEnumerable<(string stream, string tag, byte[] data)> Golden(string dir, string caseName)
    {
        string file = caseName + ".txt.gz";
        var sums = File.ReadAllLines(Path.Combine(dir, "SHA256SUMS"))
            .Select(l => l.Split("  ", 2)).Where(x => x.Length == 2).ToDictionary(x => x[1].Trim(), x => x[0].Trim(), StringComparer.Ordinal);
        using var ms = new MemoryStream();
        using (var z = new GZipStream(File.OpenRead(Path.Combine(dir, file)), CompressionMode.Decompress)) z.CopyTo(ms);
        byte[] raw = ms.ToArray();
        if (!sums.TryGetValue(file, out var want) || !string.Equals(want, Convert.ToHexString(SHA256.HashData(raw)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{file} is not the file SHA256SUMS lists");
        string stream = "";
        foreach (var line in Encoding.ASCII.GetString(raw).Split('\n'))
        {
            if (line.StartsWith("stream ", StringComparison.Ordinal)) { stream = line[7..]; continue; }
            if (stream.Length == 0 || line.Length == 0 || line == "end" || line.StartsWith("count ", StringComparison.Ordinal) || line.StartsWith("sha256 ", StringComparison.Ordinal)) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string hex = line[(sp + 1)..];
            yield return (stream, line[..sp], hex == "-" ? Array.Empty<byte>() : Convert.FromHexString(hex));
        }
    }

    /// <summary>DD-0's recorded inputs three ways: every state block the
    /// codec and the device recordings hold (case 2's bodies, case 3's), run
    /// through each layout of every catalog model whose state block is that
    /// size, under the model's initial registers and random ones, and every
    /// recorded output report through each decoder of the report's ID.</summary>
    static void Oracle(List<(ControllerProfile profile, CompiledModel model)> models, Random data)
    {
        Console.WriteLine("== DD-0's recorded inputs, three ways");
        string? dir = GoldenDir();
        if (dir == null)
        {
            Console.WriteLine("DD-0's golden files are not beside the probe; S89 replays the same inputs where they are");
            return;
        }
        var bodies = new List<byte[]>();
        var rumble = new List<byte[]>();
        foreach (var (stream, tag, bytes) in Golden(dir, "case2-packers").Concat(Golden(dir, "case3-switch2-device")))
        {
            if (stream.StartsWith("switch2", StringComparison.Ordinal) && (tag == "body" || tag == "all" || tag.StartsWith("slot", StringComparison.Ordinal)))
                bodies.Add(bytes);
            // Case 2's decode rows: the 32 data bytes after report 0x02's ID,
            // then the two levels the packer decoded.
            if (stream.StartsWith("switch2-pro/rumble/", StringComparison.Ordinal) && bytes.Length == 34)
                rumble.Add(bytes[..32]);
        }
        Console.WriteLine($"{bodies.Count} recorded state blocks, {rumble.Count} recorded rumble reports");
        if (bodies.Count == 0 || rumble.Count == 0) Fail("the golden files hold no Switch 2 state block or rumble report, so this check reads nothing");
        var a = new byte[ProgramForm.MaxReport]; var b = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
        var regs = new uint[Checks.Registers]; var ctr = new uint[Checks.Counters];
        foreach (var (p, m) in models)
        {
            var sized = bodies.Where(x => x.Length == m.StateSize).ToList();
            var authored = new NaiveJson(Root(p.Id)).Pair(m).ToList();
            for (int li = 0; li < m.Layouts.Length; li++)
            {
                var layout = m.Layouts[li];
                if (sized.Count == 0) break;
                var v = layout.Program;
                var naive = authored[li].Naive;
                var kernel = ProgramBinder.Kernel(v.Hash);
                if (kernel == null) continue;
                var interp = new ReportProgram(v);
                var s = new byte[m.StateSize + Checks.StatePad];
                int runs = 0, diff = 0; string? first = null;
                foreach (var body in sized)
                    for (int r = 0; r < 4; r++)
                    {
                        Array.Clear(s);
                        body.CopyTo(s, 0);
                        if (r == 0) { Array.Clear(regs); m.Registers.Initial.CopyTo(regs, 0); Array.Clear(ctr); }
                        else Checks.DrawRegisters(data, regs, ctr, r);
                        runs++;
                        if (!Checks.Agree(v, kernel, interp, s, a, b, c, regs, ctr, Checks.PreFill, ref first, naive)) diff++;
                    }
                if (diff == 0) Console.WriteLine($"{p.Id} layout {layout.Name}: {sized.Count} recorded state blocks under 4 register sets, 0 of {runs} differ");
                else Fail($"{p.Id} layout {layout.Name}: {diff} of {runs} recorded inputs differ, first {first}");
            }
            foreach (var dec in m.Decoders)
            {
                var v = dec.Program;
                var naive = authored.First(x => x.What == $"decoder {dec.Name}").Naive;
                var kernel = ProgramBinder.Kernel(v.Hash);
                if (kernel == null) continue;
                var interp = new ReportProgram(v);
                int runs = 0, diff = 0; string? first = null;
                foreach (byte id in dec.Reports)
                    foreach (var row in rumble)
                    {
                        if (1 + row.Length < dec.MinLength) continue;
                        var s = new byte[Math.Max(v.Spec.StateSize, 1 + row.Length) + Checks.StatePad];
                        s[0] = id;
                        row.CopyTo(s, 1);
                        runs++;
                        if (!Checks.Agree(v, kernel, interp, s, a, b, c, regs, ctr, Checks.PreFill, ref first, naive)) diff++;
                    }
                if (runs == 0) continue;
                if (diff == 0) Console.WriteLine($"{p.Id} decoder {dec.Name}: {runs} recorded reports, 0 differ");
                else Fail($"{p.Id} decoder {dec.Name}: {diff} of {runs} recorded reports differ, first {first}");
            }
        }
    }

    /// <summary>The embedded profile file of <paramref name="id"/> as JSON.</summary>
    static JsonObject Root(string id)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var st = asm.GetManifestResourceStream(name)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            if (JsonNode.Parse(sr.ReadToEnd(), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
                && (string?)o["id"] == id) return o;
        }
        throw new InvalidOperationException($"no embedded profile {id}");
    }

    /// <summary>Each embedded profile file whose model has layouts, as JSON:
    /// the profile's id and the file's text.</summary>
    static IEnumerable<(string id, string json)> EmbeddedJson()
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var st = asm.GetManifestResourceStream(name)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string text = sr.ReadToEnd();
            JsonNode? node;
            try { node = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { continue; }
            if (node is JsonObject o && o["inputReports"] != null && o["id"] is JsonValue idv && idv.TryGetValue(out string? id) && id != null)
                yield return (id, text);
        }
    }

    static CompiledModel Compile(string json) =>
        (ControllerProfile.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidDataException("the edited profile parsed to nothing")).GetOrCompileModel();

    /// <summary>Every object's properties in the reverse order, recursively.</summary>
    static void ReverseKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var props = o.ToList();
                o.Clear();
                for (int i = props.Count - 1; i >= 0; i--) { ReverseKeys(props[i].Value); o[props[i].Key] = props[i].Value; }
                break;
            }
            case JsonArray arr:
                foreach (var x in arr) ReverseKeys(x);
                break;
        }
    }

    /// <summary>The binder: a copy whose JSON differs only in key order binds
    /// the same generated set, and a copy whose program is edited, with the
    /// same bytes but another canonical form, falls back to the interpreter
    /// and gives the bytes the generated set gives.</summary>
    static void Binder(List<(ControllerProfile profile, CompiledModel model)> models, Random data)
    {
        Console.WriteLine("== The binder: key order binds the same kernels, an edited program falls back to the interpreter with the same bytes");
        int profiles = 0;
        foreach (var (id, json) in EmbeddedJson())
        {
            var original = models.FirstOrDefault(x => x.profile.Id == id).model;
            if (original?.LayoutSet == null) continue;
            profiles++;
            var generated = ProgramBinder.BindLayouts(original)!;

            var reordered = JsonNode.Parse(json)!;
            ReverseKeys(reordered);
            var rm = Compile(reordered.ToJsonString());
            var rr = ProgramBinder.BindLayouts(rm)!;
            bool sameKey = rm.LayoutSet!.Key == original.LayoutSet.Key;
            bool decodersBound = ProgramBinder.BindDecoders(rm).All(d => d.Generated);
            if (sameKey && rr.Generated && decodersBound) Console.WriteLine($"{id}: every key in reverse order, the same set key, bound to the generated set, decoders generated");
            else Fail($"{id}: with its keys reversed the set key {(sameKey ? "is the same" : "changed")}, the set is {(rr.Generated ? "generated" : "interpreted")}, decoders {(decodersBound ? "generated" : "not all generated")}");

            // The edit: an unconditional copy of two or more bytes split in
            // two, which writes the same bytes as another op list.
            var edited = JsonNode.Parse(json)!;
            JsonObject? split = null; int at = -1;
            var layouts = edited["inputReports"]!.AsArray();
            foreach (var layout in layouts)
            {
                var program = layout!["program"]!.AsArray();
                for (int i = 0; i < program.Count && split == null; i++)
                {
                    if (program[i] is not JsonObject op || op["copy"] is not JsonValue cv || op["mask"] != null || !cv.TryGetValue(out string? range)) continue;
                    var ends = range!.Split('-');
                    if (ends.Length != 2 || !int.TryParse(ends[0], out int lo) || !int.TryParse(ends[1], out int hi) || hi <= lo) continue;
                    int to = op["to"]!.GetValue<int>();
                    split = op; at = i;
                    program[i] = new JsonObject { ["copy"] = $"{lo}", ["to"] = to };
                    program.Insert(i + 1, new JsonObject { ["copy"] = $"{lo + 1}-{hi}", ["to"] = to + 1 });
                }
                if (split != null) break;
            }
            if (split == null) { Console.WriteLine($"{id}: no unconditional copy of two or more bytes to split, so the fallback is shown by the user profile check"); continue; }
            var em = Compile(edited.ToJsonString());
            var er = ProgramBinder.BindLayouts(em)!;
            if (em.LayoutSet!.Key == original.LayoutSet.Key) { Fail($"{id}: the split copy kept the set key, so the edit does not test the fallback"); continue; }
            if (er.Generated) { Fail($"{id}: the edited copy binds a generated set"); continue; }
            var s = new byte[original.StateSize + Checks.StatePad];
            var a = new byte[ProgramForm.MaxReport]; var b = new byte[ProgramForm.MaxReport];
            var regs = new uint[Checks.Registers]; var ctr = new uint[Checks.Counters];
            int diff = 0, runs = 0;
            for (int layout = 0; layout < original.Layouts.Length; layout++)
                for (int n = 0; n < 20000; n++)
                {
                    data.NextBytes(s);
                    Checks.DrawRegisters(data, regs, ctr, n);
                    Array.Fill(a, (byte)0xA5); Array.Fill(b, (byte)0x5A);
                    fixed (byte* ps = s, pa = a, pb = b)
                    fixed (uint* pr = regs, pc = ctr)
                    {
                        generated.Run(layout, ps, pa, pr, pc);
                        er.Run(layout, ps, pb, pr, pc);
                    }
                    runs++;
                    int size = original.Layouts[layout].Size;
                    if (!a.AsSpan(0, size).SequenceEqual(b.AsSpan(0, size))) diff++;
                }
            if (diff == 0) Console.WriteLine($"{id}: a copy split in two changes the set key and binds the interpreter, which gives the generated set's bytes on {runs} inputs");
            else Fail($"{id}: the edited copy on the interpreter differs from the generated set on {diff} of {runs} inputs");
        }
        if (profiles == 0) Fail("no embedded profile has layouts, so the binder is not tested");
    }
}
