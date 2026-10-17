using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HIDMaestro.Internal.Programs;

/// <summary>A defect planted in the generated path on purpose, so
/// program_equivalence_check shows it catches a path that disagrees with the
/// other two. Only the check's own build emits these.</summary>
internal enum PathMutation
{
    None,
    /// <summary>The conditional blocks placed before the unconditional ops.</summary>
    BlocksFirst,
    /// <summary>Template bytes of zero left unwritten, which only a pre-filled
    /// buffer can show.</summary>
    ZeroTemplateSkipped,
    /// <summary>The first masked shift of a bit move left out.</summary>
    MaskedShiftDropped,
    /// <summary>The first value read one bit narrower.</summary>
    ReadOneBitNarrower,
    /// <summary>The kernel marked NoInlining, so the code generic over its
    /// set calls it: the inlining check must see the call.</summary>
    NotInlined,
}

/// <summary>A defect planted in the interpreter on purpose, for the same
/// check.</summary>
internal enum InterpMutation { None, NotEqualIgnored, FieldOffsetPlusOne }

/// <summary>A device's layouts on one clock, in their order: the generated
/// set type runs the kernel of the layout the engine selected.</summary>
internal sealed record ProgramSet(ValidProgram[] Layouts)
{
    /// <summary>The set's key: the layouts' hashes in their order.</summary>
    public string Key => string.Join("|", Layouts.Select(l => l.Hash));
    public string TypeName => "Set" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key)))[..16];
}

