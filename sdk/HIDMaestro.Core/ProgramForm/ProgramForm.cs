using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HIDMaestro.Internal.Programs;

/// <summary>A program validation refused, with the JSON path of the value at
/// fault. The build tool prints it as an MSBuild error and the run-time
/// loader as a load issue, from the same path and reason.</summary>
internal sealed class FormException : Exception
{
    public string Path { get; }
    public string Reason { get; }
    public FormException(string path, string reason, Exception? inner = null) : base($"{path}: {reason}", inner) { Path = path; Reason = reason; }

    /// <summary>The refusal as the SDK words a profile defect.</summary>
    public string ForProfile(string profileId) => ProfileText(profileId, $"{Path}: {Reason}");

    /// <summary>"Profile 'id': " and <paramref name="message"/>, its leading
    /// JSON path written from the root without "$.", the form a profile
    /// check's message has always carried and a load issue reads its path
    /// from. A message about the whole file ("$: ...") names no path.</summary>
    public static string ProfileText(string profileId, string message) =>
        $"Profile '{profileId}': " + (message.StartsWith("$.", StringComparison.Ordinal) ? message[2..]
            : message.StartsWith("$: ", StringComparison.Ordinal) ? message[3..] : message);
}

/// <summary>The one validated form the generated and interpreted paths start
/// from. Only <see cref="ProgramForm.Validate"/> makes one.</summary>
internal sealed class ValidProgram
{
    public required ProgramSpec Spec { get; init; }
    /// <summary>The template: top-level const bytes, zero elsewhere.</summary>
    public required byte[] Template { get; init; }
    /// <summary>Top-level ops other than consts and blocks, in list order.
    /// No two write one report bit.</summary>
    public required Op[] Unconditional { get; init; }
    /// <summary>Conditional blocks in list order. They run after every
    /// unconditional op, and inside a block ops run in list order, a later
    /// write replacing an earlier one.</summary>
    public required WhenOp[] Blocks { get; init; }
    public required string Canonical { get; init; }
    /// <summary>SHA-256 of <see cref="Canonical"/>, uppercase hex.</summary>
    public required string Hash { get; init; }
    public string KernelName => "K" + Hash[..16];
}

/// <summary>The single gate of mechanism 2: the write order, the domain of
/// every operand, and the overlap rules both paths rely on. The build tool and
/// the SDK compile this file, the same source, and accept only what
/// <see cref="Validate"/> returns.</summary>
internal static class ProgramForm
{
    /// <summary>A value read is one unaligned 32-bit load in both paths, so the
    /// state block carries this many bytes of slack past its end.</summary>
    public const int Slack = 3;
    public const int MaxReport = 64;
    public const int MaxState = 256;
    public const int MaxDecodeBits = 12;
    public const int MaxCounters = 8;
    public const int MaxRegisters = 32;
    public const int MaxGroups = 8;

