using System;
using HIDMaestro.Internal.Programs;


/// <summary>The third evaluator, written from the design note's text alone
/// (mechanism 2: the op table, "One semantics" and its write order, the
/// condition grammar and the hat8 sector rule), for a program built in code,
/// which has no authored JSON: the corpus, the review cases and the planted
/// defects. A profile's programs go to <see cref="NaiveJson"/>, which reads
/// the JSON. This takes the program's ops in list order, not the validated
/// form's stage lists, and shares no stage order, table, constant or
/// arithmetic shortcut with the generated and interpreted paths. Values are read one state bit at a time, the
/// arithmetic is exact in 64 bits, a hat's sector is found from the note's
/// stated boundary, and an OR op writes its bits over the byte rather than
/// ORing into it, which the note says gives the same byte because validation
/// keeps a template bit of 1 out from under an OR op's bits. A defect both
/// fast paths share, or a rule the note states and the paths do not
/// follow, shows as a difference from this.</summary>
internal static class Naive
{
    /// <summary>State bit k: bit k mod 8 of byte k / 8.</summary>
    static int StateBit(byte[] s, int k) => (s[k / 8] >> (k % 8)) & 1;

    static void SetReportBit(byte[] d, int k, int value)
    {
        int mask = 1 << (k % 8);
        d[k / 8] = (byte)(value != 0 ? d[k / 8] | mask : d[k / 8] & ~mask);
    }

    /// <summary>"A value is read as N bits at a bit offset or as a 16-bit
    /// word in either byte order": bit i of the value is state bit
    /// offset + i, or the word's first byte is its high byte.</summary>
    static long ReadValue(byte[] s, Src src)
    {
        if (src.BigEndianWord) return 256L * s[src.BitOffset / 8] + s[src.BitOffset / 8 + 1];
        long v = 0;
        for (int i = 0; i < src.Bits; i++) v += (long)StateBit(s, src.BitOffset + i) << i;
        return v;
    }

    static void WriteByte(byte[] d, int at, long value) => d[at] = (byte)(value & 0xFF);

    /// <summary>"Terms join with &amp;&amp; into a group, and groups join
    /// with ||": a term holds when (register AND mask) equals its value or,
    /// for an in term, one of its values, and != negates it.</summary>
    static bool Holds(Condition c, uint[] regs)
    {
        bool any = false;
        foreach (var group in c.Groups)
        {
            bool all = true;
            foreach (var t in group)
            {
                long masked = regs[t.Register] & t.Mask;
                bool hit = false;
                if (t.In == null) hit = masked == t.Value;
                else foreach (var x in t.In) if (masked == x) hit = true;
                if (t.NotEqual) hit = !hit;
                all = all && hit;
            }
            any = any || all;
        }
        return any;
    }

    /// <summary>"A counter slot written at an offset in a width and byte
    /// order, with a scale and an add": the value times the scale plus the
    /// add, kept to 32 bits, its low Width bytes written low byte first, or
    /// high byte first in big-endian order.</summary>
    static void Counter(byte[] d, CounterOp k, uint[] ctr)
    {
        ulong v = ((ulong)ctr[k.Slot] * k.Scale + k.Add) % 4294967296UL;
        for (int i = 0; i < k.Width; i++)
        {
            long b = (long)(v >> (8 * i));
            WriteByte(d, k.BigEndian ? k.Dst + k.Width - 1 - i : k.Dst + i, b);
        }
    }

    /// <summary>The hat8 sector rule as the note states it: inside the dead
    /// zone (x squared plus y squared at most the dead zone squared) the
    /// ninth entry. Otherwise, with a the smaller and b the larger of |x| and
    /// |y|, the point is on the larger one's axis when 1,000,000 a is at most
    /// 414,214 b (up 0 or down 4 for y, right 2 or left 6 for x), and on the
    /// diagonal of its quadrant otherwise (1 up-right, 3 down-right, 5
    /// down-left, 7 up-left).</summary>
    internal static int HatSector(long x, long y, long deadzone)
    {
        if (x * x + y * y <= deadzone * deadzone) return 8;
        long ax = Math.Abs(x), ay = Math.Abs(y);
        long a = Math.Min(ax, ay), b = Math.Max(ax, ay);
        if (1_000_000 * a <= 414_214 * b)
        {
            if (ay > ax) return y > 0 ? 0 : 4;
            return x > 0 ? 2 : 6;
        }
        if (x > 0) return y > 0 ? 1 : 3;
        return y > 0 ? 7 : 5;
    }

