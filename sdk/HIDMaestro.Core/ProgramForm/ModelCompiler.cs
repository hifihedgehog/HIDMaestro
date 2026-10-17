using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal;

/// <summary>How a layout goes out.</summary>
internal enum SendRule : byte
{
    /// <summary>On the device clock.</summary>
    Clock,
    /// <summary>When its bytes differ from the last it sent.</summary>
    Change,
    /// <summary>On every SubmitState.</summary>
    Submit,
}

/// <summary>What advances a counter.</summary>
internal enum CounterEvent : byte { Report, Frame, Clock, Read }

/// <summary>A layout, compiled: its program in the validated form.</summary>
internal sealed record LayoutForm(string Name, int ReportId, int Size, Condition? While, SendRule Send, ValidProgram Program);

/// <summary>A decoder, compiled: the reports it applies to, the bytes it needs
/// and the names of the values its program writes, one byte each.</summary>
internal sealed record DecoderForm(string Name, byte[] Reports, int MinLength, string[] Semantics, ValidProgram Program);

/// <summary>A report raised in place of a command, compiled.</summary>
internal sealed record EmitForm(int ReportId, int Size, ValidProgram Program);

/// <summary>The registers a protocol declares, by name, with their widths and
/// initial values.</summary>
internal sealed class RegisterTable
{
    public static readonly RegisterTable Empty = new(Array.Empty<string>(), Array.Empty<int>(), Array.Empty<uint>());

    public string[] Names { get; }
    public int[] Bits { get; }
    public uint[] Initial { get; }
    /// <summary>The registers a port reset leaves as they are.</summary>
    public bool[] KeepOnPortReset { get; }

    public RegisterTable(string[] names, int[] bits, uint[] initial, bool[]? keepOnPortReset = null)
    {
        Names = names; Bits = bits; Initial = initial; KeepOnPortReset = keepOnPortReset ?? new bool[names.Length];
    }

    public int IndexOf(string name) => Array.IndexOf(Names, name);

    public uint WidthMask(int index) => Bits[index] == 32 ? uint.MaxValue : (1u << Bits[index]) - 1;
}

/// <summary>A profile's model groups compiled into the validated form: the
/// registers and counters, every layout, decoder, live feature report and
/// emitted report as a <see cref="ValidProgram"/>. The build tool emits
/// kernels for these programs and the SDK runs them, so both make them
/// here.</summary>
internal sealed class CompiledModel
{
    public required RegisterTable Registers { get; init; }
    public required string[] CounterNames { get; init; }
    public required int[] CounterBits { get; init; }
    public required CounterEvent[] CounterOn { get; init; }
    public required double[] CounterPeriodUs { get; init; }
    public required uint[] CounterInitial { get; init; }
    public required int StateSize { get; init; }
    public required LayoutForm[] Layouts { get; init; }
    public required DecoderForm[] Decoders { get; init; }
    public required EmitForm?[] RuleEmits { get; init; }
    public required ValidProgram[] LiveFeatures { get; init; }
    public required int[] LiveFeatureIds { get; init; }

    /// <summary>The layouts as one set, in their order, for the clock that
    /// sends them.</summary>
    public ProgramSet? LayoutSet => Layouts.Length == 0 ? null : new ProgramSet(Layouts.Select(l => l.Program).ToArray());

    /// <summary>Every program, for the build tool.</summary>
    public IEnumerable<ValidProgram> AllPrograms =>
        Layouts.Select(l => l.Program).Concat(Decoders.Select(d => d.Program))
               .Concat(RuleEmits.Where(e => e != null).Select(e => e!.Program)).Concat(LiveFeatures);
}

/// <summary>Lowers a profile's model groups into the validated form. Every
/// refusal is a <see cref="FormException"/> whose path starts at the file's
/// root, so the build tool and the loader report it alike.</summary>
internal static class ModelCompiler
{
    static readonly CultureInfo I = CultureInfo.InvariantCulture;

    public static CompiledModel Compile(DeviceModelGroups g)
    {
        var regs = Registers(g.Protocol);
        var (counterNames, counterBits, counterOn, counterPeriod, counterInitial) = Counters(g.Protocol);

        int stateSize = 0;
        if (g.StateBlock != null)
        {
            stateSize = g.StateBlock.Size;
            if (stateSize < 1 || stateSize > ProgramForm.MaxState)
                throw new FormException("$.stateBlock.size", $"{stateSize} is outside 1 to {ProgramForm.MaxState}");
        }

        // A state block is what a device's own reports are built from. With
        // nothing to build, SubmitState would hand the bare block to the
        // transport as the input report.
        if (g.StateBlock != null && g.InputReports == null && g.LiveFeatureReports == null)
            throw new FormException("$.stateBlock", "no inputReports or liveFeatureReports reads it: a state block goes with the reports a device builds from it");

        var layouts = new List<LayoutForm>();
        if (g.InputReports != null)
        {
            if (g.StateBlock == null) throw new FormException("$.inputReports", "a device that builds its reports needs a stateBlock to build them from");
            if (g.InputReports.Count == 0) throw new FormException("$.inputReports", "the list is empty: leave it out for a device that sends the SDK's report");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < g.InputReports.Count; i++)
            {
                string path = $"$.inputReports[{i}]";
                var spec = g.InputReports[i] ?? throw new FormException(path, "the entry is null");
                if (string.IsNullOrWhiteSpace(spec.Name)) throw new FormException(path + ".name", "a layout needs a name");
                if (!names.Add(spec.Name)) throw new FormException(path + ".name", $"'{spec.Name}' names two layouts");
                int id = spec.ReportId == null ? -1 : ByteValue(spec.ReportId, path + ".reportId");
                var send = spec.Send switch
                {
                    null or "clock" => SendRule.Clock,
                    "change" => SendRule.Change,
                    "submit" => SendRule.Submit,
                    _ => throw new FormException(path + ".send", $"\"{spec.Send}\" is not one of clock, change, submit"),
                };
                var when = spec.While == null ? null : ConditionParser.Parse(spec.While, regs, path + ".while");
                var program = Program(spec.Name, spec.Size, stateSize, regs, counterNames, spec.Program, path + ".program", path + ".size", reportId: id);
                layouts.Add(new LayoutForm(spec.Name, id, spec.Size, when, send, program));
            }
            // Two layouts on one report ID may share it only when their
            // conditions cannot hold together.
            for (int a = 0; a < layouts.Count; a++)
                for (int b = a + 1; b < layouts.Count; b++)
                    if (layouts[a].ReportId == layouts[b].ReportId)
                        Disjoint(layouts[a].While, layouts[b].While, regs, $"$.inputReports[{b}].while", layouts[a].Name);
        }