/// <summary>The build-time path: straight-line C# from a ValidProgram, in the
/// write order the form defines: the template with the OR ops' bits ORed into
/// it, then the unconditional whole-byte ops in list order, then each
/// conditional block in list order with its ops in list order. It reads only
/// the form and knows no device. Every number is formatted with the invariant
/// culture, and the thread's culture is left alone.</summary>
internal static class Emitter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(FormattableString s) => s.ToString(Inv);

    public static string EmitKernel(ValidProgram v, PathMutation mutation = PathMutation.None, string? name = null)
    {
        name ??= v.KernelName;
        int size = v.Spec.Size;
        bool narrowed = false, dropped = false;
        string ReadM(Src s)
        {
            if (mutation == PathMutation.ReadOneBitNarrower && !narrowed && !s.BigEndianWord && s.Bits > 1)
            {
                narrowed = true;
                return Read(s with { Bits = s.Bits - 1 });
            }
            return Read(s);
        }
        var sb = new StringBuilder();
        sb.AppendLine(F($"    // {v.Spec.Name}{(mutation == PathMutation.None ? "" : ", mutated: " + mutation)}"));
        sb.AppendLine(mutation == PathMutation.NotInlined
            ? "    [MethodImpl(MethodImplOptions.NoInlining)]"
            : "    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine(F($"    public static unsafe void {name}(byte* s, byte* d, uint* r, uint* c)"));
        sb.AppendLine("    {");
        var orTerms = new List<string>?[size];
        var whole = new bool[size];
        var tables = new List<string>();
        var body = new List<string>();
        var tableByContent = new Dictionary<string, string>();
        int tableNo = 0;
        string SharedTable(byte[] content)
        {
            string key = Convert.ToHexString(content);
            if (tableByContent.TryGetValue(key, out var existing)) return existing;
            string t = F($"T{name}_{tableNo++}");
            tables.Add(F($"    static ReadOnlySpan<byte> {t} => new byte[] {{ {string.Join(", ", content.Select(x => F($"0x{x:X2}")))} }};"));
            return tableByContent[key] = t;
        }
        void AddOr(int dst, string term) => (orTerms[dst] ??= new List<string>()).Add(term);

        foreach (var op in v.Unconditional)
        {
            switch (op)
            {
                case BitsOp b:
                {
                    // One masked shift per state byte, report byte and distance.
                    var groups = new SortedDictionary<(int dst, int src, int shift), int>();
                    for (int i = 0; i < b.Moves.Length; i += 2)
                    {
                        int sbit = b.Moves[i], dbit = b.Moves[i + 1];
                        var key = (dbit >> 3, sbit >> 3, (sbit & 7) - (dbit & 7));
                        groups[key] = (groups.TryGetValue(key, out var m) ? m : 0) | (1 << (dbit & 7));
                    }
                    foreach (var ((dst, src, shift), mask) in groups)
                    {
                        if (mutation == PathMutation.MaskedShiftDropped && !dropped && shift != 0) { dropped = true; continue; }
                        AddOr(dst, shift > 0 ? F($"((s[{src}] >> {shift}) & 0x{mask:X2})")
                                 : shift < 0 ? F($"((s[{src}] << {-shift}) & 0x{mask:X2})")
                                 : F($"(s[{src}] & 0x{mask:X2})"));
                    }
                    break;
                }
                case LookupOp l:
                {
                    string t = SharedTable(l.Table);
                    int src = l.SrcBit >> 3, sh = l.SrcBit & 7, mask = (1 << l.Bits) - 1;
                    AddOr(l.Dst, sh > 0 ? F($"{t}[(s[{src}] >> {sh}) & 0x{mask:X}]") : F($"{t}[s[{src}] & 0x{mask:X}]"));
                    break;
                }
                case XorOp x:
                    for (int i = 0; i < x.Len; i++) AddOr(x.Dst + i, F($"(s[{x.Src + i}] ^ 0x{x.Mask:X2})"));
                    break;
                case CopyOp cp:
                    for (int i = 0; i < cp.Len; i++) whole[cp.Dst + i] = true;
                    body.Add(F($"        Unsafe.CopyBlockUnaligned(d + {cp.Dst}, s + {cp.Src}, {cp.Len});"));
                    break;
                case Swap16Op w:
                    for (int k = 0; k < w.Words; k++)
                    {
                        whole[w.Dst + 2 * k] = whole[w.Dst + 2 * k + 1] = true;
                        body.Add(F($"        d[{w.Dst + 2 * k}] = s[{w.Src + 2 * k + 1}]; d[{w.Dst + 2 * k + 1}] = s[{w.Src + 2 * k}];"));
                    }
                    break;
                case FieldOp f:
                    for (int i = 0; i < f.OutBytes; i++) whole[f.Dst + i] = true;
                    body.Add("        {");
                    body.Add(F($"            int v = {ReadM(f.From)} - {f.Center};"));
                    if (f.Negate) body.Add("            v = -v;");
                    if (f.Num != 1 || f.Den != 1)
                    {
                        long range = f.From.BigEndianWord ? 0xFFFF : (1L << f.From.Bits) - 1;
                        long maxAbs = Math.Max(Math.Abs(0L - f.Center), Math.Abs(range - f.Center));
                        body.Add(maxAbs * Math.Abs((long)f.Num) < int.MaxValue
                            ? F($"            v = v * {f.Num} / {f.Den};")
                            : F($"            v = (int)((long)v * {f.Num} / {f.Den});"));
                    }
                    if (f.Offset != 0) body.Add(F($"            v += {f.Offset};"));
                    // The clamp as arithmetic, branch-free once inlined.
                    body.Add(f.Wrap16 ? "            v &= 0xFFFF;"
                        : F($"            {{ int t = v - {f.Min}; v = {f.Min} + (t & ~(t >> 31)); t = {f.Max} - v; v = {f.Max} - (t & ~(t >> 31)); }}"));
                    if (f.OutBytes == 1) body.Add(F($"            d[{f.Dst}] = (byte)v;"));
                    else if (!f.OutBigEndian) body.Add(F($"            d[{f.Dst}] = (byte)v; d[{f.Dst + 1}] = (byte)(v >> 8);"));
                    else body.Add(F($"            d[{f.Dst}] = (byte)(v >> 8); d[{f.Dst + 1}] = (byte)v;"));
                    body.Add("        }");
                    break;
                case Pack12Op p:
                    for (int i = 0; i < 3; i++) whole[p.Dst + i] = true;
                    body.Add("        {");
                    body.Add(F($"            uint x = Unsafe.ReadUnaligned<ushort>(s + {p.SrcX}), y = Unsafe.ReadUnaligned<ushort>(s + {p.SrcY});"));
                    body.Add(F($"            d[{p.Dst}] = (byte)x; d[{p.Dst + 1}] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)); d[{p.Dst + 2}] = (byte)(y >> 4);"));
                    body.Add("        }");
                    break;
                case CounterOp k:
                    for (int i = 0; i < k.Width; i++) whole[k.Dst + i] = true;
                    body.AddRange(Counter(k, "        "));
                    break;
                case Hat8Op h:
                {
                    whole[h.Dst] = true;
                    string t = SharedTable(h.Table9);
                    body.Add("        {");
                    body.Add(F($"            int x = {ReadM(h.X)} - {h.Center}, y = {ReadM(h.Y)} - {h.Center};"));
                    if (!h.YUp) body.Add("            y = -y;");
                    body.Add("            int sector;");
                    // Squared in 64 bits: a 16-bit stick's full diagonal wraps 32.
                    body.Add(F($"            if ((long)x * x + (long)y * y <= {(long)h.Deadzone * h.Deadzone}L) sector = 8;"));
                    body.Add("            else");
                    body.Add("            {");
                    body.Add("                int ax = x < 0 ? -x : x, ay = y < 0 ? -y : y;");
                    body.Add("                bool mostlyY = (long)ax * 1000000 < (long)ay * 2414214;");
                    body.Add("                bool mostlyX = (long)ay * 1000000 < (long)ax * 2414214;");
                    body.Add("                if (mostlyY && (long)ax * 1000000 <= (long)ay * 414214) sector = y > 0 ? 0 : 4;");
                    body.Add("                else if (mostlyX && (long)ay * 1000000 <= (long)ax * 414214) sector = x > 0 ? 2 : 6;");
                    body.Add("                else sector = x > 0 ? (y > 0 ? 1 : 3) : (y > 0 ? 7 : 5);");
                    body.Add("            }");
                    body.Add(F($"            d[{h.Dst}] = {t}[sector];"));
                    body.Add("        }");
                    break;
                }
                case MaxOfOp m:
                {
                    whole[m.Dst] = true;
                    body.Add("        {");
                    body.Add("            uint m = 0, x;");
                    foreach (var term in m.Terms)
                    {
                        string t = SharedTable(term.Table);
                        var parts = new List<string>();
                        int sh = 0;
                        foreach (var part in term.Parts)
                        {
                            parts.Add(sh == 0 ? F($"(uint){ReadM(part)}") : F($"((uint){ReadM(part)} << {sh})"));
                            sh += part.Bits;
                        }
                        // The largest term as arithmetic, branch-free once inlined.
                        body.Add(F($"            x = {t}[(int)({string.Join(" | ", parts)})]; {{ int g = (int)x - (int)m; m += (uint)(g & ~(g >> 31)); }}"));
                    }
                    body.Add(F($"            d[{m.Dst}] = (byte)m;"));
                    body.Add("        }");
                    break;
                }
                default:
                    throw new InvalidOperationException("no emission for " + op.GetType().Name);
            }
        }
        var blockLines = new List<string>();
        foreach (var w in v.Blocks)
        {
            blockLines.Add(F($"        if ({Cond(w.Cond)})"));
            blockLines.Add("        {");
            foreach (var sub in w.Ops)
            {
                switch (sub)
                {
                    case ConstOp k:
                        for (int i = 0; i < k.Bytes.Length; i++) blockLines.Add(F($"            d[{k.At + i}] = 0x{k.Bytes[i]:X2};"));
                        break;
                    case CopyOp cp:
                        blockLines.Add(F($"            Unsafe.CopyBlockUnaligned(d + {cp.Dst}, s + {cp.Src}, {cp.Len});"));
                        break;
                    case CounterOp k:
                        blockLines.AddRange(Counter(k, "            "));
                        break;
                    default: throw new InvalidOperationException("no block emission for " + sub.GetType().Name);
                }
            }
            blockLines.Add("        }");
        }

        // The template in 16-byte stores, then 8, 4, 2 and 1, skipping bytes
        // an unconditional whole-byte write covers. ORed bytes fold their
        // template bits into one store.
        var head = new List<string>();
        var template = v.Template;
        if (mutation == PathMutation.ZeroTemplateSkipped)
        {
            // The planted defect: one store per nonzero template byte, and
            // none for a zero byte, which a zero-filled buffer cannot show.
            for (int i = 0; i < size; i++)
                if (!whole[i] && orTerms[i] == null && template[i] != 0) head.Add(F($"        d[{i}] = 0x{template[i]:X2};"));
        }
        else
        {
            int pos = 0;
            bool Skip(int at, int len) { for (int i = at; i < at + len; i++) if (!whole[i] && orTerms[i] == null) return false; return true; }
            while (pos < size)
            {
                int left = size - pos;
                if (left >= 16)
                {
                    if (!Skip(pos, 16))
                    {
                        ulong lo = 0, hi = 0;
                        for (int i = 0; i < 8; i++) { lo |= (ulong)template[pos + i] << (8 * i); hi |= (ulong)template[pos + 8 + i] << (8 * i); }
                        head.Add(F($"        Unsafe.WriteUnaligned(d + {pos}, Vector128.Create(0x{lo:X}UL, 0x{hi:X}UL));"));
                    }
                    pos += 16;
                    continue;
                }
                int w2 = left >= 8 ? 8 : left >= 4 ? 4 : left >= 2 ? 2 : 1;
                if (!Skip(pos, w2))
                {
                    ulong val = 0;
                    for (int i = 0; i < w2; i++) val |= (ulong)template[pos + i] << (8 * i);
                    string type = w2 == 8 ? "ulong" : w2 == 4 ? "uint" : w2 == 2 ? "ushort" : "byte";
                    head.Add(F($"        *({type}*)(d + {pos}) = ({type})0x{val:X}UL;"));
                }
                pos += w2;
            }
        }
        for (int i = 0; i < size; i++)
            if (orTerms[i] is { } terms)
                head.Add(F($"        d[{i}] = (byte)(0x{template[i]:X2} | {string.Join(" | ", terms)});"));

        sb.Append(string.Join("\n", head)).Append('\n');
        if (mutation == PathMutation.BlocksFirst)
        {
            sb.Append(string.Join("\n", blockLines)).Append('\n');
            sb.Append(string.Join("\n", body)).Append('\n');
        }
        else
        {
            sb.Append(string.Join("\n", body)).Append('\n');
            sb.Append(string.Join("\n", blockLines)).Append('\n');
        }
        sb.AppendLine("    }");
        foreach (var t in tables) sb.AppendLine(t);
        return sb.ToString();
    }

    static string Read(Src s)
    {
        if (s.BigEndianWord) return F($"((s[{s.BitOffset >> 3}] << 8) | s[{(s.BitOffset >> 3) + 1}])");
        uint mask = s.Bits >= 32 ? uint.MaxValue : (1u << s.Bits) - 1;
        int sh = s.BitOffset & 7;
        return sh == 0 ? F($"(int)(Unsafe.ReadUnaligned<uint>(s + {s.BitOffset >> 3}) & 0x{mask:X})")
                       : F($"(int)((Unsafe.ReadUnaligned<uint>(s + {s.BitOffset >> 3}) >> {sh}) & 0x{mask:X})");
    }

    static IEnumerable<string> Counter(CounterOp k, string ind)
    {
        yield return F($"{ind}{{ uint v = unchecked(c[{k.Slot}] * {k.Scale}u + {k.Add}u);");
        for (int i = 0; i < k.Width; i++)
        {
            int shift = 8 * (k.BigEndian ? k.Width - 1 - i : i);
            yield return F($"{ind}  d[{k.Dst + i}] = (byte)(v >> {shift});");
        }
        yield return F($"{ind}}}");
    }

    static string TermExpr(Term t)
    {
        string reg = F($"(r[{t.Register}] & 0x{t.Mask:X}u)");
        string test = t.In == null ? F($"{reg} == 0x{t.Value:X}u")
                                   : "(" + string.Join(" || ", t.In.Select(x => F($"{reg} == 0x{x:X}u"))) + ")";
        return t.NotEqual ? F($"!({test})") : F($"({test})");
    }

    static string Cond(Condition c) => string.Join(" || ", c.Groups.Select(g => "(" + string.Join(" && ", g.Select(TermExpr)) + ")"));

    /// <summary>The generated file: every distinct kernel as a method and as a
    /// value type for the code generic over it, every set as a value type, the
    /// table the binder looks a program's hash up in, and the switch that
    /// builds the generic code for a set's key. Mutants, when given, go in a
    /// table of their own, keyed by hash and mutation, never in the binder's.</summary>
    public static string EmitFile(IEnumerable<ValidProgram> programs, IEnumerable<ProgramSet> sets, string nameSpace, string cls,
        IEnumerable<(ValidProgram program, PathMutation mutation)>? mutants = null)
    {
        var list = programs.GroupBy(p => p.Hash).Select(g => g.First()).OrderBy(p => p.Hash, StringComparer.Ordinal).ToList();
        var setList = sets.GroupBy(s => s.Key).Select(g => g.First()).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        foreach (var set in setList)
            foreach (var layout in set.Layouts)
                if (!list.Any(p => p.Hash == layout.Hash)) list.Add(layout);
        list = list.OrderBy(p => p.Hash, StringComparer.Ordinal).ToList();
        var mutantList = (mutants ?? Enumerable.Empty<(ValidProgram, PathMutation)>()).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated at build time by HIDMaestro.KernelGen from the validated form of the");
        sb.AppendLine("// profiles' programs. Do not edit.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.Intrinsics;");
        sb.AppendLine(F($"namespace {nameSpace};"));
        sb.AppendLine(F($"internal static unsafe partial class {cls}"));
        sb.AppendLine("{");
        foreach (var v in list) sb.Append(EmitKernel(v));
        foreach (var (v, m) in mutantList) sb.Append(EmitKernel(v, m, F($"{v.KernelName}_{m}")));
        sb.AppendLine("    /// <summary>Each program's kernel by the SHA-256 of its canonical form.</summary>");
        sb.AppendLine("    public static readonly Dictionary<string, nint> ByHash = new()");
        sb.AppendLine("    {");
        foreach (var v in list) sb.AppendLine(F($"        [\"{v.Hash}\"] = (nint)(delegate*<byte*, byte*, uint*, uint*, void>)&{v.KernelName},"));
        sb.AppendLine("    };");
        sb.AppendLine("    public static readonly Dictionary<string, nint> Mutants = new()");
        sb.AppendLine("    {");
        foreach (var (v, m) in mutantList) sb.AppendLine(F($"        [\"{v.Hash}|{m}\"] = (nint)(delegate*<byte*, byte*, uint*, uint*, void>)&{v.KernelName}_{m},"));
        sb.AppendLine("    };");
        sb.AppendLine("    /// <summary>The generic code for a set, built over the set's generated type.</summary>");
        sb.AppendLine("    public static bool TryBindSet(string key, IReportSetBinder binder, out object? bound)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (key)");
        sb.AppendLine("        {");
        foreach (var set in setList)
            sb.AppendLine(F($"            case \"{set.Key}\": bound = binder.Bind<{set.TypeName}>(); return true;"));
        foreach (var (v, m) in mutantList)
            sb.AppendLine(F($"            case \"{v.Hash}|{m}\": bound = binder.Bind<Set{v.KernelName}_{m}>(); return true;"));
        sb.AppendLine("            default: bound = null; return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine(F($"    public const int ProgramCount = {list.Count};"));
        sb.AppendLine(F($"    public const int SetCount = {setList.Count};"));
        sb.AppendLine("}");
        foreach (var v in list)
        {
            sb.AppendLine(F($"internal readonly struct S{v.KernelName} : IReportKernel"));
            sb.AppendLine("{");
            sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(F($"    public unsafe void Run(byte* s, byte* d, uint* r, uint* c) => {cls}.{v.KernelName}(s, d, r, c);"));
            sb.AppendLine("}");
        }
        // Each mutant as a set of one layout, so code generic over a set can
        // run it: the inlining check binds the NotInlined one.
        foreach (var (v, m) in mutantList)
        {
            sb.AppendLine(F($"internal readonly struct Set{v.KernelName}_{m} : IReportSet"));
            sb.AppendLine("{");
            sb.AppendLine("    public int Count => 1;");
            sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(F($"    public unsafe void Run(int index, byte* s, byte* d, uint* r, uint* c) => {cls}.{v.KernelName}_{m}(s, d, r, c);"));
            sb.AppendLine("}");
        }
        foreach (var set in setList)
        {
            sb.AppendLine(F($"internal readonly struct {set.TypeName} : IReportSet"));
            sb.AppendLine("{");
            sb.AppendLine(F($"    public int Count => {set.Layouts.Length};"));
            sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine("    public unsafe void Run(int index, byte* s, byte* d, uint* r, uint* c)");
            sb.AppendLine("    {");
            sb.AppendLine("        switch (index)");
            sb.AppendLine("        {");
            for (int i = 0; i < set.Layouts.Length; i++)
                sb.AppendLine(i == set.Layouts.Length - 1
                    ? F($"            default: {cls}.{set.Layouts[i].KernelName}(s, d, r, c); break;")
                    : F($"            case {i}: {cls}.{set.Layouts[i].KernelName}(s, d, r, c); break;"));
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }
}
