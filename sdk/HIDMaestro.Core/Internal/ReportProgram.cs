using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal;

// The run-time path of mechanism 2: a ValidProgram compiled once, at create,
// into stage arrays that one method walks without per-op dispatch. A program
// no generated kernel matches runs here: a profile from outside the catalog,
// or an edited copy of a catalog program. Unconditional stages may run in any
// order, since the validated form gives each report bit one unconditional
// writer. Blocks run after them, in list order, each block's ops in list
// order, which is the form's write order.

[StructLayout(LayoutKind.Sequential)] internal struct CopyE { public int Src, Dst, Len; }
[StructLayout(LayoutKind.Sequential)] internal struct RemapE { public int Src, Dst, Table; }
[StructLayout(LayoutKind.Sequential)]
internal struct FieldE
{
    public int ByteOff, Shift, Bits; public uint Mask; public int BigEndianWord;
    public int Center, Num, Den, Offset, Min, Max, Wrap16, Negate;
    public int Dst, OutBytes, OutBigEndian;
}
[StructLayout(LayoutKind.Sequential)] internal struct PackE { public int X, Y, Dst; }
[StructLayout(LayoutKind.Sequential)] internal struct CounterE { public int Slot, Dst, Width, BigEndian; public uint Scale, Add; }
[StructLayout(LayoutKind.Sequential)] internal struct HatE { public FieldE X, Y; public int Center; public long Dz2; public int YUp, Table, Dst; }
[StructLayout(LayoutKind.Sequential)] internal struct TermE { public int Register; public uint Mask, Value; public int NotEqual, InStart, InCount; }
[StructLayout(LayoutKind.Sequential)] internal struct MaxE { public int Dst, TermStart, TermCount; }
[StructLayout(LayoutKind.Sequential)] internal struct DTermE { public int PartStart, PartCount, Table; }
/// <summary>A block op: 0 const (Len bytes from the const arena at Src),
/// 1 copy, 2 counter.</summary>
[StructLayout(LayoutKind.Sequential)] internal struct BlockOpE { public int Kind, Src, Dst, Len, Slot, BigEndian; public uint Scale, Add; }
[StructLayout(LayoutKind.Sequential)] internal struct BlockE { public int GroupStart, GroupCount, OpStart, OpCount; }

/// <summary>The interpreter's form of one program. Its arrays are pinned and
/// held by the instance, so its stages are read by pointer and live as long
/// as the program does.</summary>
internal sealed unsafe class ReportProgram
{
    public readonly int Size;
    public readonly ValidProgram Program;

    // Each array is pinned on the pinned object heap and referenced here, so
    // the pointer beside it stays valid for this object's life.
    private readonly object[] _keep;
    readonly byte* _template;
    readonly CopyE* _copies; readonly int _nCopies;
    readonly RemapE* _remaps; readonly int _nRemaps;
    readonly byte* _tables;
    readonly CopyE* _swaps; readonly int _nSwaps;
    readonly FieldE* _fields; readonly int _nFields;
    readonly PackE* _packs; readonly int _nPacks;
    readonly CounterE* _counters; readonly int _nCounters;
    readonly HatE* _hats; readonly int _nHats;
    readonly MaxE* _maxes; readonly int _nMaxes;
    readonly DTermE* _dterms; readonly FieldE* _dparts;
    readonly BlockE* _blocks; readonly int _nBlocks;
    readonly int* _groupStart; readonly int* _groupCount;
    readonly TermE* _terms; readonly uint* _inValues;
    readonly BlockOpE* _blockOps; readonly byte* _constArena;

    /// <summary>The interpreter accepts only the validated form.
    /// <paramref name="mutation"/> plants one of program_equivalence_check's
    /// defects, and is None everywhere else.</summary>
    public ReportProgram(ValidProgram v, InterpMutation mutation = InterpMutation.None)
    {
        Program = v;
        Size = v.Spec.Size;
        var keep = new List<object>();
        T* Pin<T>(IList<T> list) where T : unmanaged
        {
            var a = GC.AllocateArray<T>(Math.Max(1, list.Count), pinned: true);
            for (int i = 0; i < list.Count; i++) a[i] = list[i];
            keep.Add(a);
            return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(a));
        }

        var copies = new List<CopyE>(); var swaps = new List<CopyE>(); var fields = new List<FieldE>();
        var packs = new List<PackE>(); var counters = new List<CounterE>(); var hats = new List<HatE>();
        var maxes = new List<MaxE>(); var dterms = new List<DTermE>(); var dparts = new List<FieldE>();
        var remap = new Dictionary<(int, int), byte[]>();
        var extraTables = new List<byte>();
        var tableAt = new Dictionary<string, int>();
        int ExtraTable(byte[] t)
        {
            string key = Convert.ToHexString(t);
            if (!tableAt.TryGetValue(key, out int at)) { tableAt[key] = at = extraTables.Count; extraTables.AddRange(t); }
            return at;
        }
        byte[] Table(int s, int d)
        {
            if (!remap.TryGetValue((s, d), out var t)) remap[(s, d)] = t = new byte[256];
            return t;
        }
        var hatTableAt = new List<(int index, int table)>();
        var dtermTableAt = new List<(int index, int table)>();

