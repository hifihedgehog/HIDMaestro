using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

/// <summary>The three paths compared: the generated kernel, the interpreter
/// and the naive evaluator, each output buffer filled with its own byte
/// before every input so a byte a path skips shows. Then the review cases,
/// the domain edges, the seeded corpus, the negative corpus, the planted
/// defects, the op list's one semantics and the hat8 diagonal.</summary>
internal static unsafe class Checks
{
    /// <summary>Bytes past a program's state block, which every 32-bit read
    /// at its last byte loads.</summary>
    public const int StatePad = ProgramForm.Slack + 1;
    public static readonly byte[] PreFill = { 0xA5, 0x5A, 0xC3 };
    static readonly byte[] ZeroFill = { 0, 0, 0 };

    /// <summary>The register and counter arrays every path reads: as many
    /// registers as a program may name, as many counters as a model may
    /// declare.</summary>
    public const int Registers = ProgramForm.MaxRegisters, Counters = ProgramForm.MaxCounters;

    /// <summary>Every failure in the process, so each part reports its own
    /// as the change across its run.</summary>
    public static int Failures;

    public static void Fail(string text)
    {
        Console.WriteLine("[FAIL] " + text);
        Failures++;
    }

    /// <summary>The corpus kernels this probe's build generated, then the
    /// SDK's catalog kernels.</summary>
    public static delegate*<byte*, byte*, uint*, uint*, void> Kernel(ValidProgram v)
    {
        if (ProbeKernels.ByHash.TryGetValue(v.Hash, out nint fn)) return (delegate*<byte*, byte*, uint*, uint*, void>)fn;
        return ProgramBinder.Kernel(v.Hash);
    }

    static delegate*<byte*, byte*, uint*, uint*, void> Mutant(ValidProgram v, PathMutation m) =>
        ProbeKernels.Mutants.TryGetValue($"{v.Hash}|{m}", out nint fn) ? (delegate*<byte*, byte*, uint*, uint*, void>)fn : null;

    /// <summary>Random registers and counters, with zeros and all-ones drawn
    /// often, since conditions test both.</summary>
    public static void DrawRegisters(Random data, uint[] regs, uint[] ctr, int n)
    {
        for (int k = 0; k < regs.Length; k++) regs[k] = (uint)data.Next() ^ ((uint)data.Next() << 16);
        for (int k = 0; k < ctr.Length; k++) ctr[k] = (uint)data.Next() ^ ((uint)data.Next() << 16);
        if (n % 4 == 0) regs[data.Next(4)] = 0;
        if (n % 8 == 1) regs[data.Next(4)] = 0xFFFFFFFF;
        if (n % 16 == 3) Array.Clear(regs);
    }

    /// <summary>Runs one input through the three paths and returns whether
    /// they agree on every byte of the report. <paramref name="s"/> holds the
    /// state block and its pad.</summary>
    public static bool Agree(ValidProgram v, delegate*<byte*, byte*, uint*, uint*, void> kernel, ReportProgram interp,
        byte[] s, byte[] a, byte[] b, byte[] c, uint[] regs, uint[] ctr, byte[] fills, ref string? first)
    {
        int size = v.Spec.Size;
        Array.Fill(a, fills[0]); Array.Fill(b, fills[1]); Array.Fill(c, fills[2]);
        fixed (byte* ps = s, pa = a, pb = b)
        fixed (uint* pr = regs, pc = ctr)
        {
            kernel(ps, pa, pr, pc);
            interp.Run(ps, pb, pr, pc);
        }
        Naive.Run(v.Spec, s, c, regs, ctr);
        bool same = a.AsSpan(0, size).SequenceEqual(b.AsSpan(0, size)) && a.AsSpan(0, size).SequenceEqual(c.AsSpan(0, size));
        if (!same)
            first ??= $"state {Convert.ToHexString(s, 0, v.Spec.StateSize)}: generated {Convert.ToHexString(a, 0, size)} interpreted {Convert.ToHexString(b, 0, size)} naive {Convert.ToHexString(c, 0, size)}";
        return same;
    }