    public static void Run(ProgramSpec p, byte[] s, byte[] d, uint[] regs, uint[] ctr)
    {
        // 1. The template: every top-level const byte. A byte no const sets is zero.
        for (int i = 0; i < p.Size; i++) d[i] = 0;
        foreach (var op in p.Ops)
            if (op is ConstOp c)
                for (int i = 0; i < c.Bytes.Length; i++) d[c.At + i] = c.Bytes[i];

        // 2. The unconditional ops, in list order.
        foreach (var op in p.Ops)
        {
            switch (op)
            {
                case ConstOp:
                case WhenOp:
                    break;
                case CopyOp c:
                    for (int i = 0; i < c.Len; i++) d[c.Dst + i] = s[c.Src + i];
                    break;
                case BitsOp b:
                    // Each move writes its report bit from its state bit.
                    for (int i = 0; i + 1 < b.Moves.Length; i += 2)
                        SetReportBit(d, b.Moves[i + 1], StateBit(s, b.Moves[i]));
                    break;
                case LookupOp l:
                {
                    // The group's value indexes the table, and the entry is
                    // written over the bits some entry of the table sets.
                    long index = ReadValue(s, new Src(l.SrcBit, l.Bits));
                    int entry = l.Table[index];
                    int written = 0;
                    foreach (byte e in l.Table) written |= e;
                    for (int bit = 0; bit < 8; bit++)
                        if (((written >> bit) & 1) != 0) SetReportBit(d, l.Dst * 8 + bit, (entry >> bit) & 1);
                    break;
                }
                case XorOp x:
                    for (int i = 0; i < x.Len; i++)
                    {
                        int v = s[x.Src + i] ^ x.Mask;
                        for (int bit = 0; bit < 8; bit++) SetReportBit(d, (x.Dst + i) * 8 + bit, (v >> bit) & 1);
                    }
                    break;
                case Swap16Op w:
                    // Each 16-bit word with its two bytes exchanged.
                    for (int k = 0; k < w.Words; k++)
                    {
                        byte first = s[w.Src + 2 * k], second = s[w.Src + 2 * k + 1];
                        d[w.Dst + 2 * k] = second;
                        d[w.Dst + 2 * k + 1] = first;
                    }
                    break;
                case FieldOp f:
                {
                    // read, minus the center, negated if asked, times the ratio
                    // with C truncation (toward zero), plus the offset, then
                    // clamped, or wrapped as an unsigned 16-bit value.
                    long v = ReadValue(s, f.From) - f.Center;
                    if (f.Negate) v = -v;
                    long product = v * f.Num;
                    long quotient = Math.Abs(product) / Math.Abs((long)f.Den);
                    if ((product < 0) != (f.Den < 0) && product != 0) quotient = -quotient;
                    v = quotient + f.Offset;
                    long w = f.Wrap16 ? ((v % 65536) + 65536) % 65536 : Math.Min(Math.Max(v, f.Min), f.Max);
                    long u16 = ((w % 65536) + 65536) % 65536;
                    if (f.OutBytes == 1) WriteByte(d, f.Dst, u16);
                    else if (!f.OutBigEndian) { WriteByte(d, f.Dst, u16); WriteByte(d, f.Dst + 1, u16 / 256); }
                    else { WriteByte(d, f.Dst, u16 / 256); WriteByte(d, f.Dst + 1, u16); }
                    break;
                }
                case Pack12Op k:
                {
                    // The low 12 bits of each little-endian word, x lowest, as
                    // a 24-bit little-endian value.
                    long x = s[k.SrcX] + 256L * s[k.SrcX + 1], y = s[k.SrcY] + 256L * s[k.SrcY + 1];
                    long v24 = (x % 4096) + 4096 * (y % 4096);
                    for (int i = 0; i < 3; i++) WriteByte(d, k.Dst + i, v24 >> (8 * i));
                    break;
                }
                case CounterOp k:
                    Counter(d, k, ctr);
                    break;
                case Hat8Op h:
                {
                    long x = ReadValue(s, h.X) - h.Center, y = ReadValue(s, h.Y) - h.Center;
                    if (!h.YUp) y = -y;
                    d[h.Dst] = h.Table9[HatSector(x, y, h.Deadzone)];
                    break;
                }
                case MaxOfOp m:
                {
                    // Each term's parts concatenated, the first part lowest,
                    // index its table, and the largest entry is written.
                    int best = 0;
                    foreach (var term in m.Terms)
                    {
                        long index = 0, scale = 1;
                        foreach (var part in term.Parts)
                        {
                            index += ReadValue(s, part) * scale;
                            scale <<= part.Bits;
                        }
                        if (term.Table[index] > best) best = term.Table[index];
                    }
                    WriteByte(d, m.Dst, best);
                    break;
                }
                default:
                    throw new InvalidOperationException("the note defines no op " + op.GetType().Name);
            }
        }

        // 3. The conditional blocks, in list order, each block's ops in list
        // order, a later write replacing an earlier one.
        foreach (var op in p.Ops)
        {
            if (op is not WhenOp w || !Holds(w.Cond, regs)) continue;
            foreach (var sub in w.Ops)
            {
                switch (sub)
                {
                    case ConstOp k:
                        for (int i = 0; i < k.Bytes.Length; i++) d[k.At + i] = k.Bytes[i];
                        break;
                    case CopyOp c:
                        for (int i = 0; i < c.Len; i++) d[c.Dst + i] = s[c.Src + i];
                        break;
                    case CounterOp k:
                        Counter(d, k, ctr);
                        break;
                }
            }
        }
    }
}
