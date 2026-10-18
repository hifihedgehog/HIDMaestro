// The interpreter's cost for a profile's programs, measured as the design
// note's table was (docs/design/data-driven-devices.md, mechanism 2): each
// launch a new process at high priority, the programs interleaved run by
// run, each run n reports over 1,024 random inputs, and the launch's median
// of its runs per program. The parent takes p50 and p95 across launches.
// The compile is the one-time work a profile from outside the catalog pays
// at create: validation with its canonical form and SHA-256, and the
// interpreter's stage arrays, per program and for the whole model, the
// median of 200 per launch.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

internal static unsafe class InterpreterCost
{
    /// <summary>The argument a launch of this program runs under.</summary>
    public const string LaunchArg = "--cost-launch";

    /// <summary>Register values a measurement starts from in place of the
    /// protocol's initial ones, as "name=value,name=value", value decimal
    /// or 0x hex: a block a host's command turns on runs only when its
    /// register says so (the Switch 2 Pro's 0x05 motion with
    /// featureEnabled=0x04).</summary>
    public const string RegistersVariable = "HIDMAESTRO_PROFILECHECK_REGISTERS";

    /// <summary>The register values <see cref="RegistersVariable"/> names,
    /// or an error.</summary>
    public static List<(string Name, uint Value)> ParseRegisters(string? text, out string? error)
    {
        error = null;
        var list = new List<(string, uint)>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            string name = eq > 0 ? part[..eq].Trim() : "", value = eq > 0 ? part[(eq + 1)..].Trim() : "";
            bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (name.Length == 0 || !uint.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out uint v))
            {
                error = $"'{part}' is not name=value with a decimal or 0x hex value";
                return list;
            }
            list.Add((name, v));
        }
        return list;
    }

    const int Inputs = 1024;
    const int CompileReps = 200;

    sealed class Variant
    {
        public required string Name;
        public required ValidProgram Program;
        public ReportProgram? Report;
        public StateProgram? State;
        public byte[] In = Array.Empty<byte>();
        public byte[] Out = Array.Empty<byte>();
        public int InStride, OutStride;
        public HMGamepadState[] States = Array.Empty<HMGamepadState>();
        public float[] Slots = Array.Empty<float>();
        public uint[] Regs = Array.Empty<uint>();
        public uint[] Counters = Array.Empty<uint>();
        public readonly List<double> Times = new();
    }

    /// <summary>A profile's programs, each run through the interpreter as a
    /// controller binds a program from outside the catalog. A state block
    /// the program form cannot say stays with the codec and is
    /// named.</summary>
    static List<Variant> Variants(ControllerProfile p, List<string> notes)
    {
        var model = p.GetOrCompileModel();
        var list = new List<Variant>();
        var regs = model.Registers.Initial.Length == 0 ? new uint[1] : (uint[])model.Registers.Initial.Clone();
        foreach (var (name, value) in ParseRegisters(Environment.GetEnvironmentVariable(RegistersVariable), out _))
            if (model.Registers.IndexOf(name) is int at and >= 0) regs[at] = value & model.Registers.WidthMask(at);
        var counters = new uint[Math.Max(1, model.CounterNames.Length)];
        void Add(string name, ValidProgram v) => list.Add(new Variant { Name = $"{p.Id}: {name}", Program = v, Regs = regs, Counters = counters });
        // A descriptor that declares no report IDs gives a report the ID -1.
        static string Report(int id) => id < 0 ? "no report ID" : $"report 0x{id:X2}";
        for (int i = 0; i < model.Layouts.Length; i++)
            Add($"inputReports[{i}] {model.Layouts[i].Name}, {Report(model.Layouts[i].ReportId)}", model.Layouts[i].Program);
        for (int i = 0; i < model.LiveFeatures.Length; i++)
            Add($"liveFeatureReports[{i}], {Report(model.LiveFeatureIds[i])}", model.LiveFeatures[i]);
        for (int i = 0; i < model.RuleEmits.Length; i++)
            if (model.RuleEmits[i] is { } e) Add($"protocol rule {i}'s emitted {Report(e.ReportId)}", e.Program);
        foreach (var d in model.Decoders) Add($"protocol decoder {d.Name}", d.Program);
        if (model.StateProgram is { } sp) Add("stateBlock", sp);
        else if (p.StateBlock != null) notes.Add($"{p.Id}: stateBlock runs through the codec, which is not a program: {model.StateCodecReason}");
        return list;
    }

    // ── a launch ───────────────────────────────────────────────────────────

    /// <summary>One launch: <c>--cost-launch runs reports dir id ...</c>.
    /// Prints "cost", "compile" and "model" lines, tab-separated, for the
    /// parent to read.</summary>
    public static int Launch(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[0], out int runs) || !int.TryParse(args[1], out int reports))
        {
            Console.WriteLine("error\tusage: " + LaunchArg + " <runs> <reports> <dir> <id> ...");
            return 2;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("note\tthe launch runs at normal priority, which Windows did not raise"); }
        var db = ProfileDatabase.Load(args[2]);
        var chosen = new List<ControllerProfile>();
        foreach (string id in args[3..])
        {
            var p = db.All.FirstOrDefault(x => x.Id == id);
            if (p == null) { Console.WriteLine($"error\t{id} did not load from {args[2]}"); return 1; }
            chosen.Add(p);
        }
        Run(chosen, runs, reports, (kind, name, value) => Console.WriteLine(F($"{kind}\t{name}\t{value:F3}")));
        return 0;
    }

    /// <summary>One launch's measurements of <paramref name="profiles"/>'
    /// programs, each handed to <paramref name="emit"/> as (kind, name,
    /// value): "model" and "compile" in microseconds, "cost" in ns per
    /// report, "note" with no value.</summary>
    public static void Run(IEnumerable<ControllerProfile> profiles, int runs, int reports, Action<string, string, double> emit)
    {
        var variants = new List<Variant>();
        var notes = new List<string>();
        foreach (var p in profiles)
        {
            variants.AddRange(Variants(p, notes));
            emit("model", p.Id, ModelCompile(p));
        }
        foreach (string n in notes) emit("note", n, 0);
        foreach (var v in variants) emit("compile", v.Name, Compile(v.Program));
        Measure(variants, runs, reports);
        foreach (var v in variants)
        {
            v.Times.Sort();
            emit("cost", v.Name, v.Times[v.Times.Count / 2]);
        }
    }

    static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>A program's compile off the generated path, microseconds,
    /// the median of 200.</summary>
    static double Compile(ValidProgram v)
    {
        var t = new double[CompileReps];
        object keep = v.Spec.Gamepad ? new StateProgram(ProgramForm.Validate(v.Spec)) : new ReportProgram(ProgramForm.Validate(v.Spec));
        for (int r = 0; r < CompileReps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var valid = ProgramForm.Validate(v.Spec);
            keep = v.Spec.Gamepad ? new StateProgram(valid) : new ReportProgram(valid);
            t[r] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
        GC.KeepAlive(keep);
        Array.Sort(t);
        return t[t.Length / 2];
    }

    /// <summary>The whole model's compile at create, microseconds, the
    /// median of 200: the model groups lowered and validated, and every
    /// program's stage arrays.</summary>
    static double ModelCompile(ControllerProfile p)
    {
        var groups = new DeviceModelGroups { Protocol = p.Protocol, StateBlock = p.StateBlock, InputReports = p.InputReports, LiveFeatureReports = p.LiveFeatureReports };
        var t = new double[CompileReps];
        object? keep = null;
        for (int r = 0; r < CompileReps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var model = ModelCompiler.Compile(groups);
            var built = new List<object>();
            foreach (var prog in model.AllPrograms) built.Add(new ReportProgram(prog));
            if (model.StateProgram is { } sp) built.Add(new StateProgram(sp));
            keep = built;
            t[r] = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
        GC.KeepAlive(keep);
        Array.Sort(t);
        return t[t.Length / 2];
    }

    static void Prepare(Variant v, Random rng)
    {
        var spec = v.Program.Spec;
        v.OutStride = spec.Size + 16;
        v.Out = GC.AllocateArray<byte>(Inputs * v.OutStride, pinned: true);
        if (spec.Gamepad)
        {
            v.State = new StateProgram(v.Program);
            v.States = new HMGamepadState[Inputs];
            v.Slots = GC.AllocateArray<float>(Inputs * 6, pinned: true);
            for (int i = 0; i < Inputs; i++)
            {
                v.States[i] = new HMGamepadState
                {
                    // The sticks and triggers arrive as the six slots
                    // SubmitState resolves, below.
                    Buttons = (HMButton)(uint)rng.Next(),
                    AccelGX = (float)(rng.NextDouble() * 4 - 2), AccelGY = (float)(rng.NextDouble() * 4 - 2), AccelGZ = (float)(rng.NextDouble() * 4 - 2),
                    GyroDpsX = (float)(rng.NextDouble() * 400 - 200), GyroDpsY = (float)(rng.NextDouble() * 400 - 200), GyroDpsZ = (float)(rng.NextDouble() * 400 - 200),
                };
                for (int k = 0; k < 6; k++) v.Slots[i * 6 + k] = (float)rng.NextDouble();
            }
        }
        else
        {
            v.Report = new ReportProgram(v.Program);
            // A read is one unaligned 32-bit load, so each input carries the
            // slack the form gives it past its end.
            v.InStride = spec.StateSize + ProgramForm.Slack + 1;
            v.In = GC.AllocateArray<byte>(Inputs * v.InStride, pinned: true);
            rng.NextBytes(v.In);
        }
    }

    static void RunN(Variant v, int n)
    {
        fixed (byte* o = v.Out)
        fixed (uint* r = v.Regs)
        fixed (uint* c = v.Counters)
        {
            if (v.State is { } sp)
            {
                fixed (float* sl = v.Slots)
                    for (int i = 0; i < n; i++)
                    {
                        int j = i & (Inputs - 1);
                        sp.Run(in v.States[j], sl + j * 6, o + j * v.OutStride, c);
                    }
                return;
            }
            var p = v.Report!;
            fixed (byte* s = v.In)
                for (int i = 0; i < n; i++)
                {
                    int j = i & (Inputs - 1);
                    c[0] = (uint)i;
                    p.Run(s + j * v.InStride, o + j * v.OutStride, r, c);
                }
        }
    }

    static void Measure(List<Variant> variants, int runs, int n)
    {
        var rng = new Random(20261018);
        foreach (var v in variants)
        {
            Prepare(v, rng);
            RunN(v, n);
        }
        for (int r = 0; r < runs; r++)
            foreach (var v in variants)
            {
                long t0 = Stopwatch.GetTimestamp();
                RunN(v, n);
                v.Times.Add((Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / n);
            }
    }

    // ── the parent ─────────────────────────────────────────────────────────

    /// <summary>What the numbers were measured under.</summary>
    public static IEnumerable<string> Conditions()
    {
        bool jit = RuntimeFeature.IsDynamicCodeSupported;
        string tiering = !jit ? "none, compiled ahead of time"
            : Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0" || AppContext.GetData("System.Runtime.TieredCompilation") is false
                ? "off" : "on";
        var run = typeof(ReportProgram).GetMethod("Run", BindingFlags.Instance | BindingFlags.Public);
        bool aggressive = run != null && (run.MethodImplementationFlags & MethodImplAttributes.AggressiveOptimization) != 0;
        yield return $"runtime: {RuntimeInformation.FrameworkDescription}, {(jit ? "JIT" : "NativeAOT")}, {RuntimeInformation.ProcessArchitecture}";
        yield return $"tiering: {tiering}{(jit && aggressive ? ", and the interpreter's Run carries AggressiveOptimization, so no report runs Tier0 code" : "")}";
        yield return $"CPU: {CpuName()}, {Environment.ProcessorCount} logical processors, {RuntimeInformation.OSDescription}";
        yield return "instruction set: " + (RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86
            ? $"AVX-512F {Yes(Avx512F.IsSupported)}, AVX2 {Yes(Avx2.IsSupported)}, BMI2 {Yes(Bmi2.IsSupported)}"
            : $"AdvSimd {Yes(AdvSimd.IsSupported)}, ArmBase {Yes(ArmBase.IsSupported)}, CRC32 {Yes(System.Runtime.Intrinsics.Arm.Crc32.IsSupported)}");
    }

    static string Yes(bool b) => b ? "yes" : "no";

    static string CpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
    }

    /// <summary>Nearest rank, as the battery's probes print p50 and
    /// p95.</summary>
    static double Rank(List<double> sorted, double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>Launches this program <paramref name="launches"/> times per
    /// directory and prints p50 and p95 across them. Returns the number of
    /// launches that failed.</summary>
    public static int Measure(List<(string Dir, string Id)> profiles, int launches, int runs, int reports, string registers)
    {
        Console.WriteLine($"cost on the interpreter: {launches} launches, each the median of {runs} runs of {reports} reports, the programs interleaved run by run");
        Console.WriteLine(registers.Length == 0
            ? "  registers: each protocol's initial values"
            : $"  registers: {registers}, the rest at each protocol's initial values");
        foreach (string c in Conditions()) Console.WriteLine("  " + c);
        if (profiles.Count == 0)
        {
            Console.WriteLine("  no profile loaded, so nothing to measure");
            return 0;
        }
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("the program's own path is unknown");
        var cost = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var compile = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var model = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var order = new List<string>();
        var notes = new SortedSet<string>(StringComparer.Ordinal);
        int failed = 0;
        foreach (var group in profiles.GroupBy(p => p.Dir, StringComparer.OrdinalIgnoreCase))
            for (int l = 0; l < launches; l++)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                psi.Environment[RegistersVariable] = registers;
                foreach (string a in new[] { LaunchArg, runs.ToString(CultureInfo.InvariantCulture), reports.ToString(CultureInfo.InvariantCulture), group.Key }
                                     .Concat(group.Select(p => p.Id)))
                    psi.ArgumentList.Add(a);
                using var child = Process.Start(psi)!;
                string err = "";
                var errTask = child.StandardError.ReadToEndAsync();
                var lines = new List<string>();
                while (child.StandardOutput.ReadLine() is { } line) lines.Add(line);
                child.WaitForExit();
                err = errTask.Result;
                if (child.ExitCode != 0)
                {
                    failed++;
                    Console.WriteLine($"  launch {l + 1} exited {child.ExitCode}: {string.Join(" | ", lines.Concat(new[] { err.Trim() }).Where(x => x.Length > 0))}");
                    continue;
                }
                foreach (string line in lines)
                {
                    string[] f = line.Split('\t');
                    if (f[0] == "note" && f.Length >= 2) { notes.Add(f[1]); continue; }
                    if (f.Length != 3) continue;
                    double value = double.Parse(f[2], CultureInfo.InvariantCulture);
                    var target = f[0] switch { "cost" => cost, "compile" => compile, "model" => model, _ => null };
                    if (target == null) continue;
                    if (!target.TryGetValue(f[1], out var list)) { target[f[1]] = list = new List<double>(); if (f[0] == "cost") order.Add(f[1]); }
                    list.Add(value);
                }
            }
        foreach (string n in notes) Console.WriteLine("  " + n);
        foreach (string name in order)
        {
            var s = cost[name].OrderBy(x => x).ToList();
            var c = compile.TryGetValue(name, out var cl) ? cl.OrderBy(x => x).ToList() : null;
            Console.WriteLine(F($"  {name}: p50 {Rank(s, 0.50):F2} ns, p95 {Rank(s, 0.95):F2} ns per report over {s.Count} launches")
                              + (c == null ? "" : F($", compile p50 {Rank(c, 0.50):F1} microseconds")));
        }
        foreach (var (id, values) in model.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var s = values.OrderBy(x => x).ToList();
            Console.WriteLine(F($"  {id}: the whole model's compile at create, p50 {Rank(s, 0.50):F1}, p95 {Rank(s, 0.95):F1} microseconds over {s.Count} launches"));
        }
        if (order.Count == 0 && failed == 0) Console.WriteLine("  no program: these profiles build no report and decode none");
        return failed;
    }
}
