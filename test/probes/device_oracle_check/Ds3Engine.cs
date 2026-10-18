// ds3-engine: the DualShock 3's sixaxis form as profile data, against the
// form 9bd6279's driver ran (design note, decision (b)).
//
// dualshock-3-full.json builds the 12-byte joystick report and the report
// 0 feature from the native 49-byte report SubmitState fills, and folds
// the sixaxis.sys output commands into the native output report 0x01.
// 9bd6279's driver did all three itself (Ds3BuildSixaxisInput,
// Ds3BuildSixaxisFeature and Ds3HandleSixaxisOutput, driver.c:129-215 and
// 1273-1324), so the sequences below run through both:
// driver_harness_oracle.exe in the native harness's script mode on case
// 5's plan, and the engine on the catalog profile. The states are case 5's
// scripted ones (each button bit, every d-pad nibble, stick and pressure
// edges, motion words past 10 bits, each byte flipped) and 10,000 random
// reports. After each the read's 12 bytes and a GET of feature 0 at its 49
// must match. The commands are 1, 2, 3, 9 and 10, every LED mode and 2,000
// random ones, each at the 48 bytes HID class hands the driver for the
// descriptor's one output report, and the report each raises must match
// the one the driver published.
//
// Case 5's own DualShock 3 streams also write commands of other lengths,
// the 0x00-led 49-byte form, frames of other sizes and reads and GETs at
// other lengths. HID class hands a driver none of them, so they are not
// here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

internal static class Ds3Engine
{
    const int Native = 49, Command = 48, Input = 12;