    public static ValidProgram Validate(ProgramSpec p)
    {
        if (p.Size < 1 || p.Size > MaxReport) throw new FormException("$.size", $"{p.Size} is outside 1 to {MaxReport}");
        if (p.StateSize < 1 || p.StateSize > MaxState) throw new FormException("$.stateSize", $"{p.StateSize} is outside 1 to {MaxState}");
        if (p.RegisterBits.Length > MaxRegisters) throw new FormException("$.registers", $"{p.RegisterBits.Length} registers is past the {MaxRegisters} a program reads");
        for (int i = 0; i < p.RegisterBits.Length; i++)
            if (p.RegisterBits[i] < 1 || p.RegisterBits[i] > 32) throw new FormException($"$.registers[{i}]", "a register is 1 to 32 bits");

        var template = new byte[p.Size];
        var templateSet = new bool[p.Size];
        var whole = new bool[p.Size];
        var orBits = new byte[p.Size];
        // Each OR op's bits, checked against the template once every const is
        // known: an OR op cannot clear a template bit of 1.
        var orWrites = new List<(string path, int at, byte bits)>();
        var unconditional = new List<Op>();
        var blocks = new List<WhenOp>();

        // Every bound is checked in long: an offset and a length near
        // int.MaxValue would wrap an int sum negative and pass.
        void Range(string path, long at, long len, int limit, string what)
        {
            if (len < 1 || at < 0 || at + len > limit)
                throw new FormException(path, len == 1 ? $"{what} {at} is outside 0 to {limit - 1}" : $"{what} {at} to {at + len - 1} are outside 0 to {limit - 1}");
        }
        void TakeWhole(string path, int at, int len)
        {
            Range(path, at, len, p.Size, "report bytes");
            for (int i = at; i < at + len; i++)
            {
                if (whole[i] || orBits[i] != 0) throw new FormException(path, $"report byte {i} is written by another op");
                whole[i] = true;
            }
        }
        void TakeBits(string path, int at, byte bits)
        {
            Range(path, at, 1, p.Size, "report byte");
            if (whole[at] || (orBits[at] & bits) != 0) throw new FormException(path, $"report byte {at} is written by another op");
            orBits[at] |= bits;
            orWrites.Add((path, at, bits));
        }
        void Read(string path, Src s)
        {
            if (s.BigEndianWord)
            {
                if (s.Bits != 16) throw new FormException(path + ".bits", "a big-endian word is 16 bits");
                Range(path, s.BitOffset >> 3, 2, p.StateSize, "state bytes");
                if ((s.BitOffset & 7) != 0) throw new FormException(path + ".bit", "a big-endian word starts on a byte");
                return;
            }
            if (s.Bits < 1 || s.Bits > 25) throw new FormException(path + ".bits", $"{s.Bits} bits is outside 1 to 25, the most one 32-bit load holds at any shift");
            if (s.BitOffset < 0 || (long)s.BitOffset + s.Bits > p.StateSize * 8) throw new FormException(path + ".bit", $"bits {s.BitOffset} to {(long)s.BitOffset + s.Bits - 1} are outside the {p.StateSize}-byte state block");
            // 25 bits at a shift of at most 7 fit one 32-bit load.
        }
        void Counter(string path, CounterOp c, bool inBlock)
        {
            if (c.Slot < 0 || c.Slot >= MaxCounters) throw new FormException(path + ".slot", $"a counter slot is 0 to {MaxCounters - 1}");
            if (c.Width < 1 || c.Width > 4) throw new FormException(path + ".width", "a counter is 1 to 4 bytes");
            if (inBlock) Range(path, c.Dst, c.Width, p.Size, "report bytes");
            else TakeWhole(path, c.Dst, c.Width);
        }

        for (int i = 0; i < p.Ops.Length; i++)
        {
            string path = $"$.ops[{i}]";
            switch (p.Ops[i])
            {
                case ConstOp c:
                    Range(path, c.At, c.Bytes.Length, p.Size, "report bytes");
                    for (int k = 0; k < c.Bytes.Length; k++)
                    {
                        if (templateSet[c.At + k]) throw new FormException(path, $"template byte {c.At + k} is set twice");
                        templateSet[c.At + k] = true;
                        template[c.At + k] = c.Bytes[k];
                    }
                    break;
                case WhenOp w:
                    CheckCondition(path + ".when", w.Cond, p.RegisterBits);
                    if (w.Ops.Length == 0) throw new FormException(path + ".ops", "a block needs an op");
                    for (int j = 0; j < w.Ops.Length; j++)
                    {
                        string bp = $"{path}.ops[{j}]";
                        switch (w.Ops[j])
                        {
                            case ConstOp c: Range(bp, c.At, c.Bytes.Length, p.Size, "report bytes"); break;
                            case CopyOp c: Range(bp, c.Dst, c.Len, p.Size, "report bytes"); Range(bp + ".src", c.Src, c.Len, p.StateSize, "state bytes"); break;
                            case CounterOp c: Counter(bp, c, inBlock: true); break;
                            default: throw new FormException(bp, "a conditional block holds constant bytes, copies and counters");
                        }
                    }
                    blocks.Add(w);
                    break;
                case CopyOp c:
                    Range(path + ".src", c.Src, c.Len, p.StateSize, "state bytes");
                    TakeWhole(path, c.Dst, c.Len);
                    unconditional.Add(c);
                    break;
                case BitsOp b:
                    if (b.Moves.Length == 0 || (b.Moves.Length & 1) != 0) throw new FormException(path + ".moves", "moves come in pairs");
                    for (int k = 0; k < b.Moves.Length; k += 2)
                    {
                        if (b.Moves[k] < 0 || b.Moves[k] >= p.StateSize * 8) throw new FormException($"{path}.moves[{k}]", "the state bit is outside the state block");
                        if (b.Moves[k + 1] < 0 || b.Moves[k + 1] >= p.Size * 8) throw new FormException($"{path}.moves[{k + 1}]", "the report bit is outside the report");
                        TakeBits($"{path}.moves[{k + 1}]", b.Moves[k + 1] >> 3, (byte)(1 << (b.Moves[k + 1] & 7)));
                    }
                    unconditional.Add(b);
                    break;
                case LookupOp l:
                    if (l.Bits < 1 || l.Bits > 8) throw new FormException(path + ".bits", "a lookup reads 1 to 8 bits");
                    if (l.SrcBit < 0 || (long)l.SrcBit + l.Bits > p.StateSize * 8) throw new FormException(path + ".bit", "the bits are outside the state block");
                    if ((l.SrcBit & 7) + l.Bits > 8) throw new FormException(path + ".bits", $"bits {l.SrcBit} to {l.SrcBit + l.Bits - 1} span two state bytes, and a lookup reads one");
                    if (l.Table.Length != 1 << l.Bits) throw new FormException(path + ".table", $"the table has {l.Table.Length} entries for {l.Bits} bits");
                    {
                        // The bits a lookup writes: every bit some table entry sets.
                        byte written = 0;
                        foreach (byte e in l.Table) written |= e;
                        TakeBits(path, l.Dst, written);
                    }
                    unconditional.Add(l);
                    break;
                case XorOp x:
                    Range(path + ".src", x.Src, x.Len, p.StateSize, "state bytes");
                    Range(path, x.Dst, x.Len, p.Size, "report bytes");
                    for (int k = 0; k < x.Len; k++) TakeBits(path, x.Dst + k, 0xFF);
                    unconditional.Add(x);
                    break;
                case Swap16Op s:
                    if (s.Words < 1) throw new FormException(path + ".words", "a swap16 moves at least one word");
                    Range(path + ".src", s.Src, s.Words * 2L, p.StateSize, "state bytes");
                    TakeWhole(path, s.Dst, s.Words * 2);
                    unconditional.Add(s);
                    break;
                case FieldOp f:
                    Read(path + ".from", f.From);
                    {
                        long range = f.From.BigEndianWord ? 0xFFFF : (1L << f.From.Bits) - 1;
                        // The center lies in the raw range of its read, so the
                        // difference fits the fast paths' 32-bit subtraction.
                        if (f.Center < 0 || f.Center > range) throw new FormException(path + ".center", $"the center {f.Center} is outside the read's raw range, 0 to {range}");
                        long maxAbs = Math.Max(Math.Abs(0L - f.Center), Math.Abs(range - f.Center));
                        if (f.Den == 0) throw new FormException(path + ".den", "the divisor is zero");
                        long scaled = maxAbs * Math.Abs((long)f.Num) / Math.Abs((long)f.Den) + Math.Abs((long)f.Offset);
                        if (scaled >= 1L << 30) throw new FormException(path + ".num", $"the scaled range reaches {scaled}, past 2^30");
                        if (Math.Abs((long)f.Min) >= 1L << 30 || Math.Abs((long)f.Max) >= 1L << 30 || f.Min > f.Max) throw new FormException(path + ".min", "the bounds must be ordered and inside 2^30");
                        if (f.OutBytes < 1 || f.OutBytes > 2) throw new FormException(path + ".bytes", "a field writes 1 or 2 bytes");
                    }
                    TakeWhole(path, f.Dst, f.OutBytes);
                    unconditional.Add(f);
                    break;
                case Pack12Op k:
                    Range(path + ".x", k.SrcX, 2, p.StateSize, "state bytes");
                    Range(path + ".y", k.SrcY, 2, p.StateSize, "state bytes");
                    TakeWhole(path, k.Dst, 3);
                    unconditional.Add(k);
                    break;
                case CounterOp c:
                    Counter(path, c, inBlock: false);
                    unconditional.Add(c);
                    break;
                case Hat8Op h:
                    Read(path + ".x", h.X);
                    Read(path + ".y", h.Y);
                    {
                        long rx = h.X.BigEndianWord ? 0xFFFF : (1L << h.X.Bits) - 1;
                        long ry = h.Y.BigEndianWord ? 0xFFFF : (1L << h.Y.Bits) - 1;
                        long range = Math.Min(rx, ry);
                        if (h.Center < 0 || h.Center > range) throw new FormException(path + ".center", $"the center {h.Center} is outside the reads' raw range, 0 to {range}");
                    }
                    if (h.Deadzone < 0 || h.Deadzone > 65535) throw new FormException(path + ".deadzone", "the dead zone is 0 to 65535 counts");
                    if (h.Table9.Length != 9) throw new FormException(path + ".table", "a hat table has 9 entries");
                    TakeWhole(path, h.Dst, 1);
                    unconditional.Add(h);
                    break;
                case MaxOfOp m:
                    if (m.Terms.Length == 0) throw new FormException(path + ".terms", "a decoded value needs a term");
                    for (int t = 0; t < m.Terms.Length; t++)
                    {
                        string tp = $"{path}.terms[{t}]";
                        int bits = 0;
                        if (m.Terms[t].Parts.Length == 0) throw new FormException(tp + ".parts", "a term needs a part");
                        for (int q = 0; q < m.Terms[t].Parts.Length; q++)
                        {
                            Read($"{tp}.parts[{q}]", m.Terms[t].Parts[q]);
                            if (m.Terms[t].Parts[q].BigEndianWord) throw new FormException($"{tp}.parts[{q}]", "a part is a bit read");
                            bits += m.Terms[t].Parts[q].Bits;
                        }
                        if (bits > MaxDecodeBits) throw new FormException(tp + ".parts", $"{bits} bits is past the {MaxDecodeBits} a term indexes");
                        if (m.Terms[t].Table.Length != 1 << bits) throw new FormException(tp + ".table", $"the table has {m.Terms[t].Table.Length} entries for {bits} bits");
                    }
                    TakeWhole(path, m.Dst, 1);
                    unconditional.Add(m);
                    break;
                default:
                    throw new FormException(path, $"no op {p.Ops[i].GetType().Name}");
            }
        }

        foreach (var (path, at, bits) in orWrites)
            if ((template[at] & bits) != 0)
                throw new FormException(path, $"template byte {at} is 0x{template[at]:X2}, and this op ORs bits 0x{bits:X2} into it, which it could never clear");

        string canonical = Canonicalize(p);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ValidProgram
        {
            Spec = p, Template = template, Unconditional = unconditional.ToArray(), Blocks = blocks.ToArray(),
            Canonical = canonical, Hash = hash,
        };
    }