        foreach (var op in v.Unconditional)
        {
            switch (op)
            {
                case CopyOp c: copies.Add(new CopyE { Src = c.Src, Dst = c.Dst, Len = c.Len }); break;
                case BitsOp b:
                    // Every bit move from one state byte to one report byte
                    // becomes one 256-entry table, ORed in with the others.
                    for (int i = 0; i < b.Moves.Length; i += 2)
                    {
                        int sb = b.Moves[i], db = b.Moves[i + 1];
                        var t = Table(sb >> 3, db >> 3);
                        byte bit = (byte)(1 << (db & 7));
                        for (int x = 0; x < 256; x++) if (((x >> (sb & 7)) & 1) != 0) t[x] |= bit;
                    }
                    break;
                case LookupOp l:
                {
                    var t = Table(l.SrcBit >> 3, l.Dst);
                    int mask = (1 << l.Bits) - 1;
                    for (int x = 0; x < 256; x++) t[x] |= l.Table[(x >> (l.SrcBit & 7)) & mask];
                    break;
                }
                case XorOp x:
                    for (int i = 0; i < x.Len; i++) { var t = Table(x.Src + i, x.Dst + i); for (int y = 0; y < 256; y++) t[y] = (byte)(y ^ x.Mask); }
                    break;
                case Swap16Op w: swaps.Add(new CopyE { Src = w.Src, Dst = w.Dst, Len = w.Words }); break;
                case FieldOp f:
                {
                    var e = Field(f.From, f);
                    if (mutation == InterpMutation.FieldOffsetPlusOne && fields.Count == 0) e.Offset += 1;
                    fields.Add(e);
                    break;
                }
                case Pack12Op p: packs.Add(new PackE { X = p.SrcX, Y = p.SrcY, Dst = p.Dst }); break;
                case CounterOp c: counters.Add(new CounterE { Slot = c.Slot, Dst = c.Dst, Width = c.Width, BigEndian = c.BigEndian ? 1 : 0, Scale = c.Scale, Add = c.Add }); break;
                case Hat8Op h:
                    hatTableAt.Add((hats.Count, ExtraTable(h.Table9)));
                    hats.Add(new HatE { X = Field(h.X, null), Y = Field(h.Y, null), Center = h.Center, Dz2 = (long)h.Deadzone * h.Deadzone, YUp = h.YUp ? 1 : 0, Dst = h.Dst });
                    break;
                case MaxOfOp m:
                    maxes.Add(new MaxE { Dst = m.Dst, TermStart = dterms.Count, TermCount = m.Terms.Length });
                    foreach (var term in m.Terms)
                    {
                        dtermTableAt.Add((dterms.Count, ExtraTable(term.Table)));
                        dterms.Add(new DTermE { PartStart = dparts.Count, PartCount = term.Parts.Length });
                        foreach (var part in term.Parts) dparts.Add(Field(part, null));
                    }
                    break;
                default: throw new InvalidOperationException("no stage for " + op.GetType().Name);
            }
        }

        var remapList = new List<RemapE>();
        var tables = new List<byte>();
        foreach (var ((s, d), t) in remap.OrderBy(k => k.Key.Item2).ThenBy(k => k.Key.Item1))
        {
            remapList.Add(new RemapE { Src = s, Dst = d, Table = tables.Count });
            tables.AddRange(t);
        }
        int extraBase = tables.Count;
        foreach (var (i, t) in hatTableAt) { var h = hats[i]; h.Table = extraBase + t; hats[i] = h; }
        foreach (var (i, t) in dtermTableAt) { var e = dterms[i]; e.Table = extraBase + t; dterms[i] = e; }