        var decoders = new List<DecoderForm>();
        if (g.Protocol?.Decoders is { } decs)
        {
            foreach (var (name, spec) in decs)
                decoders.Add(Decoder(name, spec, $"$.protocol.decoders.{name}"));
        }

        var emits = new List<EmitForm?>();
        if (g.Protocol?.Commands?.Rules is { } rules)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule?.EmitOutput is not { } e) { emits.Add(null); continue; }
                string path = $"$.protocol.commands.rules[{i}].emitOutput";
                int state = rule.MinLength ?? 0;
                if (state < 1) throw new FormException($"$.protocol.commands.rules[{i}].minLength", "a rule that emits a report reads its command, so it needs a minLength");
                int id = ByteValue(e.ReportId, path + ".reportId");
                emits.Add(new EmitForm(id, e.Size, Program("emit", e.Size, state, regs, counterNames, e.Program, path + ".program", path + ".size", reportId: id)));
            }
        }

        var live = new List<ValidProgram>();
        var liveIds = new List<int>();
        if (g.LiveFeatureReports != null)
        {
            if (g.StateBlock == null) throw new FormException("$.liveFeatureReports", "a live feature report needs a stateBlock to build from");
            for (int i = 0; i < g.LiveFeatureReports.Count; i++)
            {
                string path = $"$.liveFeatureReports[{i}]";
                var spec = g.LiveFeatureReports[i] ?? throw new FormException(path, "the entry is null");
                int id = ByteValue(spec.ReportId, path + ".reportId");
                if (liveIds.Contains(id)) throw new FormException(path + ".reportId", $"0x{id:X2} names two live feature reports");
                liveIds.Add(id);
                live.Add(Program("feature", spec.Size, stateSize, regs, counterNames, spec.Program, path + ".program", path + ".size", reportId: id));
            }
        }

        return new CompiledModel
        {
            Registers = regs,
            CounterNames = counterNames, CounterBits = counterBits, CounterOn = counterOn, CounterPeriodUs = counterPeriod, CounterInitial = counterInitial,
            StateSize = stateSize,
            Layouts = layouts.ToArray(),
            Decoders = decoders.ToArray(),
            RuleEmits = emits.ToArray(),
            LiveFeatures = live.ToArray(),
            LiveFeatureIds = liveIds.ToArray(),
        };
    }

    /// <summary>Refuses an op other than a top-level const that writes byte 0
    /// of a report whose byte 0 is its ID, with the op's path in the form.
    /// Every op's writes start at its destination, so an op writes byte 0
    /// when its destination is 0, and a bit move when its report bit is below
    /// 8.</summary>
    static void ByteZeroIsTheId(ProgramSpec p, int id)
    {
        static bool AtZero(Op op) => op switch
        {
            CopyOp c => c.Dst == 0,
            FieldOp f => f.Dst == 0,
            CounterOp k => k.Dst == 0,
            Swap16Op w => w.Dst == 0,
            Pack12Op k => k.Dst == 0,
            Hat8Op h => h.Dst == 0,
            MaxOfOp m => m.Dst == 0,
            LookupOp l => l.Dst == 0,
            XorOp x => x.Dst == 0,
            ConstOp c => c.At == 0,
            BitsOp b => Enumerable.Range(0, b.Moves.Length / 2).Any(k => b.Moves[2 * k + 1] < 8),
            _ => false,
        };
        string why = $"writes byte 0, the report ID 0x{id:X2} a const puts there";
        for (int i = 0; i < p.Ops.Length; i++)
        {
            if (p.Ops[i] is WhenOp w)
            {
                for (int j = 0; j < w.Ops.Length; j++)
                    if (AtZero(w.Ops[j])) throw new FormException($"$.ops[{i}].ops[{j}]", why);
            }
            else if (p.Ops[i] is not ConstOp && AtZero(p.Ops[i])) throw new FormException($"$.ops[{i}]", why);
        }
    }

    // ── Registers and counters ───────────────────────────────────────────

    public static RegisterTable Registers(ProtocolSpec? protocol)
    {
        if (protocol is { StateWritten: true, State: null }) throw new FormException("$.protocol.state", "the list is null: give one, or leave the key out for a device with no registers");
        if (protocol?.State is not { } state) return RegisterTable.Empty;
        if (state.Count > ProgramForm.MaxRegisters)
            throw new FormException("$.protocol.state", $"{state.Count} registers is past the {ProgramForm.MaxRegisters} a device has");
        var names = new string[state.Count];
        var bits = new int[state.Count];
        var initial = new uint[state.Count];
        var keep = new bool[state.Count];
        for (int i = 0; i < state.Count; i++)
        {
            string path = $"$.protocol.state[{i}]";
            var r = state[i] ?? throw new FormException(path, "the entry is null");
            if (!IsName(r.Name)) throw new FormException(path + ".name", $"'{r.Name}' is not a register name: letters, digits and _, starting with a letter");
            if (Array.IndexOf(names, r.Name, 0, i) >= 0) throw new FormException(path + ".name", $"'{r.Name}' names two registers");
            names[i] = r.Name;
            bits[i] = r.Bits ?? 1;
            if (bits[i] < 1 || bits[i] > 32) throw new FormException(path + ".bits", "a register is 1 to 32 bits");
            initial[i] = r.Initial?.Value ?? 0;
            uint mask = bits[i] == 32 ? uint.MaxValue : (1u << bits[i]) - 1;
            if ((initial[i] & ~mask) != 0) throw new FormException(path + ".initial", $"0x{initial[i]:X} does not fit {bits[i]} bits");
            keep[i] = r.KeepOnPortReset ?? false;
        }
        return new RegisterTable(names, bits, initial, keep);
    }

    static (string[], int[], CounterEvent[], double[], uint[]) Counters(ProtocolSpec? protocol)
    {
        var list = protocol?.Counters;
        if (list == null) return (Array.Empty<string>(), Array.Empty<int>(), Array.Empty<CounterEvent>(), Array.Empty<double>(), Array.Empty<uint>());
        if (list.Count > ProgramForm.MaxCounters)
            throw new FormException("$.protocol.counters", $"{list.Count} counters is past the {ProgramForm.MaxCounters} a device has");
        var names = new string[list.Count];
        var bits = new int[list.Count];
        var on = new CounterEvent[list.Count];
        var period = new double[list.Count];
        var initial = new uint[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            string path = $"$.protocol.counters[{i}]";
            var c = list[i] ?? throw new FormException(path, "the entry is null");
            if (!IsName(c.Name)) throw new FormException(path + ".name", $"'{c.Name}' is not a counter name");
            if (Array.IndexOf(names, c.Name, 0, i) >= 0) throw new FormException(path + ".name", $"'{c.Name}' names two counters");
            names[i] = c.Name;
            bits[i] = c.Bits ?? 32;
            if (bits[i] < 1 || bits[i] > 32) throw new FormException(path + ".bits", "a counter is 1 to 32 bits");
            on[i] = c.On switch
            {
                "report" => CounterEvent.Report,
                "frame" => CounterEvent.Frame,
                "clock" => CounterEvent.Clock,
                "read" => CounterEvent.Read,
                _ => throw new FormException(path + ".on", $"\"{c.On}\" is not one of report, frame, clock, read"),
            };
            if (on[i] == CounterEvent.Clock)
            {
                if (c.PeriodUs is not double p || !(p > 0) || double.IsInfinity(p)) throw new FormException(path + ".periodUs", "a clock counter needs a period above 0");
                period[i] = p;
            }
            else if (c.PeriodUs != null) throw new FormException(path + ".periodUs", "a period goes with \"on\": \"clock\" only");
            initial[i] = c.Initial?.Value ?? 0;
            uint mask = bits[i] == 32 ? uint.MaxValue : (1u << bits[i]) - 1;
            if ((initial[i] & ~mask) != 0) throw new FormException(path + ".initial", $"0x{initial[i]:X} does not fit {bits[i]} bits");
        }
        return (names, bits, on, period, initial);
    }

    static bool IsName(string? s)
    {
        if (string.IsNullOrEmpty(s) || !char.IsAsciiLetter(s[0])) return false;
        foreach (char ch in s) if (!char.IsAsciiLetterOrDigit(ch) && ch != '_') return false;
        return true;
    }

    // ── Programs ─────────────────────────────────────────────────────────

    /// <summary>One program from its ops as a profile writes them.</summary>
    /// <summary>A layout's, decoder's or report's program lowered and
    /// validated. With <paramref name="reportId"/> 0 or more, byte 0 is that
    /// report ID: a top-level const puts it there, and no other op writes
    /// it.</summary>
    public static ValidProgram Program(string name, int size, int stateSize, RegisterTable regs, string[] counters,
        List<ProgramOpSpec>? ops, string path, string sizePath, int reportId = -1)
    {
        if (ops == null) throw new FormException(path, "a layout needs a program");
        if (size < 1 || size > ProgramForm.MaxReport) throw new FormException(sizePath, $"{size} is outside 1 to {ProgramForm.MaxReport}");
        var ir = new List<Op>();
        var paths = new List<string>();
        var subPaths = new List<List<string>?>();
        for (int i = 0; i < ops.Count; i++)
            Lower(ops[i], $"{path}[{i}]", regs, counters, ir, paths, subPaths, inBlock: false);
        var spec = new ProgramSpec(name, size, stateSize, regs.Bits, ir.ToArray());
        ValidProgram valid;
        try
        {
            valid = ProgramForm.Validate(spec);
            if (reportId >= 0) ByteZeroIsTheId(spec, reportId);
        }
        catch (FormException e) { throw new FormException(MapPath(e.Path, path, sizePath, paths, subPaths, ir, ops), e.Reason, e); }
        if (reportId >= 0 && valid.Template[0] != reportId)
            throw new FormException(path, $"byte 0 is the report ID 0x{reportId:X2}: a const must put it there");
        return valid;
    }

    /// <summary>The validator's name for a value of <paramref name="op"/>,
    /// <paramref name="rest"/>, as the profile writes it in
    /// <paramref name="spec"/>: a field's read is "field", its ratio "scale"
    /// and its bounds "clamp", a hat's and a pack12's two reads the two
    /// entries of their list, a lookup's read "lookup", a copy's, xor's or
    /// swap16's source its range, a counter's slot its name, a bit move the
    /// pair it came from or the mask of the copy it came from, and any term of
    /// a condition the condition's text.</summary>
    static string Authored(string rest, Op op, ProgramOpSpec? spec)
    {
        static bool Is(string rest, string name) =>
            rest == name || rest.StartsWith(name + ".", StringComparison.Ordinal) || rest.StartsWith(name + "[", StringComparison.Ordinal);
        string Swap(params (string ir, string authored)[] names)
        {
            foreach (var (n, a) in names)
                if (Is(rest, n)) return a + rest[n.Length..];
            return rest;
        }
        if (Is(rest, ".when")) return ".when";
        switch (op)
        {
            case FieldOp: return Swap((".from", ".field"), (".num", ".scale"), (".den", ".scale"), (".min", ".clamp"), (".max", ".clamp"));
            case Hat8Op: return Swap((".x", ".hat8[0]"), (".y", ".hat8[1]"));
            case Pack12Op: return Swap((".x", ".pack12[0]"), (".y", ".pack12[1]"));
            case LookupOp: return Swap((".bits", ".lookup.bits"), (".bit", ".lookup.bit"));
            case CopyOp: return Swap((".src", ".copy"));
            case XorOp: return Swap((".src", ".xor"));
            case Swap16Op: return Swap((".src", ".swap16"), (".words", ".swap16"));
            case CounterOp: return Swap((".slot", ".counter"));
            case BitsOp when spec?.Bits == null:
                return Is(rest, ".moves") ? ".mask" : rest;
            case BitsOp:
                if (rest.StartsWith(".moves[", StringComparison.Ordinal))
                {
                    int close = rest.IndexOf(']');
                    if (int.TryParse(rest.AsSpan(7, close - 7), NumberStyles.None, I, out int k))
                        return $".bits[{k / 2}][{k % 2}]{rest[(close + 1)..]}";
                }
                return Swap((".moves", ".bits"));
            default: return rest;
        }
    }

    /// <summary>The authoring op at <paramref name="authored"/>, a path under
    /// <paramref name="programPath"/> such as "[3]" or "[3].ops[1]".</summary>
    static ProgramOpSpec? AuthoredOp(string authored, string programPath, List<ProgramOpSpec>? ops)
    {
        if (ops == null || !authored.StartsWith(programPath + "[", StringComparison.Ordinal)) return null;
        string rest = authored[programPath.Length..];
        int close = rest.IndexOf(']');
        if (!int.TryParse(rest.AsSpan(1, close - 1), NumberStyles.None, I, out int j) || j >= ops.Count) return null;
        var op = ops[j];
        rest = rest[(close + 1)..];
        if (!rest.StartsWith(".ops[", StringComparison.Ordinal) || op?.Ops == null) return op;
        close = rest.IndexOf(']');
        return int.TryParse(rest.AsSpan(5, close - 5), NumberStyles.None, I, out int k) && k < op.Ops.Count ? op.Ops[k] : null;
    }

    /// <summary>The validator's path, "$.ops[i]..." over the lowered ops, as
    /// the profile's own path.</summary>
    static string MapPath(string irPath, string programPath, string sizePath, List<string> paths, List<List<string>?> subPaths,
        List<Op> ir, List<ProgramOpSpec>? ops)
    {
        if (irPath == "$.size") return sizePath;
        if (irPath == "$.stateSize") return "$.stateBlock.size";
        if (!irPath.StartsWith("$.ops[", StringComparison.Ordinal)) return programPath;
        int close = irPath.IndexOf(']');
        if (!int.TryParse(irPath.AsSpan(6, close - 6), NumberStyles.None, I, out int i) || i >= paths.Count) return programPath;
        string rest = irPath[(close + 1)..];
        if (rest.StartsWith(".ops[", StringComparison.Ordinal) && subPaths[i] is { } sub && ir[i] is WhenOp block)
        {
            int c2 = rest.IndexOf(']');
            if (int.TryParse(rest.AsSpan(5, c2 - 5), NumberStyles.None, I, out int j) && j < sub.Count)
                return sub[j] + Authored(rest[(c2 + 1)..], block.Ops[j], AuthoredOp(sub[j], programPath, ops));
        }
        return paths[i] + Authored(rest, ir[i], AuthoredOp(paths[i], programPath, ops));
    }

    static void Lower(ProgramOpSpec? o, string path, RegisterTable regs, string[] counters,
        List<Op> ir, List<string> paths, List<List<string>?> subPaths, bool inBlock)
    {
        if (o == null) throw new FormException(path, "the op is null");
        string kind = OneKind(o, path);
        void Add(Op op) { ir.Add(op); paths.Add(path); subPaths.Add(null); }
        int To() => o.To ?? throw new FormException(path + ".to", $"a {kind} needs \"to\"");
        bool BigEndian()
        {
            return o.Order switch
            {
                null or "le" => false,
                "be" => true,
                _ => throw new FormException(path + ".order", $"\"{o.Order}\" is not le or be"),
            };
        }
        switch (kind)
        {
            case "const":
            {
                Allowed(o, path, kind);
                if (o.Const!.Count == 0) throw new FormException(path + ".const", "no bytes");
                // By offset, so the order the keys are written in does not
                // change the program's canonical form.
                var entries = new List<(int at, byte[] bytes, string key)>();
                foreach (var (key, hex) in o.Const)
                {
                    if (!int.TryParse(key, NumberStyles.None, I, out int at)) throw new FormException($"{path}.const[\"{key}\"]", "the key is a report offset");
                    if (!ModelText.TryParseBytes(hex, out var bytes)) throw new FormException($"{path}.const[\"{key}\"]", $"\"{hex}\" is not hex bytes");
                    if ((long)at + bytes.Length > ProgramForm.MaxReport)
                        throw new FormException($"{path}.const[\"{key}\"]", $"report bytes {at} to {(long)at + bytes.Length - 1} are outside 0 to {ProgramForm.MaxReport - 1}, the most a report holds");
                    entries.Add((at, bytes, key));
                }
                foreach (var (at, bytes, _) in entries.OrderBy(e => e.at)) Add(new ConstOp(at, bytes));
                break;
            }
            case "copy":
            {
                Allowed(o, path, kind, "to", "mask");
                var (lo, hi) = Range(o.Copy, path + ".copy");
                int to = To();
                if (o.Mask == null) { Add(new CopyOp(lo, to, hi - lo + 1)); break; }
                if (inBlock) throw new FormException(path + ".mask", "a copy with a mask is bit moves, which a conditional block cannot hold");
                byte mask = HexByte(o.Mask, path + ".mask");
                // The bounds before the moves are listed: a range past the
                // state block would list billions of them.
                if (hi >= ProgramForm.MaxState) throw new FormException(path + ".copy", $"state bytes {lo} to {hi} are outside 0 to {ProgramForm.MaxState - 1}, the most a state block holds");
                if (to < 0 || (long)to + (hi - lo) >= ProgramForm.MaxReport)
                    throw new FormException(path + ".to", $"report bytes {to} to {(long)to + (hi - lo)} are outside 0 to {ProgramForm.MaxReport - 1}");
                var moves = new List<int>();
                for (int k = 0; k <= hi - lo; k++)
                    for (int b = 0; b < 8; b++)
                        if ((mask & (1 << b)) != 0) { moves.Add((lo + k) * 8 + b); moves.Add((to + k) * 8 + b); }
                if (moves.Count == 0) throw new FormException(path + ".mask", "the mask moves no bit");
                Add(new BitsOp(moves.ToArray()));
                break;
            }
            case "bits":
            {
                Allowed(o, path, kind);
                var moves = new List<int>();
                for (int k = 0; k < o.Bits!.Count; k++)
                {
                    var pair = o.Bits[k];
                    if (pair == null || pair.Count != 2) throw new FormException($"{path}.bits[{k}]", "a move is [state bit, report bit]");
                    moves.Add(pair[0]); moves.Add(pair[1]);
                }
                Add(new BitsOp(moves.ToArray()));
                break;
            }
            case "xor":
            {
                Allowed(o, path, kind, "to", "mask");
                var (lo, hi) = Range(o.Xor, path + ".xor");
                byte mask = o.Mask == null ? throw new FormException(path + ".mask", "an xor needs a mask") : HexByte(o.Mask, path + ".mask");
                Add(new XorOp(lo, To(), hi - lo + 1, mask));
                break;
            }
            case "swap16":
            {
                Allowed(o, path, kind, "to");
                var (lo, hi) = Range(o.Swap16, path + ".swap16");
                if (((hi - lo + 1) & 1) != 0) throw new FormException(path + ".swap16", "a swap16 range holds whole 16-bit words");
                Add(new Swap16Op(lo, To(), (hi - lo + 1) / 2));
                break;
            }
            case "field":
            {
                Allowed(o, path, kind, "to", "center", "negate", "scale", "offset", "clamp", "overflow", "bytes", "order");
                var src = Read(o.Field!, path + ".field");
                int bytes = o.Bytes ?? 1;
                var (num, den) = Ratio(o.Scale, path + ".scale");
                bool wrap = o.Overflow switch
                {
                    null => false,
                    "wrap16" => true,
                    _ => throw new FormException(path + ".overflow", $"\"{o.Overflow}\" is not wrap16"),
                };
                int min = 0, max = bytes == 1 ? 255 : 65535;
                if (o.Clamp != null)
                {
                    if (wrap) throw new FormException(path + ".clamp", "a field that wraps is not clamped");
                    if (o.Clamp.Count != 2) throw new FormException(path + ".clamp", "a clamp is [min, max]");
                    min = o.Clamp[0]; max = o.Clamp[1];
                }
                Add(new FieldOp(src, o.Center ?? 0, o.Negate ?? false, num, den, o.Offset ?? 0, min, max, wrap, To(), bytes, BigEndian()));
                break;
            }
            case "lookup":
            {
                Allowed(o, path, kind, "to", "table");
                var r = o.Lookup!;
                if (r.Bit == null || r.Bits == null || r.Byte != null || r.Word != null || r.Order != null)
                    throw new FormException(path + ".lookup", "a lookup reads { \"bit\", \"bits\" }");
                Add(new LookupOp(r.Bit.Value, r.Bits.Value, To(), Table(o.Table, path + ".table")));
                break;
            }
            case "hat8":
            {
                Allowed(o, path, kind, "to", "center", "deadzone", "yUp", "table");
                if (o.Hat8!.Count != 2) throw new FormException(path + ".hat8", "a hat reads [x, y]");
                var x = Read(o.Hat8[0], path + ".hat8[0]");
                var y = Read(o.Hat8[1], path + ".hat8[1]");
                int center = o.Center ?? throw new FormException(path + ".center", "a hat needs its center");
                int dz = o.Deadzone ?? throw new FormException(path + ".deadzone", "a hat needs its dead zone in counts");
                Add(new Hat8Op(x, y, center, dz, o.YUp ?? false, Table(o.Table, path + ".table"), To()));
                break;
            }
            case "pack12":
            {
                Allowed(o, path, kind, "to");
                if (o.Pack12!.Count != 2) throw new FormException(path + ".pack12", "a pack12 reads [x offset, y offset]");
                Add(new Pack12Op(o.Pack12[0], o.Pack12[1], To()));
                break;
            }
            case "counter":
            {
                Allowed(o, path, kind, "at", "width", "scale", "add", "order");
                int slot = Array.IndexOf(counters, o.Counter);
                if (slot < 0) throw new FormException(path + ".counter", $"'{o.Counter}' is not a counter protocol.counters declares");
                int width = o.Width ?? 8;
                if (width is not (8 or 16 or 24 or 32)) throw new FormException(path + ".width", "a counter's width is 8, 16, 24 or 32 bits");
                int at = o.At ?? throw new FormException(path + ".at", "a counter needs \"at\"");
                uint scale = 1;
                if (o.Scale != null)
                {
                    if (o.Scale.Denominator != 1 || o.Scale.Numerator < 0 || o.Scale.Numerator > uint.MaxValue || o.Scale.Numerator != Math.Floor(o.Scale.Numerator))
                        throw new FormException(path + ".scale", "a counter's scale is a whole number from 0 to 4294967295");
                    scale = (uint)o.Scale.Numerator;
                }
                Add(new CounterOp(slot, at, width / 8, scale, o.Add?.Value ?? 0, BigEndian()));
                break;
            }
            case "when":
            {
                Allowed(o, path, kind, "ops");
                if (inBlock) throw new FormException(path + ".when", "a conditional block holds no block");
                var cond = ConditionParser.Parse(o.When!, regs, path + ".when");
                if (o.Ops == null || o.Ops.Count == 0) throw new FormException(path + ".ops", "a block needs an op");
                var inner = new List<Op>();
                var innerPaths = new List<string>();
                var innerSub = new List<List<string>?>();
                for (int k = 0; k < o.Ops.Count; k++)
                    Lower(o.Ops[k], $"{path}.ops[{k}]", regs, counters, inner, innerPaths, innerSub, inBlock: true);
                ir.Add(new WhenOp(cond, inner.ToArray()));
                paths.Add(path);
                subPaths.Add(innerPaths);
                break;
            }
        }
    }

    static readonly string[] Kinds = { "const", "copy", "bits", "xor", "swap16", "field", "lookup", "hat8", "pack12", "counter", "when" };

    static string OneKind(ProgramOpSpec o, string path)
    {
        var set = new List<string>();
        if (o.Const != null) set.Add("const");
        if (o.Copy != null) set.Add("copy");
        if (o.Bits != null) set.Add("bits");
        if (o.Xor != null) set.Add("xor");
        if (o.Swap16 != null) set.Add("swap16");
        if (o.Field != null) set.Add("field");
        if (o.Lookup != null) set.Add("lookup");
        if (o.Hat8 != null) set.Add("hat8");
        if (o.Pack12 != null) set.Add("pack12");
        if (o.Counter != null) set.Add("counter");
        if (o.When != null) set.Add("when");
        if (set.Count == 0) throw new FormException(path, $"the op names none of {string.Join(", ", Kinds)}");
        if (set.Count > 1) throw new FormException(path, $"the op names {string.Join(" and ", set)}: one op per entry");
        return set[0];
    }

    /// <summary>Refuses a parameter the op does not take, by its path.</summary>
    static void Allowed(ProgramOpSpec o, string path, string kind, params string[] keys)
    {
        void Check(object? value, string key)
        {
            if (value != null && Array.IndexOf(keys, key) < 0) throw new FormException($"{path}.{key}", $"a {kind} takes no \"{key}\"");
        }
        Check(o.To, "to"); Check(o.At, "at"); Check(o.Mask, "mask"); Check(o.Center, "center"); Check(o.Negate, "negate");
        Check(o.Scale, "scale"); Check(o.Offset, "offset"); Check(o.Clamp, "clamp"); Check(o.Overflow, "overflow");
        Check(o.Bytes, "bytes"); Check(o.Order, "order"); Check(o.Width, "width"); Check(o.Add, "add");
        Check(o.Table, "table"); Check(o.Deadzone, "deadzone"); Check(o.YUp, "yUp"); Check(o.Ops, "ops");
    }

    static Src Read(ReadSpec r, string path)
    {
        if (r == null) throw new FormException(path, "the read is null");
        if (r.Word != null)
        {
            if (r.Bit != null || r.Bits != null || r.Byte != null) throw new FormException(path, "a word read takes \"word\" and \"order\" only");
            StateByte(r.Word.Value, 2, path + ".word", "word");
            return r.Order switch
            {
                null or "le" => new Src(r.Word.Value * 8, 16),
                "be" => new Src(r.Word.Value * 8, 16, BigEndianWord: true),
                _ => throw new FormException(path + ".order", $"\"{r.Order}\" is not le or be"),
            };
        }
        if (r.Order != null) throw new FormException(path + ".order", "an order goes with a word read");
        if (r.Byte != null)
        {
            if (r.Bit != null || r.Bits != null) throw new FormException(path, "a byte read takes \"byte\" only");
            StateByte(r.Byte.Value, 1, path + ".byte", "byte");
            return new Src(r.Byte.Value * 8, 8);
        }
        if (r.Bit == null || r.Bits == null) throw new FormException(path, "a read is { \"bit\", \"bits\" }, { \"byte\" } or { \"word\", \"order\" }");
        return new Src(r.Bit.Value, r.Bits.Value);
    }

    /// <summary>A byte offset of a read, refused before it is scaled to bits,
    /// where an offset past 2^28 would wrap. Validation checks the bit offset
    /// against the state block's own size.</summary>
    static void StateByte(long at, int len, string path, string what)
    {
        if (at < 0 || at + len > ProgramForm.MaxState)
            throw new FormException(path, $"{what} {at} is outside 0 to {ProgramForm.MaxState - len}, the most a state block holds");
    }

    static (int num, int den) Ratio(ScaleSpec? s, string path)
    {
        if (s == null) return (1, 1);
        bool whole(double v) => v == Math.Floor(v) && v >= int.MinValue && v <= int.MaxValue;
        if (!whole(s.Numerator) || !whole(s.Denominator) || s.Denominator == 0)
            throw new FormException(path, "a field's scale is a ratio of whole numbers, [numerator, denominator], the denominator not 0");
        return ((int)s.Numerator, (int)s.Denominator);
    }

    static (int lo, int hi) Range(string? text, string path) =>
        ModelText.TryParseRange(text, out int lo, out int hi) ? (lo, hi) : throw new FormException(path, $"\"{text}\" is not a byte range such as \"3-8\"");

    static byte HexByte(string text, string path) =>
        ModelText.TryParseBytes(text, out var b) && b.Length == 1 ? b[0] : throw new FormException(path, $"\"{text}\" is not one hex byte");

    static byte[] Table(string? text, string path) =>
        ModelText.TryParseBytes(text, out var b) ? b : throw new FormException(path, "the table is hex bytes");

    public static int ByteValue(string text, string path) =>
        ModelText.TryParseHex(text, out uint v) && v <= 0xFF ? (int)v : throw new FormException(path, $"\"{text}\" is not a byte, such as \"0x09\"");

    // ── Layout overlap ───────────────────────────────────────────────────

    /// <summary>Refuses two layouts on one report ID whose conditions can hold
    /// together, by enumerating every value of the bits the two conditions
    /// test, up to 16 bits in all.</summary>
    static void Disjoint(Condition? a, Condition? b, RegisterTable regs, string path, string other)
    {
        if (a == null || b == null)
            throw new FormException(path, $"layout '{other}' shares this report ID, and one of the two has no condition, so both can hold");
        // The tested bits, per register.
        var masks = new Dictionary<int, uint>();
        foreach (var c in new[] { a, b })
            foreach (var group in c.Groups)
                foreach (var t in group)
                    masks[t.Register] = (masks.TryGetValue(t.Register, out var m) ? m : 0) | t.Mask;
        var bits = new List<(int reg, int bit)>();
        foreach (var (reg, mask) in masks.OrderBy(k => k.Key))
            for (int i = 0; i < 32; i++)
                if ((mask & (1u << i)) != 0) bits.Add((reg, i));
        if (bits.Count > 16)
            throw new FormException(path, $"this layout and '{other}' share a report ID and test {bits.Count} bits together, past the 16 validation can enumerate: separate the layouts on fewer bits");
        var r = new uint[regs.Bits.Length];
        for (int v = 0; v < 1 << bits.Count; v++)
        {
            Array.Clear(r);
            for (int i = 0; i < bits.Count; i++)
                if ((v & (1 << i)) != 0) r[bits[i].reg] |= 1u << bits[i].bit;
            if (a.Holds(r) && b.Holds(r))
                throw new FormException(path, $"this layout and '{other}' share a report ID, and both hold when {Describe(r, masks, regs)}");
        }
    }

    static string Describe(uint[] r, Dictionary<int, uint> masks, RegisterTable regs) =>
        string.Join(", ", masks.Keys.OrderBy(k => k).Select(k => $"{regs.Names[k]} is 0x{r[k]:X}"));

    // ── Decoders ─────────────────────────────────────────────────────────

    static DecoderForm Decoder(string name, DecoderSpec? spec, string path)
    {
        if (spec == null) throw new FormException(path, "the decoder is null");
        if (!IsName(name)) throw new FormException(path, $"'{name}' is not a decoder name");
        if (spec.MinLength < 1 || spec.MinLength > ProgramForm.MaxState) throw new FormException(path + ".minLength", $"{spec.MinLength} is outside 1 to {ProgramForm.MaxState}");
        if (spec.Fields == null || spec.Fields.Count == 0 || spec.Fields.Count > ProgramForm.MaxReport) throw new FormException(path + ".fields", $"a decoder has 1 to {ProgramForm.MaxReport} fields");
        var reports = new List<byte>();
        if (spec.Reports != null)
            for (int i = 0; i < spec.Reports.Count; i++) reports.Add((byte)ByteValue(spec.Reports[i], $"{path}.reports[{i}]"));
        var ops = new List<Op>();
        var semantics = new string[spec.Fields.Count];
        // The part each term of each field came from, to name it in a refusal.
        var partOf = new List<int>[spec.Fields.Count];
        for (int f = 0; f < spec.Fields.Count; f++)
        {
            string fp = $"{path}.fields[{f}]";
            var field = spec.Fields[f] ?? throw new FormException(fp, "the field is null");
            if (field.Type != "packed-uint") throw new FormException(fp + ".type", $"\"{field.Type}\" is not a decoder field type: packed-uint");
            if (string.IsNullOrWhiteSpace(field.Semantic)) throw new FormException(fp + ".semantic", "a decoded field needs the name it is raised under");
            if (Array.IndexOf(semantics, field.Semantic, 0, f) >= 0) throw new FormException(fp + ".semantic", $"'{field.Semantic}' names two fields");
            semantics[f] = field.Semantic;
            if (field.Parts == null || field.Parts.Count == 0) throw new FormException(fp + ".parts", "a packed-uint needs a part");
            var terms = new List<DecodeTerm>();
            partOf[f] = new List<int>();
            for (int p = 0; p < field.Parts.Count; p++)
                foreach (var term in Terms(field.Parts[p], $"{fp}.parts[{p}]"))
                {
                    terms.Add(term);
                    partOf[f].Add(p);
                }
            ops.Add(new MaxOfOp(terms.ToArray(), f));
        }
        var program = new ProgramSpec("decoder:" + name, spec.Fields.Count, spec.MinLength, Array.Empty<int>(), ops.ToArray());
        ValidProgram valid;
        try { valid = ProgramForm.Validate(program); }
        catch (FormException e)
        {
            // "$.ops[f].terms[t].parts[q].bit" is piece q's "at" of the
            // part term t came from, in field f.
            string mapped = path;
            if (e.Path.StartsWith("$.ops[", StringComparison.Ordinal))
            {
                int close = e.Path.IndexOf(']');
                int f = int.Parse(e.Path.AsSpan(6, close - 6), NumberStyles.None, I);
                string rest = e.Path[(close + 1)..];
                mapped = $"{path}.fields[{f}]";
                if (rest.StartsWith(".terms[", StringComparison.Ordinal))
                {
                    int tc = rest.IndexOf(']');
                    int t = int.Parse(rest.AsSpan(7, tc - 7), NumberStyles.None, I);
                    mapped += $".parts[{partOf[f][t]}]";
                    rest = rest[(tc + 1)..];
                    if (rest.StartsWith(".parts", StringComparison.Ordinal))
                        mapped += ".pieces" + rest[6..].Replace("].bits", "].count", StringComparison.Ordinal).Replace("].bit", "].at", StringComparison.Ordinal);
                }
            }
            throw new FormException(mapped, e.Reason, e);
        }
        return new DecoderForm(name, reports.ToArray(), spec.MinLength, semantics, valid);
    }

    /// <summary>A part as one term per frame, each mapping the frame's value
    /// through the part's table. The decode keeps the largest mapped term. A
    /// part read over several frames needs a map that never decreases, so the
    /// largest mapped frame is the map of the largest frame, which is how the
    /// codec's packed-uint reads it.</summary>
    static IEnumerable<DecodeTerm> Terms(PackedPartSpec? part, string path)
    {
        if (part == null) throw new FormException(path, "the part is null");
        if (part.Pieces == null || part.Pieces.Count == 0) throw new FormException(path + ".pieces", "a part needs a piece");
        int frames = 1, stride = 0;
        if (part.Frames != null)
        {
            if (part.Frames.Count != 2 || part.Frames[0] < 1 || part.Frames[1] < 0) throw new FormException(path + ".frames", "frames is [count, stride], a count of 1 or more");
            frames = part.Frames[0]; stride = part.Frames[1];
            if (frames > 1 && stride < 1) throw new FormException(path + ".frames", "a part read over several frames needs a stride of 1 byte or more");
            // In long: the last frame's offset, scaled to bits, would wrap an int.
            if ((long)(frames - 1) * stride >= ProgramForm.MaxState)
                throw new FormException(path + ".frames", $"frame {frames - 1} starts at byte {(long)(frames - 1) * stride}, past the {ProgramForm.MaxState} bytes a report holds");
        }
        int bits = 0;
        for (int k = 0; k < part.Pieces.Count; k++)
        {
            var piece = part.Pieces[k] ?? throw new FormException($"{path}.pieces[{k}]", "the piece is null");
            if (piece.Count < 1 || piece.Count > ProgramForm.MaxDecodeBits) throw new FormException($"{path}.pieces[{k}].count", $"a piece is 1 to {ProgramForm.MaxDecodeBits} bits");
            if (piece.At < 0 || piece.At >= ProgramForm.MaxState * 8) throw new FormException($"{path}.pieces[{k}].at", $"a piece starts at bit 0 to {ProgramForm.MaxState * 8 - 1}");
            bits += piece.Count;
        }
        if (bits > ProgramForm.MaxDecodeBits) throw new FormException(path + ".pieces", $"{bits} bits is past the {ProgramForm.MaxDecodeBits} a term indexes");
        long lo = long.MinValue, hi = long.MaxValue;
        if (part.Range != null)
        {
            if (part.Range.Count != 2 || part.Range[0] > part.Range[1]) throw new FormException(path + ".range", "a range is [low, high], low first");
            lo = part.Range[0]; hi = part.Range[1];
        }
        double num = part.Scale?.Numerator ?? 1, den = part.Scale?.Denominator ?? 1;
        if (den == 0 || double.IsNaN(num) || double.IsInfinity(num) || double.IsNaN(den) || double.IsInfinity(den))
            throw new FormException(path + ".scale", "the scale is a finite ratio, its denominator not 0");
        bool down = part.Round switch
        {
            "down" => true,
            null or "nearest" => false,
            _ => throw new FormException(path + ".round", $"\"{part.Round}\" is not one of down, nearest"),
        };
        var table = new byte[1 << bits];
        for (int v = 0; v < table.Length; v++)
            table[v] = (byte)PackedMap.Level(v, part.Offset ?? 0, lo, hi, num, den, down);
        if (frames > 1)
            for (int v = 1; v < table.Length; v++)
                if (table[v] < table[v - 1])
                    throw new FormException(path + ".frames", $"the map decreases from value {v - 1} to {v}, and a part read over several frames keeps the largest");
        for (int fr = 0; fr < frames; fr++)
        {
            var srcs = new Src[part.Pieces.Count];
            for (int k = 0; k < srcs.Length; k++) srcs[k] = new Src(part.Pieces[k].At + fr * stride * 8, part.Pieces[k].Count);
            yield return new DecodeTerm(srcs, table);
        }
    }
}