    /// <summary>The domain of a condition: at most <see cref="MaxGroups"/>
    /// groups, each term's register declared, its mask nonzero and inside the
    /// register's width, and its value and each in value inside the mask.</summary>
    public static void CheckCondition(string path, Condition c, int[] registerBits)
    {
        if (c.Groups.Length == 0) throw new FormException(path, "a condition needs a group");
        if (c.Groups.Length > MaxGroups) throw new FormException(path, $"{c.Groups.Length} groups is past the {MaxGroups} a condition compiles to");
        for (int g = 0; g < c.Groups.Length; g++)
        {
            if (c.Groups[g].Length == 0) throw new FormException($"{path}[{g}]", "a group needs a term");
            for (int t = 0; t < c.Groups[g].Length; t++)
            {
                var term = c.Groups[g][t];
                string tp = $"{path}[{g}][{t}]";
                if (term.Register < 0 || term.Register >= registerBits.Length) throw new FormException(tp + ".register", "no such register");
                int w = registerBits[term.Register];
                uint widthMask = w == 32 ? uint.MaxValue : (1u << w) - 1;
                if (term.Mask == 0 || (term.Mask & ~widthMask) != 0) throw new FormException(tp + ".mask", $"the mask must be nonzero and inside the register's {w} bits");
                if (term.In == null)
                {
                    if ((term.Value & ~term.Mask) != 0) throw new FormException(tp + ".value", "the value has bits outside the mask");
                }
                else
                {
                    if (term.In.Length == 0) throw new FormException(tp + ".in", "an in term needs a value");
                    for (int k = 0; k < term.In.Length; k++)
                        if ((term.In[k] & ~term.Mask) != 0) throw new FormException($"{tp}.in[{k}]", "the value has bits outside the mask");
                }
            }
        }
    }