    static int s_failures;
    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    sealed class Submit : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public readonly byte[] Buffer = new byte[64];
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet => Engine.BuildOnSubmit(ref set, Buffer);
    }

    /// <summary>What each sequence expects back, in the order the
    /// responder records it: a read's bytes, a feature's, a ring slot's.</summary>
    sealed class Sim
    {
        readonly ProtocolEngine _e;
        readonly LayoutRunner _r;
        readonly Submit _s;
        public readonly StringBuilder Script = new();
        public readonly List<byte[]> Reads = new(), Features = new(), Ring = new();

        public Sim(ControllerProfile p, CompiledProtocol proto, string name)
        {
            _r = ProgramBinder.BindLayouts(proto.Model)!;
            _e = new ProtocolEngine(proto, p.SerialString, 0, _r);
            _e.SetStateBlock(p.StateBlockAtRest());
            _s = new Submit { Engine = _e };
            Script.Append($"stream ds3-engine/{name}\ndevice dualshock-3-full 0 driver\n");
        }

        /// <summary>A native report as SubmitState writes it, the read HID
        /// class keeps parked, then a GET of feature 0.</summary>
        public void State(byte[] raw)
        {
            Script.Append($"read {Input}\nframe - {Convert.ToHexString(raw)}\ngetfeature 00 {Native} -\n");
            _e.SetStateBlock(raw);
            int n = _r.Visit(_s);
            Reads.Add(_s.Buffer[..n]);
            var f = new byte[Native];
            int m = _e.BuildLiveFeature(0x00, f);
            Features.Add(f[..m]);
        }

        public void Write(byte[] cmd, bool setOutput)
        {
            Script.Append($"{(setOutput ? "setoutput" : "write")} {Convert.ToHexString(cmd)}\n");
            var r = _e.HandleCommand(cmd, 0);
            var dst = new byte[64];
            Ring.Add(r.Emit < 0 ? cmd : dst[.._e.BuildEmit(r.Emit, cmd, dst)]);
        }
    }

    static byte[] Neutral()
    {
        var raw = new byte[Native];
        raw[0] = 0x01;
        raw[6] = raw[7] = raw[8] = raw[9] = 0x80;
        raw[41] = raw[43] = raw[45] = raw[47] = 0x02;
        return raw;
    }

    static List<(string Name, Sim Sim)> Sequences(ControllerProfile p, CompiledProtocol proto, ulong seed)
    {
        var list = new List<(string, Sim)>();
        var rng = new Rng(seed, 0x5E03);
        Sim New(string name) { var s = new Sim(p, proto, name); list.Add((name, s)); return s; }

        var sc = New("states-scripted");
        sc.State(Neutral());
        for (int b = 2; b <= 4; b++)
            for (int bit = 0; bit < 8; bit++) { var raw = Neutral(); raw[b] = (byte)(1 << bit); sc.State(raw); }
        for (int nib = 0; nib < 16; nib++) { var raw = Neutral(); raw[2] = (byte)(nib << 4); sc.State(raw); }
        foreach (int b in Enumerable.Range(6, 20).Where(b => b < 10 || b > 13))
            foreach (byte e in new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF }) { var raw = Neutral(); raw[b] = e; sc.State(raw); }
        foreach (int w in new[] { 41, 43, 45, 47 })
            foreach (ushort v in new ushort[] { 0x0000, 0x0001, 0x01FF, 0x0200, 0x0201, 0x03FE, 0x03FF, 0x0400, 0x0401, 0x07FF, 0x0800, 0x8000, 0xFC00, 0xFFFE, 0xFFFF })
            { var raw = Neutral(); raw[w] = (byte)(v >> 8); raw[w + 1] = (byte)v; sc.State(raw); }
        for (int b = 0; b < Native; b++) { var raw = Neutral(); raw[b] ^= 0xA5; sc.State(raw); }

        var rs = New("states-random");
        for (int i = 0; i < 10000; i++) { var raw = new byte[Native]; rng.Fill(raw); rs.State(raw); }

        var cmds = new byte[] { 1, 2, 3, 9, 10 };
        foreach (bool via in new[] { false, true })
        {
            var s = New($"output-{(via ? "setoutput" : "write")}");
            foreach (byte c in cmds)
                for (int i = 0; i < 8; i++) { var buf = new byte[Command]; rng.Fill(buf); buf[0] = c; s.Write(buf, via); }
            for (int m = 0; m < 16; m++)
            {
                var buf = new byte[Command];
                buf[0] = 1;
                for (int b = 0; b < 4; b++) buf[4 + b] = (byte)(((m >> b) & 1) != 0 ? 1 + b % 2 : 0);
                s.Write(buf, via);
            }
            for (int i = 0; i < 2000; i++)
            {
                var buf = new byte[Command];
                rng.Fill(buf);
                if (rng.Bool()) buf[0] = cmds[rng.Next(cmds.Length)];
                s.Write(buf, via);
            }
        }
        return list;
    }

    static int Compare(List<(string Name, Sim Sim)> seqs, Dictionary<string, CompareLive.Stream> oracle, bool quiet)
    {
        int differ = 0;
        long compared = 0;
        foreach (var (name, sim) in seqs)
        {
            if (!oracle.TryGetValue("ds3-engine/" + name, out var stream)) { differ++; Console.WriteLine($"  [DIFF] {name}: the driver wrote no stream"); continue; }
            var reads = stream.Outs.Where(o => o.Tag == "read").Select(o => CompareLive.Completion.From(o.Tag, o.Data).Reported).ToList();
            var features = stream.Outs.Where(o => o.Tag == "getfeature").Select(o => CompareLive.Completion.From(o.Tag, o.Data).Reported).ToList();
            // A ring slot: source, report ID, a 16-bit size, the data.
            var ring = stream.Outs.Where(o => o.Tag == "ring").Select(o => o.Data.AsSpan(1, 1).ToArray().Concat(o.Data.AsSpan(4).ToArray()).ToArray()).ToList();
            string? first = null;
            void Same(string what, List<byte[]> want, List<byte[]> mine)
            {
                if (first != null) return;
                if (want.Count != mine.Count) { first = $"{want.Count} {what} from the driver, {mine.Count} from the engine"; return; }
                for (int i = 0; i < want.Count; i++)
                    if (!want[i].AsSpan().SequenceEqual(mine[i]))
                    {
                        first = $"{what} {i}: driver {Convert.ToHexString(want[i])}, engine {Convert.ToHexString(mine[i])}";
                        return;
                    }
                compared += want.Count;
            }
            Same("reads", reads, sim.Reads);
            Same("features", features, sim.Features);
            Same("ring slots", ring, sim.Ring);
            if (first != null) { differ++; if (!quiet) Console.WriteLine($"  [DIFF] {name}: {first}"); }
        }
        if (!quiet) Console.WriteLine($"  {seqs.Count} sequences, {compared} reports compared, {differ} differ");
        return differ;
    }

    public static int Run(string golden, string native, ulong seed)
    {
        s_failures = 0;
        Console.WriteLine("=== device_oracle_check ds3-engine: dualshock-3-full against 9bd6279's sixaxis form ===");
        string planPath = Path.Combine(golden, "case5-plan.txt");
        string exe = Path.Combine(native, "driver_harness_oracle.exe");
        foreach (var need in new[] { planPath, exe })
            if (!File.Exists(need)) { Console.WriteLine($"  [MISSING] {need}"); return 2; }
        string json = SwitchEngine.CatalogJson("dualshock-3-full");
        var (p, proto, refusal) = SwitchEngine.Load(json);
        Check("dualshock-3-full loads and its protocol compiles for UMDF2", proto != null, refusal ?? "");
        if (proto == null) return Done();

        var seqs = Sequences(p!, proto, seed);
        var oracle = Drive(seqs, exe, planPath);
        int differ = Compare(seqs, oracle, quiet: false);
        Check("every read, feature and raised output report of the driver's sixaxis form, the profile's programs and rules gave with the same bytes", differ == 0, $"{differ} of {seqs.Count} sequences differ");

        // One entry of the d-pad table changed, its fourth.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var lookup = node["inputReports"]![0]!["program"]!.AsArray().First(o => o!["lookup"] != null)!;
        string table = (string)lookup["table"]!;
        lookup["table"] = table[..6] + (table[6] == '0' ? '1' : '0') + table[7..];
        var (mp, mproto, mRefusal) = SwitchEngine.Load(node.ToJsonString());
        if (mproto == null) Check("the mutated profile loads", false, mRefusal ?? "");
        else
        {
            int mdiffer = Compare(Sequences(mp!, mproto, seed), oracle, quiet: true);
            Check("with one d-pad table entry changed the comparison fails", mdiffer > 0, $"{mdiffer} sequences differ");
        }
        return Done();
    }

    static Dictionary<string, CompareLive.Stream> Drive(List<(string Name, Sim Sim)> seqs, string exe, string planPath)
    {
        string work = Path.Combine(AppContext.BaseDirectory, "ds3-engine", $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var sc = new StringBuilder("hm-driver-script 1\n");
            foreach (var (_, sim) in seqs) sc.Append(sim.Script);
            string script = Path.Combine(work, "script.txt");
            File.WriteAllText(script, sc.ToString(), new UTF8Encoding(false));
            return CompareLive.RunHarness(exe, planPath, script, work).ToDictionary(s => s.Name, StringComparer.Ordinal);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(work)) File.Delete(f);
            Directory.Delete(work);
        }
    }

    static int Done()
    {
        Console.WriteLine($"device_oracle_check ds3-engine: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures} check(s)")}");
        return s_failures == 0 ? 0 : 1;
    }
}