/// <summary>The packed-uint map from a part's value to a level: plus the
/// offset, 0 outside the range, else scaled in double, rounded down or to
/// nearest with ties to even, and clamped to 0..255. The codec's packed-uint
/// and the decoders' tables both call this.</summary>
internal static class PackedMap
{
    public static int Level(long value, long offset, long lo, long hi, double numerator, double denominator, bool down)
    {
        long v = value + offset;
        if (v < lo || v > hi) return 0;
        double scaled = v * numerator / denominator;
        double r = down ? Math.Floor(scaled) : Math.Round(scaled);
        return (int)Math.Clamp(r, 0, 255);
    }
}

/// <summary>The condition grammar of the design note: terms join with &amp;&amp;
/// into a group, groups join with ||, without parentheses. A term tests a
/// register: <c>name</c> (nonzero), <c>!name</c> (zero), <c>name==V</c>,
/// <c>name!=V</c>, <c>name&amp;M</c> (any of the bits set), or
/// <c>name in a,b,c</c>. Every value is hex, with or without 0x.</summary>
internal static class ConditionParser
{
    /// <summary>A condition as a profile writes it.</summary>
    public static Condition Parse(ConditionText c, RegisterTable regs, string path)
    {
        if (c.NullTerm >= 0) throw new FormException($"{path}[{c.NullTerm}]", "the term is null");
        return Parse(c.Text, regs, path);
    }