    /// <summary>Generated against interpreted against naive on random
    /// inputs. Returns the inputs on which any two differ.</summary>
    public static int Compare(ValidProgram v, int inputs, Random data, out string? first,
        PathMutation gen = PathMutation.None, InterpMutation interpMutation = InterpMutation.None, byte[]? fills = null,
        delegate*<byte*, byte*, uint*, uint*, void> kernel = null)
    {
        fills ??= PreFill;
        first = null;
        if (kernel == null) kernel = gen == PathMutation.None ? Kernel(v) : Mutant(v, gen);
        if (kernel == null) throw new InvalidOperationException($"{v.Spec.Name}: no generated kernel for hash {v.Hash[..16]} ({gen})");
        var interp = new ReportProgram(v, interpMutation);
        var s = new byte[v.Spec.StateSize + StatePad];
        var a = new byte[ProgramForm.MaxReport]; var b = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
        var regs = new uint[Registers]; var ctr = new uint[Counters];
        int diff = 0;
        for (int n = 0; n < inputs; n++)
        {
            data.NextBytes(s);
            DrawRegisters(data, regs, ctr, n);
            if (!Agree(v, kernel, interp, s, a, b, c, regs, ctr, fills, ref first)) diff++;
        }
        return diff;
    }

    static void Refusals(string title, IEnumerable<(string rule, ProgramSpec program, string path)> cases)
    {
        if (title.Length > 0) Console.WriteLine(title);
        foreach (var (rule, p, path) in cases)
        {
            try { ProgramForm.Validate(p); Fail($"accepted: {rule}"); }
            catch (FormException e)
            {
                if (e.Path == path) Console.WriteLine($"refused: {rule}: {e.Message}");
                else Fail($"refused at {e.Path}, not {path}: {rule}: {e.Reason}");
            }
        }
    }