        var blocks = new List<BlockE>(); var gs = new List<int>(); var gc = new List<int>();
        var terms = new List<TermE>(); var inValues = new List<uint>(); var blockOps = new List<BlockOpE>(); var consts = new List<byte>();
        foreach (var w in v.Blocks)
        {
            blocks.Add(new BlockE { GroupStart = gs.Count, GroupCount = w.Cond.Groups.Length, OpStart = blockOps.Count, OpCount = w.Ops.Length });
            foreach (var g in w.Cond.Groups)
            {
                gs.Add(terms.Count); gc.Add(g.Length);
                foreach (var t in g)
                {
                    bool ne = t.NotEqual && !(mutation == InterpMutation.NotEqualIgnored && terms.Count == 0);
                    terms.Add(new TermE { Register = t.Register, Mask = t.Mask, Value = t.Value, NotEqual = ne ? 1 : 0, InStart = inValues.Count, InCount = t.In?.Length ?? 0 });
                    if (t.In != null) inValues.AddRange(t.In);
                }
            }
            foreach (var sub in w.Ops)
            {
                switch (sub)
                {
                    case ConstOp k: blockOps.Add(new BlockOpE { Kind = 0, Src = consts.Count, Dst = k.At, Len = k.Bytes.Length }); consts.AddRange(k.Bytes); break;
                    case CopyOp c: blockOps.Add(new BlockOpE { Kind = 1, Src = c.Src, Dst = c.Dst, Len = c.Len }); break;
                    case CounterOp c: blockOps.Add(new BlockOpE { Kind = 2, Slot = c.Slot, Dst = c.Dst, Len = c.Width, BigEndian = c.BigEndian ? 1 : 0, Scale = c.Scale, Add = c.Add }); break;
                    default: throw new InvalidOperationException("no block stage for " + sub.GetType().Name);
                }
            }
        }