    public static Condition Parse(string text, RegisterTable regs, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormException(path, "the condition is empty");
        var groups = new List<Term[]>();
        string[] groupTexts = text.Split("||");
        for (int g = 0; g < groupTexts.Length; g++)
        {
            var terms = new List<Term>();
            string[] termTexts = groupTexts[g].Split("&&");
            for (int t = 0; t < termTexts.Length; t++)
                terms.Add(ParseTerm(termTexts[t].Trim(), regs, path));
            groups.Add(terms.ToArray());
        }
        var c = new Condition(groups.ToArray());
        // The profile writes the condition as one text, so a refusal names
        // that text's path and the term at fault.
        try { ProgramForm.CheckCondition(path, c, regs.Bits); }
        catch (FormException e) when (e.Path.StartsWith(path + "[", StringComparison.Ordinal))
        {
            string rest = e.Path[path.Length..];
            int g1 = rest.IndexOf(']');
            int g = int.Parse(rest.AsSpan(1, g1 - 1), NumberStyles.None, CultureInfo.InvariantCulture);
            string term = groupTexts[g].Trim();
            if (rest.Length > g1 + 1 && rest[g1 + 1] == '[')
            {
                int t1 = rest.IndexOf(']', g1 + 1);
                int t = int.Parse(rest.AsSpan(g1 + 2, t1 - g1 - 2), NumberStyles.None, CultureInfo.InvariantCulture);
                term = groupTexts[g].Split("&&")[t].Trim();
            }
            throw new FormException(path, $"'{term}': {e.Reason}", e);
        }
        return c;
    }