    /// <summary>A program as round 4's validation accepted it: the same form
    /// without the rule that keeps a template bit of 1 out from under an OR
    /// op. Built here only to show the naive evaluator catches what that rule
    /// refuses.</summary>
    static ValidProgram AcceptedAsRound4Did(ProgramSpec p)
    {
        var template = new byte[p.Size];
        foreach (var op in p.Ops) if (op is ConstOp c) Array.Copy(c.Bytes, 0, template, c.At, c.Bytes.Length);
        string canonical = ProgramForm.Canonicalize(p);
        return new ValidProgram
        {
            Spec = p, Template = template,
            Unconditional = p.Ops.Where(o => o is not ConstOp and not WhenOp).ToArray(),
            Blocks = p.Ops.OfType<WhenOp>().ToArray(),
            Canonical = canonical,
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))),
        };
    }

    static int InterpretedAgainstNaive(ValidProgram v, int inputs, Random data)
    {
        var interp = new ReportProgram(v);
        var s = new byte[v.Spec.StateSize + StatePad];
        var b = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
        var regs = new uint[Registers]; var ctr = new uint[Counters];
        int diff = 0;
        fixed (byte* ps = s, pb = b)
        fixed (uint* pr = regs, pc = ctr)
            for (int n = 0; n < inputs; n++)
            {
                data.NextBytes(s);
                Array.Fill(b, (byte)0x5A); Array.Fill(c, (byte)0xC3);
                interp.Run(ps, pb, pr, pc);
                Naive.Run(v.Spec, s, c, regs, ctr);
                if (!b.AsSpan(0, v.Spec.Size).SequenceEqual(c.AsSpan(0, v.Spec.Size))) diff++;
            }
        return diff;
    }

    static void Expect(string name, int differ, int inputs, string? first)
    {
        if (differ == 0) Console.WriteLine($"{name}: valid, 0 of {inputs} inputs differ among generated, interpreted and naive");
        else Fail($"{name}: {differ} of {inputs} inputs differ, first {first}");
    }

    public static int Run()
    {
        int before = Failures;
        var data = new Random(ProbeCorpusSeed.Value ^ 0x5EED);

        Console.WriteLine("== The third review's path check, against the validated form");
        foreach (var p in ReviewCases.All)
        {
            ValidProgram v;
            try { v = ProgramForm.Validate(p); }
            catch (FormException e) { Console.WriteLine($"{p.Name}: refused at validation, {e.Message}"); continue; }
            int d = Compare(v, 2000, data, out var first);
            Expect(p.Name, d, 2000, first);
        }
        // P4 and P5 break the read rules, which the form must refuse.
        foreach (var name in new[] { "P4", "P5" })
            try { ProgramForm.Validate(ReviewCases.All.First(p => p.Name == name)); Fail($"{name} accepted"); }
            catch (FormException) { }

        Console.WriteLine("== A template bit under an OR op is refused, and the same shapes with the template's bits beside the op's agree three ways");
        Refusals("", Round5Cases.RefusedOverTemplate.Select(x => (x.program.Name, x.program, x.path)));
        foreach (var p in Round5Cases.ValidBesideTemplate)
        {
            int d = Compare(ProgramForm.Validate(p), 2000, data, out var first);
            Expect(p.Name, d, 2000, first);
        }

        Console.WriteLine("== The naive evaluator follows the note, not the fast paths: A1 to A3 accepted as round 4's validation accepted them, interpreted against naive");
        foreach (var (p, _) in Round5Cases.RefusedOverTemplate)
        {
            int d = InterpretedAgainstNaive(AcceptedAsRound4Did(p), 2000, data);
            if (d > 0) Console.WriteLine($"{p.Name}: {d} of 2000 inputs differ, so a validation that let a template bit under an OR op through would fail the check");
            else Fail($"{p.Name}: the naive evaluator agrees with the interpreter on a template bit under an OR op");
        }

        Refusals("== Centers and dead zones past their domains: refused, each with its path",
            Round5Cases.RefusedCenters.Select(x => (x.program.Name, x.program, x.path)));

        Console.WriteLine("== Domain edges, valid: three ways, 100,000 inputs each");
        foreach (var p in Round5Cases.Edges.Append(Round5Cases.U))
        {
            int d = Compare(ProgramForm.Validate(p), 100000, data, out var first);
            Expect(p.Name, d, 100000, first);
        }

        Console.WriteLine($"== Random programs from seed {ProbeCorpusSeed.Value}, three ways, 2,000 inputs each");
        CorpusRun(data);

        Refusals("== Negative corpus: each program breaks one rule and must be refused with its path", Corpus.Negatives());

        Console.WriteLine("== Planted defects: one path carries a defect; 2,000 inputs each, pre-filled and zero-filled");
        foreach (var (cls, program, gen, interpMutation) in ProbeCorpus.PathMutations)
        {
            var v = ProgramForm.Validate(program);
            int pre = Compare(v, 2000, data, out _, gen, interpMutation, PreFill);
            int zero = Compare(v, 2000, data, out _, gen, interpMutation, ZeroFill);
            if (pre == 0) { Fail($"{cls}: the check missed it"); continue; }
            Console.WriteLine($"{cls}: pre-filled {pre} of 2000 differ, zero-filled {zero} of 2000, so the check fails on it{(zero == 0 ? ", and only the pre-fill shows it" : "")}");
            if (gen == PathMutation.ZeroTemplateSkipped && zero != 0)
                Fail($"{cls}: a zero-filled buffer showed a skipped zero store, so the pre-fill is not what shows it");
        }

        OneSemantics(data);
        HatDiagonal();
        HatEdges();

        Console.WriteLine(Failures == before ? "CHECKS: PASS" : $"CHECKS: {Failures - before} FAILED");
        return Failures - before;
    }

    static void CorpusRun(Random data)
    {
        int programs = 0, differ = 0, overlapping = 0, ops = 0, inTerms = 0, wideMasks = 0, wideRegs = 0, decode1112 = 0, blocks = 0, refused = 0;
        int orBeside = 0, wideFieldReads = 0, centerEnds = 0, scaledNear30 = 0, clamps30 = 0, wideHatReads = 0, hatCenterEnds = 0, dzMax = 0, bigEndianCounters = 0;
        int widestField = 0; long largestCenter = 0;
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in Corpus.Draw(ProbeCorpusSeed.Value))
        {
            ValidProgram v;
            try { v = ProgramForm.Validate(p); } catch (FormException e) { refused++; Fail($"{p.Name} refused: {e.Message}"); continue; }
            programs++;
            ops += p.Ops.Length;
            foreach (var op in p.Ops) { string k = op.GetType().Name; kinds[k] = kinds.GetValueOrDefault(k) + 1; }
            if (p.RegisterBits.Any(b => b > 8)) wideRegs++;
            bool beside = false;
            foreach (var op in p.Ops)
            {
                IEnumerable<int> bytes = op switch
                {
                    BitsOp b => Enumerable.Range(0, b.Moves.Length / 2).Select(i => b.Moves[2 * i + 1] >> 3),
                    LookupOp l => new[] { l.Dst },
                    XorOp x => Enumerable.Range(x.Dst, x.Len),
                    _ => Array.Empty<int>(),
                };
                foreach (int at in bytes) if (v.Template[at] != 0) beside = true;
                switch (op)
                {
                    case FieldOp f:
                    {
                        long range = f.From.BigEndianWord ? 0xFFFF : (1L << f.From.Bits) - 1;
                        long maxAbs = Math.Max(f.Center, range - f.Center);
                        if (!f.From.BigEndianWord && f.From.Bits > 16) wideFieldReads++;
                        widestField = Math.Max(widestField, f.From.BigEndianWord ? 16 : f.From.Bits);
                        largestCenter = Math.Max(largestCenter, f.Center);
                        if (f.Center == 0 || f.Center == range) centerEnds++;
                        if (maxAbs * Math.Abs((long)f.Num) / Math.Abs((long)f.Den) + Math.Abs((long)f.Offset) >= 1L << 29) scaledNear30++;
                        if (f.Min == -((1 << 30) - 1) && f.Max == (1 << 30) - 1) clamps30++;
                        break;
                    }
                    case Hat8Op h:
                    {
                        long rx = h.X.BigEndianWord ? 0xFFFF : (1L << h.X.Bits) - 1, ry = h.Y.BigEndianWord ? 0xFFFF : (1L << h.Y.Bits) - 1;
                        if (h.X.Bits > 16 || h.Y.Bits > 16) wideHatReads++;
                        if (h.Center == 0 || h.Center == Math.Min(rx, ry)) hatCenterEnds++;
                        if (h.Deadzone == 65535) dzMax++;
                        break;
                    }
                    case CounterOp k when k.BigEndian:
                        bigEndianCounters++;
                        break;
                    case WhenOp w when w.Ops.Any(o => o is CounterOp { BigEndian: true }):
                        bigEndianCounters++;
                        break;
                }
            }
            if (beside) orBeside++;
            // A block over a byte the template, an unconditional op or an
            // earlier block writes.
            var written = new bool[p.Size];
            foreach (var op in p.Ops)
                switch (op)
                {
                    case ConstOp c: for (int i = c.At; i < c.At + c.Bytes.Length; i++) written[i] = true; break;
                    case CopyOp c: for (int i = c.Dst; i < c.Dst + c.Len; i++) written[i] = true; break;
                    case FieldOp f: for (int i = f.Dst; i < f.Dst + f.OutBytes; i++) written[i] = true; break;
                    case CounterOp c: for (int i = c.Dst; i < c.Dst + c.Width; i++) written[i] = true; break;
                    case Swap16Op s: for (int i = s.Dst; i < s.Dst + 2 * s.Words; i++) written[i] = true; break;
                    case Pack12Op k: for (int i = k.Dst; i < k.Dst + 3; i++) written[i] = true; break;
                    case LookupOp l: written[l.Dst] = true; break;
                    case XorOp x: for (int i = x.Dst; i < x.Dst + x.Len; i++) written[i] = true; break;
                    case BitsOp b: for (int i = 1; i < b.Moves.Length; i += 2) written[b.Moves[i] >> 3] = true; break;
                    case Hat8Op h: written[h.Dst] = true; break;
                    case MaxOfOp m: written[m.Dst] = true; break;
                }
            bool overlap = false;
            foreach (var w in v.Blocks)
            {
                blocks++;
                foreach (var g in w.Cond.Groups)
                    foreach (var t in g)
                    {
                        if (t.In != null) inTerms++;
                        if (t.Mask > 0xFF) wideMasks++;
                    }
                foreach (var sub in w.Ops)
                {
                    int at = sub switch { ConstOp c => c.At, CopyOp c => c.Dst, CounterOp c => c.Dst, _ => 0 };
                    int len = sub switch { ConstOp c => c.Bytes.Length, CopyOp c => c.Len, CounterOp c => c.Width, _ => 0 };
                    for (int i = at; i < at + len; i++) if (written[i]) overlap = true;
                    for (int i = at; i < at + len; i++) written[i] = true;
                }
            }
            if (overlap) overlapping++;
            foreach (var op in p.Ops) if (op is MaxOfOp m) decode1112 += m.Terms.Count(t => t.Parts.Sum(x => x.Bits) >= 11);
            int d = Compare(v, 2000, data, out var first);
            if (d != 0) { differ++; Fail($"{p.Name}: {d} of 2000 differ; {first}"); }
        }
        Console.WriteLine($"{programs} programs from seed {ProbeCorpusSeed.Value} ({refused} refused), {ops} ops, {blocks} blocks; {overlapping} programs with a block over a byte the template, an unconditional op or an earlier block writes; {orBeside} programs with an OR op into a byte whose template sets other bits; {inTerms} in terms, {wideMasks} masks wider than 8 bits, {wideRegs} programs with registers of 16 or 32 bits, {decode1112} decode terms of 11 or 12 bits, {bigEndianCounters} big-endian counters; {differ} programs differ");
        Console.WriteLine($"domain edges drawn: {wideFieldReads} field reads of 17 to 25 bits (widest {widestField}), {centerEnds} field centers at an end of the read's range (largest center {largestCenter}), {scaledNear30} fields scaled to 2^29 or more, {clamps30} clamped at +-(2^30 - 1); {wideHatReads} hats reading more than 16 bits, {hatCenterEnds} hat centers at an end of the range, {dzMax} dead zones of 65,535");
        Console.WriteLine("op kinds: " + string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}")));
        // Each kind of draw the plan names must occur, or the corpus does not
        // test it.
        void Drawn(string what, int count) { if (count == 0) Fail($"the corpus drew no {what}"); }
        Drawn("block over a written byte", overlapping); Drawn("OR op beside template bits", orBeside); Drawn("in term", inTerms);
        Drawn("mask wider than 8 bits", wideMasks); Drawn("register of 16 or 32 bits", wideRegs); Drawn("decode term of 11 or 12 bits", decode1112);
        Drawn("field read of 17 to 25 bits", wideFieldReads); Drawn("field center at an end of its range", centerEnds);
        Drawn("field scaled near 2^30", scaledNear30); Drawn("clamp at +-(2^30 - 1)", clamps30); Drawn("hat center at an end of its range", hatCenterEnds);
        Drawn("dead zone of 65,535", dzMax); Drawn("big-endian counter", bigEndianCounters);
        foreach (var kind in SemanticsCases.Samples().Select(x => x.op.GetType().Name).Distinct())
            if (!kinds.ContainsKey(kind)) Fail($"the corpus drew no {kind}");
    }

    /// <summary>One semantics: every op kind the form defines has an
    /// interpreter stage and a generator emission, or neither, and the three
    /// paths give one result for it. The kinds are read from the form's own
    /// types under the JIT, where reflection lists every type. NativeAOT runs
    /// the same samples.</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The type list is read only under the JIT (RuntimeFeature.IsDynamicCodeSupported), where nothing is trimmed.")]
    static void OneSemantics(Random data)
    {
        Console.WriteLine("== One semantics: each op kind has an interpreter stage and a generator emission, and the three paths agree on it");
        var samples = SemanticsCases.Samples().ToList();
        foreach (var (name, _, program) in samples)
        {
            var v = ProgramForm.Validate(program);
            bool emits, stages;
            try { Emitter.EmitKernel(v); emits = true; } catch (InvalidOperationException) { emits = false; }
            try { _ = new ReportProgram(v); stages = true; } catch (InvalidOperationException) { stages = false; }
            if (emits != stages) { Fail($"{name}: {(emits ? "a generator emission and no interpreter stage" : "an interpreter stage and no generator emission")}"); continue; }
            if (!emits) { Fail($"{name}: neither path runs it"); continue; }
            var kernel = Kernel(v);
            if (kernel == null) { Fail($"{name}: the probe's build generated no kernel for its sample"); continue; }
            int d = Compare(v, 20000, data, out var first);
            if (d == 0) Console.WriteLine($"{name}: emitted, staged, 0 of 20000 inputs differ");
            else Fail($"{name}: {d} of 20000 differ, first {first}");
        }
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            var kinds = typeof(Op).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Op)) && !t.IsAbstract).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var sampled = samples.Select(s => s.op.GetType().Name).ToHashSet();
            foreach (var k in kinds.Where(k => !sampled.Contains(k)))
                Fail($"{k}: an op kind of the form with no sample here, so nothing shows the two paths both run it");
            Console.WriteLine($"op kinds of the form: {string.Join(", ", kinds)}, each sampled");
        }
    }

    /// <summary>The sector rule as the design note states it, for the edge
    /// check: inside the dead zone the ninth entry, else with a the smaller
    /// and b the larger of |x| and |y|, the larger one's axis when
    /// 1,000,000 a is at most 414,214 b, and the diagonal otherwise.</summary>
    static int Sector(long x, long y, long dz)
    {
        if (x * x + y * y <= dz * dz) return 8;
        long a = Math.Min(Math.Abs(x), Math.Abs(y)), b = Math.Max(Math.Abs(x), Math.Abs(y));
        if (1_000_000 * a <= 414_214 * b) return Math.Abs(y) > Math.Abs(x) ? (y > 0 ? 0 : 4) : (x > 0 ? 2 : 6);
        return x > 0 ? (y > 0 ? 1 : 3) : (y > 0 ? 7 : 5);
    }

    /// <summary>hat8 at each of the 16 edges between an axis sector and a
    /// diagonal one, at the last count on each side, and at the dead zone's
    /// edge on each axis: every path reads the sector the rule gives.</summary>
    static void HatEdges()
    {
        Console.WriteLine("== hat8 at each sector edge, one count either side, and at the dead zone's edge");
        var v = ProgramForm.Validate(SemanticsCases.HatEdges);
        var kernel = Kernel(v);
        if (kernel == null) { Fail("hat16-edges: no generated kernel"); return; }
        var interp = new ReportProgram(v);
        var points = new List<(long x, long y)>();
        foreach (long b in new long[] { 3001, 12345, 32767 })
        {
            long edge = 414_214 * b / 1_000_000;            // the largest a still on the axis
            foreach (long a in new[] { edge - 1, edge, edge + 1, edge + 2 })
                foreach (int sx in new[] { 1, -1 })
                    foreach (int sy in new[] { 1, -1 })
                    {
                        points.Add((sx * b, sy * a));
                        points.Add((sx * a, sy * b));
                    }
        }
        foreach (long d in new long[] { 2999, 3000, 3001 })
            foreach (var (x, y) in new[] { (d, 0L), (-d, 0L), (0L, d), (0L, -d), (2121, 2121), (2122, 2122) })
                points.Add((x, y));
        var s = new byte[8 + StatePad];
        var a8 = new byte[ProgramForm.MaxReport]; var b8 = new byte[ProgramForm.MaxReport]; var c8 = new byte[ProgramForm.MaxReport];
        var regs = new uint[Registers]; var ctr = new uint[Counters];
        int bad = 0; string? first = null;
        var seen = new HashSet<int>();
        foreach (var (x, y) in points)
        {
            // Y up: a wire value above the center is up.
            ushort rx = (ushort)(32768 + x), ry = (ushort)(32768 + y);
            Array.Clear(s);
            s[0] = (byte)rx; s[1] = (byte)(rx >> 8); s[2] = (byte)ry; s[3] = (byte)(ry >> 8);
            string? diff = null;
            bool agree = Agree(v, kernel, interp, s, a8, b8, c8, regs, ctr, PreFill, ref diff);
            int want = Sector(x, y, 3000);
            seen.Add(want);
            if (!agree || a8[0] != want)
            {
                bad++;
                first ??= $"({x}, {y}): generated {a8[0]}, interpreted {b8[0]}, naive {c8[0]}, the rule {want}";
            }
        }
        if (bad == 0 && seen.Count == 9) Console.WriteLine($"hat16-edges: {points.Count} points at the 16 sector edges and the dead zone's edge, all nine sectors, every path reads the rule's sector");
        else Fail($"hat16-edges: {bad} of {points.Count} points differ, sectors seen {seen.Count}, first {first}");
    }

    /// <summary>hat8 on a 16-bit stick at a full diagonal, center 0: x and y
    /// are 65,535, whose squares sum to 8,589,672,450. In 32 bits the sum
    /// wraps to -262,142, inside any dead zone, and the hat reads centered.
    /// Every path must read the up-right diagonal.</summary>
    static void HatDiagonal()
    {
        Console.WriteLine("== hat8 at a 16-bit stick's full diagonal");
        var table = SemanticsCases.HatTable;
        var v = ProgramForm.Validate(SemanticsCases.HatDiagonal);
        var s = new byte[8 + StatePad];
        s[0] = s[1] = s[2] = s[3] = 0xFF;
        var a = new byte[ProgramForm.MaxReport]; var b = new byte[ProgramForm.MaxReport]; var c = new byte[ProgramForm.MaxReport];
        var regs = new uint[Registers]; var ctr = new uint[Counters];
        string? first = null;
        var kernel = Kernel(v);
        if (kernel == null) { Fail("hat16-diagonal: no generated kernel"); return; }
        bool agree = Agree(v, kernel, new ReportProgram(v), s, a, b, c, regs, ctr, PreFill, ref first);
        if (!agree || a[1] != table[1]) Fail($"hat16-diagonal: generated {a[1]:X2}, interpreted {b[1]:X2}, naive {c[1]:X2}, where the up-right diagonal is {table[1]:X2}");
        else Console.WriteLine($"hat16-diagonal: all three read {a[1]:X2}, the up-right diagonal");
        // The same test against a hat that squares in 32 bits: it reads the
        // centered entry, so the check above fails it.
        int x = 65535, y = 65535;
        int sum32 = unchecked(x * x + y * y);
        byte wrong = sum32 <= 1000 * 1000 ? table[8] : table[1];
        if (wrong == table[1]) Fail("a hat that squares in 32 bits reads the diagonal too, so the check cannot tell it apart");
        else Console.WriteLine($"a hat that squares in 32 bits reads {wrong:X2} (centered, the sum wraps to {sum32}), which the check above fails");
    }
}
