using System;
using System.Collections.Generic;
using System.Linq;

namespace HIDMaestro.Internal.Programs;

/// <summary>Seeded random programs for the equivalence check. The tool draws
/// the corpus at build time to emit its kernels, and the check draws it again
/// from the same seed at run time, so both read one list. Every program here
/// is valid under ProgramForm, overlaps included: template bytes under
/// whole-byte ops, template bits beside the bits an OR op writes, and
/// conditional blocks over any byte. Operands are drawn to the edges of
/// their domains: reads of up to 25 bits at shift 7, centers at both ends
/// of a read's raw range, scaled ranges and clamps near 2^30, dead zones up
/// to 65,535. Negative programs, each breaking one rule, come from
/// Negatives.</summary>
internal static class Corpus
{
    public const int Seed = 20261017;
    public const int Count = 1000;
    public const int StateSize = 64;

    public static IEnumerable<ProgramSpec> Draw(int seed = Seed, int count = Count)
    {
        var r = new Random(seed);
        for (int i = 0; i < count; i++) yield return Program(r, $"fuzz{i}");
    }

    static byte[] Bytes(Random r, int n) { var b = new byte[n]; r.NextBytes(b); return b; }

    public static ProgramSpec Program(Random r, string name)
    {
        int size = r.Next(4, 65);
        int[] regBits = { 8, 16, 32, new[] { 8, 16, 32 }[r.Next(3)] };
        var ops = new List<Op>();
        var whole = new bool[size];
        var orBits = new byte[size];
        var templ = new bool[size];
        var tmplVal = new byte[size];

        bool Free(int at, int len)
        {
            if (at < 0 || at + len > size) return false;
            for (int i = at; i < at + len; i++) if (whole[i] || orBits[i] != 0) return false;
            return true;
        }
        int FindFree(int len)
        {
            for (int t = 0; t < 30; t++)
            {
                int at = r.Next(0, Math.Max(1, size - len + 1));
                if (Free(at, len)) return at;
            }
            return -1;
        }
        void Take(int at, int len) { for (int i = at; i < at + len; i++) whole[i] = true; }
        Src ReadSrc(int maxBits)
        {
            if (r.Next(6) == 0) return new Src(r.Next(0, StateSize - 1) * 8, 16, true);
            // A wide read at shift 7, the widest a 32-bit load holds there.
            if (maxBits >= 17 && r.Next(5) == 0)
            {
                int wide = r.Next(17, maxBits + 1);
                return new Src(r.Next(0, StateSize - 4) * 8 + 7, wide);
            }
            int bits = r.Next(1, maxBits + 1);
            int bit = r.Next(0, StateSize * 8 - bits + 1);
            if ((bit & 7) + bits > 32) bit &= ~7;
            return new Src(bit, bits);
        }
        long RangeOf(Src s) => s.BigEndianWord ? 0xFFFF : (1L << s.Bits) - 1;
        // A center at either end of the read's raw range, or inside it.
        int CenterIn(long range) => r.Next(4) switch
        {
            0 => 0,
            1 => (int)range,
            _ => (int)r.NextInt64(0, range + 1),
        };
        Condition Cond()
        {
            var groups = new List<Term[]>();
            for (int g = 0, ng = r.Next(1, 4); g < ng; g++)
            {
                var terms = new List<Term>();
                for (int t = 0, nt = r.Next(1, 4); t < nt; t++)
                {
                    int reg = r.Next(0, 4);
                    int w = regBits[reg];
                    uint widthMask = w == 32 ? uint.MaxValue : (1u << w) - 1;
                    // Masks from one bit up to the register's width, wide ones included.
                    uint mask = r.Next(3) switch
                    {
                        0 => (uint)(1 << r.Next(0, w)),
                        1 => (uint)r.NextInt64(1, (long)widthMask + 1),
                        _ => widthMask,
                    };
                    if (mask == 0) mask = 1;
                    if (r.Next(4) == 0)
                    {
                        var set = new uint[r.Next(1, 4)];
                        for (int k = 0; k < set.Length; k++) set[k] = (uint)r.NextInt64(0, (long)widthMask + 1) & mask;
                        terms.Add(new Term(reg, mask, 0, r.Next(3) == 0, set));
                    }
                    else terms.Add(new Term(reg, mask, (uint)r.NextInt64(0, (long)widthMask + 1) & mask, r.Next(3) == 0));
                }
                groups.Add(terms.ToArray());
            }
            return new Condition(groups.ToArray());
        }

        // The template, without two consts on one byte.
        for (int i = 0, n = r.Next(0, 4); i < n; i++)
        {
            int at = r.Next(0, size), len = r.Next(1, Math.Min(8, size - at) + 1);
            bool ok = true;
            for (int k = at; k < at + len; k++) if (templ[k]) ok = false;
            if (!ok) continue;
            for (int k = at; k < at + len; k++) templ[k] = true;
            var bytes = Bytes(r, len);
            // Some template bytes are zero, and some carry few bits, so OR ops
            // land on them with their bits beside the template's.
            for (int k = 0; k < len; k++)
                bytes[k] = r.Next(3) switch { 0 => 0, 1 => (byte)(bytes[k] & 0x11), _ => bytes[k] };
            for (int k = 0; k < len; k++) tmplVal[at + k] = bytes[k];
            ops.Add(new ConstOp(at, bytes));
        }

        for (int k = 0, count = r.Next(1, 12); k < count; k++)
        {
            switch (r.Next(12))
            {
                case 0:
                {
                    int len = r.Next(1, 17), at = FindFree(len);
                    if (at < 0) break;
                    Take(at, len);
                    ops.Add(new CopyOp(r.Next(0, StateSize - len + 1), at, len));
                    break;
                }
                case 1:
                {
                    var moves = new List<int>();
                    for (int m = 0, n = r.Next(1, 9); m < n; m++)
                    {
                        int db = r.Next(0, size * 8), d = db >> 3;
                        byte bit = (byte)(1 << (db & 7));
                        if (whole[d] || (orBits[d] & bit) != 0) continue;
                        // An OR op never writes a bit the template sets.
                        if ((tmplVal[d] & bit) != 0) continue;
                        orBits[d] |= bit;
                        moves.Add(r.Next(0, StateSize * 8)); moves.Add(db);
                    }
                    if (moves.Count > 0) ops.Add(new BitsOp(moves.ToArray()));
                    break;
                }
                case 2:
                {
                    int at = FindFree(1);
                    if (at < 0) break;
                    int bits = r.Next(1, 9), sb = r.Next(0, StateSize) * 8 + r.Next(0, 9 - bits);
                    orBits[at] = 0xFF;
                    // The table's entries keep clear of the template's bits.
                    var table = Bytes(r, 1 << bits);
                    for (int t = 0; t < table.Length; t++) table[t] &= (byte)~tmplVal[at];
                    ops.Add(new LookupOp(sb, bits, at, table));
                    break;
                }
                case 3:
                {
                    int len = r.Next(1, 9), at = FindFree(len);
                    if (at < 0) break;
                    // An xor writes all eight bits, so its template bytes are zero.
                    bool clear = true;
                    for (int i = at; i < at + len; i++) if (tmplVal[i] != 0) clear = false;
                    if (!clear) break;
                    for (int i = at; i < at + len; i++) orBits[i] = 0xFF;
                    ops.Add(new XorOp(r.Next(0, StateSize - len + 1), at, len, (byte)r.Next(256)));
                    break;
                }
                case 4:
                {
                    int words = r.Next(1, 5), at = FindFree(words * 2);
                    if (at < 0) break;
                    Take(at, words * 2);
                    ops.Add(new Swap16Op(r.Next(0, StateSize - words * 2 + 1), at, words));
                    break;
                }
                case 5:
                {
                    int outBytes = r.Next(1, 3), at = FindFree(outBytes);
                    if (at < 0) break;
                    var src = ReadSrc(25);
                    long range = RangeOf(src);
                    int center = CenterIn(range);
                    long maxAbs = Math.Max(center, range - center);
                    int offset = r.Next(-1000, 1001);
                    int num = r.Next(-40000, 40001), den = r.Next(1, 5000);
                    if (r.Next(3) == 0) { num = 1; den = 1; }
                    // A quarter of the fields scale to just under 2^30.
                    if (r.Next(4) == 0 && maxAbs > 0)
                    {
                        long top = ((1L << 30) - 1 - Math.Abs(offset)) / maxAbs;
                        num = (int)Math.Min(top, int.MaxValue) * (r.Next(2) == 0 ? 1 : -1);
                        den = 1;
                    }
                    if (maxAbs * Math.Abs((long)num) / den + Math.Abs(offset) >= 1L << 30) { num = 1; den = 1; }
                    int min, max;
                    if (r.Next(4) == 0) { min = -((1 << 30) - 1); max = (1 << 30) - 1; }
                    else { min = r.Next(-70000, 70001); max = r.Next(min, 140001); }
                    Take(at, outBytes);
                    ops.Add(new FieldOp(src, center, r.Next(2) == 0, num, den, offset, min, max, r.Next(4) == 0, at, outBytes, r.Next(2) == 0));
                    break;
                }
                case 6:
                {
                    int at = FindFree(3);
                    if (at < 0) break;
                    Take(at, 3);
                    ops.Add(new Pack12Op(r.Next(0, StateSize - 1), r.Next(0, StateSize - 1), at));
                    break;
                }
                case 7:
                {
                    int width = r.Next(1, 5), at = FindFree(width);
                    if (at < 0) break;
                    Take(at, width);
                    int slot = r.Next(0, 4);
                    uint scale = (uint)r.Next(0, 5000), add = (uint)r.Next();
                    // Byte order from a bit of the add, so the draws stay
                    // the prototype's.
                    ops.Add(new CounterOp(slot, at, width, scale, add, (add & 0x100) != 0));
                    break;
                }
                case 8:
                {
                    int at = FindFree(1);
                    if (at < 0) break;
                    Take(at, 1);
                    // Hat reads of 12, 16 and up to 25 bits, the center anywhere
                    // in the narrower read's range, ends included.
                    Src HatSrc(int kind) => kind switch
                    {
                        0 => new Src(r.Next(0, StateSize * 8 - 12 + 1), 12),
                        1 => new Src(r.Next(0, StateSize - 1) * 8, 16, r.Next(2) == 0),
                        _ => new Src(r.Next(0, StateSize - 4) * 8 + 7, r.Next(17, 26)),
                    };
                    int kind = r.Next(3);
                    Src x = HatSrc(kind), y = HatSrc(kind);
                    int center = CenterIn(Math.Min(RangeOf(x), RangeOf(y)));
                    int dz = r.Next(5) switch { 0 => 0, 1 => 65535, _ => r.Next(0, 65536) };
                    ops.Add(new Hat8Op(x, y, center, dz, r.Next(2) == 0, Bytes(r, 9), at));
                    break;
                }
                case 9:
                {
                    int at = FindFree(1);
                    if (at < 0) break;
                    Take(at, 1);
                    var terms = new List<DecodeTerm>();
                    for (int t = 0, n = r.Next(1, 7); t < n; t++)
                    {
                        // Up to 12 bits a term, 11 and 12 drawn on purpose.
                        int total = r.Next(4) == 0 ? r.Next(11, 13) : r.Next(1, 11);
                        var parts = new List<Src>();
                        int left = total;
                        while (left > 0)
                        {
                            int bits = parts.Count == 2 ? left : r.Next(1, left + 1);
                            int bit = r.Next(0, StateSize * 8 - bits + 1);
                            parts.Add(new Src(bit, bits));
                            left -= bits;
                        }
                        terms.Add(new DecodeTerm(parts.ToArray(), Bytes(r, 1 << total)));
                    }
                    ops.Add(new MaxOfOp(terms.ToArray(), at));
                    break;
                }
                default:
                {
                    // A conditional block over any byte: blocks run after
                    // every unconditional op and may overlap anything.
                    var sub = new List<Op>();
                    for (int s = 0, ns = r.Next(1, 4); s < ns; s++)
                    {
                        int len = r.Next(1, 6), at = r.Next(0, Math.Max(1, size - len + 1));
                        if (at + len > size) continue;
                        sub.Add(r.Next(3) switch
                        {
                            0 => new ConstOp(at, Bytes(r, len)),
                            1 => new CopyOp(r.Next(0, StateSize - len + 1), at, len),
                            _ => BlockCounter(r, at, Math.Min(len, 4)),
                        });
                    }
                    if (sub.Count > 0) ops.Add(new WhenOp(Cond(), sub.ToArray()));
                    break;
                }
            }
        }
        return new ProgramSpec(name, size, StateSize, regBits, ops.ToArray());
    }