    /// <summary>The canonical text of a program: every value in a fixed order
    /// and spelling, so two programs that mean the same thing hash the same at
    /// build time and at run time. The name is left out, so two profiles that
    /// state one program share its kernel. Every number is formatted with the
    /// invariant culture, whatever the culture of the build host or of the
    /// thread that creates the controller, which this leaves untouched.</summary>
    public static string Canonicalize(ProgramSpec p)
    {
        var sb = new StringBuilder();
        sb.Append(I, $"size={p.Size};state={p.StateSize};regs={Ints(p.RegisterBits)};");
        foreach (var op in p.Ops) { Op(sb, op); sb.Append(';'); }
        return sb.ToString();
    }

    static readonly CultureInfo I = CultureInfo.InvariantCulture;
    static string Ints(IEnumerable<int> v) => string.Join(",", v.Select(x => x.ToString(I)));
    static string Hex(byte[] b) => Convert.ToHexString(b);
    static string S(Src s) => string.Create(I, $"{s.BitOffset}:{s.Bits}:{(s.BigEndianWord ? 1 : 0)}");

    static void Op(StringBuilder sb, Op op)
    {
        switch (op)
        {
            case CopyOp c: sb.Append(I, $"copy({c.Src},{c.Dst},{c.Len})"); break;
            case ConstOp c: sb.Append(I, $"const({c.At},{Hex(c.Bytes)})"); break;
            case BitsOp b: sb.Append(I, $"bits({Ints(b.Moves)})"); break;
            case XorOp x: sb.Append(I, $"xor({x.Src},{x.Dst},{x.Len},{x.Mask})"); break;
            case Swap16Op s: sb.Append(I, $"swap16({s.Src},{s.Dst},{s.Words})"); break;
            case LookupOp l: sb.Append(I, $"lookup({l.SrcBit},{l.Bits},{l.Dst},{Hex(l.Table)})"); break;
            case FieldOp f: sb.Append(I, $"field({S(f.From)},{f.Center},{(f.Negate ? 1 : 0)},{f.Num},{f.Den},{f.Offset},{f.Min},{f.Max},{(f.Wrap16 ? 1 : 0)},{f.Dst},{f.OutBytes},{(f.OutBigEndian ? 1 : 0)})"); break;
            case Pack12Op k: sb.Append(I, $"pack12({k.SrcX},{k.SrcY},{k.Dst})"); break;
            case CounterOp c: sb.Append(I, $"counter({c.Slot},{c.Dst},{c.Width},{c.Scale},{c.Add},{(c.BigEndian ? 1 : 0)})"); break;
            case Hat8Op h: sb.Append(I, $"hat8({S(h.X)},{S(h.Y)},{h.Center},{h.Deadzone},{(h.YUp ? 1 : 0)},{Hex(h.Table9)},{h.Dst})"); break;
            case MaxOfOp m:
                sb.Append(I, $"maxof({m.Dst}");
                foreach (var t in m.Terms) sb.Append(I, $",[{string.Join("+", t.Parts.Select(S))}:{Hex(t.Table)}]");
                sb.Append(')');
                break;
            case WhenOp w:
                sb.Append("when(");
                sb.Append(CanonicalCondition(w.Cond));
                sb.Append("){");
                foreach (var s in w.Ops) { Op(sb, s); sb.Append(';'); }
                sb.Append('}');
                break;
            default: throw new InvalidOperationException(op.GetType().Name);
        }
    }

    /// <summary>A condition's canonical text, groups in order, terms in order.</summary>
    public static string CanonicalCondition(Condition c) =>
        string.Join("|", c.Groups.Select(g => string.Join("&", g.Select(t =>
            string.Create(I, $"r{t.Register}:{t.Mask:X}:{(t.NotEqual ? "ne" : "eq")}:")
            + (t.In == null ? t.Value.ToString("X", I) : "in" + string.Join(".", t.In.Select(v => v.ToString("X", I))))))));
}
