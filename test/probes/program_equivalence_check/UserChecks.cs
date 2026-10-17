using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

/// <summary>Profiles from outside the catalog: a directory's profiles load at
/// run time with no rebuild, their programs bind the interpreter unless the
/// catalog generated the same program, and every layout and decoder gives the
/// naive evaluator's bytes, through the bound set and through the protocol
/// engine a USB/IP device runs. user/every-op.json uses every op.</summary>
internal static unsafe class UserChecks
{
    static void Fail(string text) => Checks.Fail(text);

    public static string DefaultDir() => Path.Combine(AppContext.BaseDirectory, "user");

    public static int Run(string dir)
    {
        int before = Checks.Failures;
        Console.WriteLine($"== Profiles from {dir}, loaded at run time");
        if (!Directory.Exists(dir)) { Fail($"{dir} does not exist"); return Checks.Failures - before; }
        var db = ProfileDatabase.Load(dir);
        int files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Count();
        if (db.All.Count != files) Fail($"{files} files, {db.All.Count} profiles loaded");
        var data = new Random(ProbeCorpusSeed.Value ^ 0x05E2);
        foreach (var p in db.All)
        {
            CompiledModel m;
            try { m = p.GetOrCompileModel(); p.ValidateDeviceModel(); }
            catch (Exception e) when (e is FormException or InvalidOperationException) { Fail($"{p.Id}: {e.Message}"); continue; }
            var kinds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var v in m.AllPrograms)
                foreach (var op in v.Spec.Ops)
                {
                    kinds.Add(op.GetType().Name);
                    if (op is WhenOp w) foreach (var sub in w.Ops) kinds.Add("when-" + sub.GetType().Name);
                }
            Console.WriteLine($"{p.Id}: {m.Layouts.Length} layouts, {m.Decoders.Length} decoders; op kinds {string.Join(", ", kinds)}");
            if (p.Id == "user-every-op")
                foreach (var (name, op, _) in SemanticsCases.Samples())
                {
                    string kind = name.StartsWith("when-", StringComparison.Ordinal) ? "when-" + op.GetType().Name : op.GetType().Name;
                    if (!kinds.Contains(kind)) Fail($"{p.Id}: uses no {kind}, so it does not test every op");
                }

            if (ProgramBinder.BindLayouts(m) is { } runner)
            {
                Console.WriteLine($"{p.Id}: its layout set binds {(runner.Generated ? "the generated set, since the build generated it from the same JSON" : "the interpreter")}");
                Layouts(p, m, runner, data);
                Engine(p, m, runner, data);
            }
            foreach (var dec in ProgramBinder.BindDecoders(m))
            {
                var v = dec.Form.Program;
                var s = new byte[v.Spec.StateSize + Checks.StatePad];
                var a = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
                var regs = new uint[Checks.Registers]; var ctr = new uint[Checks.Counters];
                int diff = 0;
                for (int n = 0; n < 20000; n++)
                {
                    data.NextBytes(s);
                    Array.Fill(a, (byte)0xA5); Array.Fill(c, (byte)0xC3);
                    fixed (byte* ps = s, pa = a) dec.Run(ps, pa);
                    Naive.Run(v.Spec, s, c, regs, ctr);
                    if (!a.AsSpan(0, v.Spec.Size).SequenceEqual(c.AsSpan(0, v.Spec.Size))) diff++;
                }
                if (diff == 0) Console.WriteLine($"{p.Id} decoder {dec.Form.Name}: {(dec.Generated ? "generated" : "interpreted")}, 0 of 20000 differ from the naive evaluator");
                else Fail($"{p.Id} decoder {dec.Form.Name}: {diff} of 20000 differ from the naive evaluator");
            }
        }
        return Checks.Failures - before;
    }

    static void Layouts(ControllerProfile p, CompiledModel m, LayoutRunner runner, Random data)
    {
        var s = new byte[m.StateSize + Checks.StatePad];
        var a = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
        var regs = new uint[Checks.Registers]; var ctr = new uint[Checks.Counters];
        for (int layout = 0; layout < m.Layouts.Length; layout++)
        {
            var v = m.Layouts[layout].Program;
            int diff = 0; string? first = null;
            for (int n = 0; n < 20000; n++)
            {
                data.NextBytes(s);
                Checks.DrawRegisters(data, regs, ctr, n);
                Array.Fill(a, (byte)0xA5); Array.Fill(c, (byte)0xC3);
                fixed (byte* ps = s, pa = a)
                fixed (uint* pr = regs, pc = ctr)
                    runner.Run(layout, ps, pa, pr, pc);
                Naive.Run(v.Spec, s, c, regs, ctr);
                if (!a.AsSpan(0, v.Spec.Size).SequenceEqual(c.AsSpan(0, v.Spec.Size)))
                {
                    diff++;
                    first ??= $"runner {Convert.ToHexString(a, 0, v.Spec.Size)} naive {Convert.ToHexString(c, 0, v.Spec.Size)}";
                }
            }
            if (diff == 0) Console.WriteLine($"{p.Id} layout {m.Layouts[layout].Name}: 0 of 20000 differ from the naive evaluator");
            else Fail($"{p.Id} layout {m.Layouts[layout].Name}: {diff} of 20000 differ, first {first}");
        }
    }

    /// <summary>The profile's protocol compiled as a USB/IP device compiles
    /// it, its engine holding a random state block, and each layout read
    /// through the engine as a GET_REPORT reads it, against the naive
    /// evaluator under the engine's registers and counters.</summary>
    static void Engine(ControllerProfile p, CompiledModel m, LayoutRunner runner, Random data)
    {
        var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p, 0));
        if (compiled == null) { Console.WriteLine($"{p.Id}: no protocol, so no engine"); return; }
        var engine = new ProtocolEngine(compiled, "HM-USER-0", 0, runner);
        var block = new byte[m.StateSize];
        var dst = new byte[ProgramForm.MaxReport];
        var c = new byte[ProgramForm.MaxReport];
        var s = new byte[m.StateSize + Checks.StatePad];
        var regs = new uint[Checks.Registers]; var ctr = new uint[Checks.Counters];
        int runs = 0, diff = 0;
        foreach (var layout in m.Layouts.Where(l => l.ReportId >= 0))
            for (int n = 0; n < 2000; n++)
            {
                data.NextBytes(block);
                engine.SetStateBlock(block);
                Array.Fill(dst, (byte)0xA5);
                int len = engine.Peek((byte)layout.ReportId, dst);
                Array.Clear(s); block.CopyTo(s, 0);
                Array.Clear(regs); engine.RegisterWords.CopyTo(regs, 0);
                Array.Clear(ctr); for (int k = 0; k < m.CounterNames.Length; k++) ctr[k] = engine.GetCounter(k);
                Array.Fill(c, (byte)0xC3);
                Naive.Run(layout.Program.Spec, s, c, regs, ctr);
                runs++;
                if (len != layout.Size || !dst.AsSpan(0, layout.Size).SequenceEqual(c.AsSpan(0, layout.Size))) diff++;
            }
        if (diff == 0) Console.WriteLine($"{p.Id}: the protocol engine's reports equal the naive evaluator's on {runs} state blocks");
        else Fail($"{p.Id}: the protocol engine's reports differ from the naive evaluator's on {diff} of {runs}");
    }
}