    static CounterOp BlockCounter(Random r, int at, int width)
    {
        int slot = r.Next(0, 4);
        uint scale = (uint)r.Next(0, 5000), add = (uint)r.Next();
        return new CounterOp(slot, at, width, scale, add, (add & 0x100) != 0);
    }

    /// <summary>One program per rule, each breaking only that rule, with the
    /// path validation must name.</summary>
    public static IEnumerable<(string rule, ProgramSpec program, string path)> Negatives()
    {
        int[] r8 = { 8 }, r16 = { 16 };
        var always = new Condition(new[] { new[] { new Term(0, 0xFF, 0, true) } });
        ProgramSpec P(int size, int state, int[] regs, params Op[] ops) => new("negative", size, state, regs, ops);
        yield return ("an unconditional byte written twice", P(8, 8, r8, new CopyOp(0, 2, 2), new CopyOp(4, 3, 1)), "$.ops[1]");
        yield return ("a whole byte over ORed bits", P(8, 8, r8, new BitsOp(new[] { 0, 16 }), new CopyOp(0, 2, 1)), "$.ops[1]");
        yield return ("one report bit ORed twice", P(8, 8, r8, new BitsOp(new[] { 0, 16, 1, 16 })), "$.ops[0].moves[3]");
        yield return ("a template byte set twice", P(8, 8, r8, new ConstOp(2, new byte[] { 1, 2 }), new ConstOp(3, new byte[] { 3 })), "$.ops[1]");
        yield return ("a 28-bit read", P(8, 8, r8, new FieldOp(new Src(6, 28), 0, false, 1, 1 << 20, 0, -(1 << 29), (1 << 29) - 1, false, 0, 2, false)), "$.ops[0].from.bits");
        yield return ("a read past the state block", P(8, 8, r8, new FieldOp(new Src(60, 8), 0, false, 1, 1, 0, 0, 255, false, 0, 1, false)), "$.ops[0].from.bit");
        yield return ("a lookup spanning two state bytes", P(4, 8, r8, new LookupOp(6, 4, 0, new byte[16])), "$.ops[0].bits");
        yield return ("a lookup table of the wrong size", P(4, 8, r8, new LookupOp(0, 4, 0, new byte[8])), "$.ops[0].table");
        yield return ("a field inside a conditional block", P(8, 8, r8, new WhenOp(always, new Op[] { new FieldOp(new Src(0, 8), 0, false, 1, 1, 0, 0, 255, false, 0, 1, false) })), "$.ops[0].ops[0]");
        yield return ("a mask wider than its register", P(8, 8, r8, new WhenOp(new Condition(new[] { new[] { new Term(0, 0x1FF, 0, false) } }), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when[0][0].mask");
        yield return ("a value outside its mask", P(8, 8, r16, new WhenOp(new Condition(new[] { new[] { new Term(0, 0x0F00, 0x00F0, false) } }), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when[0][0].value");
        yield return ("an in value outside its mask", P(8, 8, r16, new WhenOp(new Condition(new[] { new[] { new Term(0, 0x00FF, 0, false, new uint[] { 1, 0x100 }) } }), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when[0][0].in[1]");
        yield return ("a register that does not exist", P(8, 8, r8, new WhenOp(new Condition(new[] { new[] { new Term(1, 0x1, 0, false) } }), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when[0][0].register");
        yield return ("a decode term of 13 bits", P(2, 8, r8, new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(0, 7), new Src(8, 6) }, new byte[1 << 13]) }, 0)), "$.ops[0].terms[0].parts");
        yield return ("a decode table of the wrong size", P(2, 8, r8, new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(0, 4) }, new byte[8]) }, 0)), "$.ops[0].terms[0].table");
        yield return ("a counter of 5 bytes", P(8, 8, r8, new CounterOp(0, 0, 5, 1, 0)), "$.ops[0].width");
        yield return ("a field whose scaled range passes 2^30", P(8, 8, r8, new FieldOp(new Src(0, 16), 0, false, 40000, 1, 0, 0, 255, false, 0, 1, false)), "$.ops[0].num");
        yield return ("a block write outside the report", P(8, 8, r8, new WhenOp(always, new Op[] { new ConstOp(7, new byte[] { 1, 2 }) })), "$.ops[0].ops[0]");
    }
}