    static Term ParseTerm(string s, RegisterTable regs, string path)
    {
        if (s.Length == 0) throw new FormException(path, "a term is empty");
        int Reg(string name)
        {
            int i = regs.IndexOf(name.Trim());
            if (i < 0) throw new FormException(path, $"'{name.Trim()}' is not a register protocol.state declares");
            return i;
        }
        uint Value(string v)
        {
            if (!ModelText.TryParseHex(v, out uint x)) throw new FormException(path, $"\"{v.Trim()}\" in '{s}' is not hex");
            return x;
        }
        int at;
        if ((at = s.IndexOf("!=", StringComparison.Ordinal)) > 0)
        {
            int r = Reg(s[..at]);
            return new Term(r, regs.WidthMask(r), Value(s[(at + 2)..]), NotEqual: true);
        }
        if ((at = s.IndexOf("==", StringComparison.Ordinal)) > 0)
        {
            int r = Reg(s[..at]);
            return new Term(r, regs.WidthMask(r), Value(s[(at + 2)..]), NotEqual: false);
        }
        if ((at = s.IndexOf('&')) > 0)
        {
            int r = Reg(s[..at]);
            return new Term(r, Value(s[(at + 1)..]), 0, NotEqual: true);
        }
        if ((at = s.IndexOf(" in ", StringComparison.Ordinal)) > 0)
        {
            int r = Reg(s[..at]);
            var values = s[(at + 4)..].Split(',').Select(Value).ToArray();
            return new Term(r, regs.WidthMask(r), 0, NotEqual: false, In: values);
        }
        if (s[0] == '!')
        {
            int r = Reg(s[1..]);
            return new Term(r, regs.WidthMask(r), 0, NotEqual: false);
        }
        int reg = Reg(s);
        return new Term(reg, regs.WidthMask(reg), 0, NotEqual: true);
    }
}