        _template = Pin(v.Template);
        _copies = Pin(copies); _nCopies = copies.Count;
        _remaps = Pin(remapList); _nRemaps = remapList.Count;
        _tables = Pin(tables.Concat(extraTables).ToList());
        _swaps = Pin(swaps); _nSwaps = swaps.Count;
        _fields = Pin(fields); _nFields = fields.Count;
        _packs = Pin(packs); _nPacks = packs.Count;
        _counters = Pin(counters); _nCounters = counters.Count;
        _hats = Pin(hats); _nHats = hats.Count;
        _maxes = Pin(maxes); _nMaxes = maxes.Count;
        _dterms = Pin(dterms); _dparts = Pin(dparts);
        _blocks = Pin(blocks); _nBlocks = blocks.Count;
        _groupStart = Pin(gs); _groupCount = Pin(gc);
        _terms = Pin(terms); _inValues = Pin(inValues);
        _blockOps = Pin(blockOps); _constArena = Pin(consts);
        _keep = keep.ToArray();
    }

    static FieldE Field(Src s, FieldOp? f)
    {
        var e = new FieldE
        {
            ByteOff = s.BitOffset >> 3, Shift = s.BitOffset & 7, Bits = s.Bits,
            Mask = s.Bits >= 32 ? uint.MaxValue : (1u << s.Bits) - 1, BigEndianWord = s.BigEndianWord ? 1 : 0,
        };
        if (f != null)
        {
            e.Center = f.Center; e.Num = f.Num; e.Den = f.Den; e.Offset = f.Offset; e.Min = f.Min; e.Max = f.Max;
            e.Wrap16 = f.Wrap16 ? 1 : 0; e.Negate = f.Negate ? 1 : 0; e.Dst = f.Dst; e.OutBytes = f.OutBytes; e.OutBigEndian = f.OutBigEndian ? 1 : 0;
        }
        return e;
    }

    /// <summary>Every value read is one unaligned 32-bit load, as in the
    /// generated path: the state block carries <see cref="ProgramForm.Slack"/>
    /// bytes past its end for it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Read(byte* s, in FieldE f)
    {
        if (f.BigEndianWord != 0) return (uint)((s[f.ByteOff] << 8) | s[f.ByteOff + 1]);
        uint raw = Unsafe.ReadUnaligned<uint>(s + f.ByteOff);
        return (raw >> f.Shift) & f.Mask;
    }

    /// <summary>Runs the program. Never compiled at Tier0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Run(byte* s, byte* d, uint* regs, uint* counters)
    {
        Unsafe.CopyBlockUnaligned(d, _template, (uint)Size);
        for (int i = 0; i < _nCopies; i++) { ref var c = ref _copies[i]; Unsafe.CopyBlockUnaligned(d + c.Dst, s + c.Src, (uint)c.Len); }
        byte* tb = _tables;
        for (int i = 0; i < _nRemaps; i++) { ref var r = ref _remaps[i]; d[r.Dst] |= tb[r.Table + s[r.Src]]; }
        for (int i = 0; i < _nSwaps; i++)
        {
            ref var w = ref _swaps[i];
            for (int k = 0; k < w.Len; k++) { d[w.Dst + 2 * k] = s[w.Src + 2 * k + 1]; d[w.Dst + 2 * k + 1] = s[w.Src + 2 * k]; }
        }
        for (int i = 0; i < _nFields; i++)
        {
            ref var f = ref _fields[i];
            int v = (int)Read(s, f) - f.Center;
            if (f.Negate != 0) v = -v;
            v = (int)((long)v * f.Num / f.Den) + f.Offset;
            if (f.Wrap16 != 0) v &= 0xFFFF;
            else v = v < f.Min ? f.Min : v > f.Max ? f.Max : v;
            if (f.OutBytes == 1) d[f.Dst] = (byte)v;
            else if (f.OutBigEndian == 0) { d[f.Dst] = (byte)v; d[f.Dst + 1] = (byte)(v >> 8); }
            else { d[f.Dst] = (byte)(v >> 8); d[f.Dst + 1] = (byte)v; }
        }
        for (int i = 0; i < _nPacks; i++)
        {
            ref var p = ref _packs[i];
            ushort x = Unsafe.ReadUnaligned<ushort>(s + p.X), y = Unsafe.ReadUnaligned<ushort>(s + p.Y);
            d[p.Dst] = (byte)x; d[p.Dst + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)); d[p.Dst + 2] = (byte)(y >> 4);
        }
        for (int i = 0; i < _nCounters; i++)
        {
            ref var c = ref _counters[i];
            uint v = unchecked(counters[c.Slot] * c.Scale + c.Add);
            for (int k = 0; k < c.Width; k++) d[c.Dst + k] = (byte)(v >> (8 * (c.BigEndian != 0 ? c.Width - 1 - k : k)));
        }
        for (int i = 0; i < _nHats; i++)
        {
            ref var h = ref _hats[i];
            int x = (int)Read(s, h.X) - h.Center, y = (int)Read(s, h.Y) - h.Center;
            if (h.YUp == 0) y = -y;
            int sector;
            // Squared in 64 bits: a 16-bit stick's full diagonal wraps 32.
            if ((long)x * x + (long)y * y <= h.Dz2) sector = 8;
            else
            {
                int ax = x < 0 ? -x : x, ay = y < 0 ? -y : y;
                bool mostlyY = (long)ax * 1000000 < (long)ay * 2414214;
                bool mostlyX = (long)ay * 1000000 < (long)ax * 2414214;
                if (mostlyY && (long)ax * 1000000 <= (long)ay * 414214) sector = y > 0 ? 0 : 4;
                else if (mostlyX && (long)ay * 1000000 <= (long)ax * 414214) sector = x > 0 ? 2 : 6;
                else sector = x > 0 ? (y > 0 ? 1 : 3) : (y > 0 ? 7 : 5);
            }
            d[h.Dst] = tb[h.Table + sector];
        }
        for (int i = 0; i < _nMaxes; i++)
        {
            ref var m = ref _maxes[i];
            uint best = 0;
            for (int t = m.TermStart; t < m.TermStart + m.TermCount; t++)
            {
                ref var term = ref _dterms[t];
                uint idx = 0; int sh = 0;
                for (int k = term.PartStart; k < term.PartStart + term.PartCount; k++)
                {
                    ref var part = ref _dparts[k];
                    idx |= Read(s, part) << sh;
                    sh += part.Bits;
                }
                int g = tb[term.Table + idx] - (int)best;
                best += (uint)(g & ~(g >> 31));
            }
            d[m.Dst] = (byte)best;
        }
        for (int b = 0; b < _nBlocks; b++)
        {
            ref var blk = ref _blocks[b];
            bool any = false;
            for (int g = blk.GroupStart; g < blk.GroupStart + blk.GroupCount && !any; g++)
            {
                bool all = true;
                int t0 = _groupStart[g], tn = _groupCount[g];
                for (int t = t0; t < t0 + tn; t++)
                {
                    ref var term = ref _terms[t];
                    uint masked = regs[term.Register] & term.Mask;
                    bool hit;
                    if (term.InCount == 0) hit = masked == term.Value;
                    else
                    {
                        hit = false;
                        for (int k = term.InStart; k < term.InStart + term.InCount; k++) if (masked == _inValues[k]) { hit = true; break; }
                    }
                    if (hit == (term.NotEqual != 0)) { all = false; break; }
                }
                any = all;
            }
            if (!any) continue;
            for (int o = blk.OpStart; o < blk.OpStart + blk.OpCount; o++)
            {
                ref var op = ref _blockOps[o];
                if (op.Kind == 0) Unsafe.CopyBlockUnaligned(d + op.Dst, _constArena + op.Src, (uint)op.Len);
                else if (op.Kind == 1) Unsafe.CopyBlockUnaligned(d + op.Dst, s + op.Src, (uint)op.Len);
                else
                {
                    uint v = unchecked(counters[op.Slot] * op.Scale + op.Add);
                    for (int k = 0; k < op.Len; k++) d[op.Dst + k] = (byte)(v >> (8 * (op.BigEndian != 0 ? op.Len - 1 - k : k)));
                }
            }
        }
        GC.KeepAlive(_keep);
    }
}
