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

        var head = Head(v.Template, size, whole, orTerms, mutation);

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

    /// <summary>The template in 16-byte stores, then 8, 4, 2 and 1, skipping
    /// bytes an unconditional whole-byte write covers. ORed bytes fold their
    /// template bits into one store.</summary>
    static List<string> Head(byte[] template, int size, bool[] whole, List<string>?[] orTerms, PathMutation mutation)
    {
        var head = new List<string>();
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
        return head;
    }

    /// <summary>A state program's kernel: the template and the ORed bits of
    /// the buttons and flags, then each linear and scaled value, through
    /// WireArith, the arithmetic the codec calls, with the field's constants
    /// in place.</summary>
    public static string EmitStateKernel(ValidProgram v, string? name = null)
    {
        name ??= v.KernelName;
        int size = v.Spec.Size;
        var orTerms = new List<string>?[size];
        var whole = new bool[size];
        var body = new List<string>();
        var tail = new List<string>();
        var tables = new List<string>();
        bool buttons = false, hat = false, resolved = false;
        void AddOr(int dst, string term) => (orTerms[dst] ??= new List<string>()).Add(term);
        void Whole(int at, int n) { for (int i = 0; i < n; i++) whole[at + i] = true; }
        foreach (var op in v.Unconditional)
        {
            switch (op)
            {
                case GpRawOp g:
                    Whole(g.Dst, g.Bytes);
                    body.Add(F($"        {{ uint v = (uint){RawSource(g.Src)};"));
                    for (int i = 0; i < g.Bytes; i++) body.Add(F($"          d[{g.Dst + i}] = (byte)(v >> {8 * i});"));
                    body.Add("        }");
                    break;
                case GpPressureOp g:
                {
                    Whole(g.Dst, 1);
                    var (field, down) = Pressure(g.Button);
                    if (g.Button >= GpPressure.DpadUp) hat = true; else buttons = true;
                    body.Add(F($"        {{ byte p = st.{field}; d[{g.Dst}] = p != 0 ? p : ({down} ? (byte)255 : (byte)0); }}"));
                    break;
                }
                case GpTableOp g:
                {
                    string t = F($"T{name}_{tables.Count}");
                    tables.Add(F($"    static ReadOnlySpan<byte> {t} => new byte[] {{ {string.Join(", ", g.Table.Select(x => F($"0x{x:X2}")))} }};"));
                    string v2 = F($"{t}[(byte)st.{g.Src}]");
                    if (g.Charging >= 0) v2 = F($"(st.BatteryCharging ? 0x{g.Charging:X2} : {v2})");
                    if (g.Full >= 0) v2 = F($"(st.BatteryFull ? 0x{g.Full:X2} : {v2})");
                    if (g.Whole) { Whole(g.Dst, 1); body.Add(F($"        d[{g.Dst}] = (byte){v2};")); }
                    else
                    {
                        int mask = ((1 << (g.BitHi - g.BitLo + 1)) - 1) << g.BitLo;
                        AddOr(g.Dst, F($"(((uint){v2} << {g.BitLo}) & 0x{mask:X2}u)"));
                    }
                    break;
                }
                case GpFingerOp g:
                {
                    Whole(g.Dst, 4);
                    int n = g.Finger;
                    body.Add(F($"        {{ uint x = st.TouchpadFinger{n}X, y = st.TouchpadFinger{n}Y;"));
                    body.Add(F($"          d[{g.Dst}] = (byte)((st.TouchpadFinger{n}Id & 0x7F) | (st.TouchpadFinger{n}Active ? 0x00 : 0x80));"));
                    body.Add(F($"          d[{g.Dst + 1}] = (byte)x; d[{g.Dst + 2}] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)); d[{g.Dst + 3}] = (byte)(y >> 4); }}"));
                    break;
                }
                case GpPadOp g:
                {
                    Whole(g.Dst, 2);
                    int n = g.Pad;
                    string axis = g.IsY ? "Y" : "X";
                    string span = g.IsY ? "1079f" : "1919f";
                    int slot = (g.Pad == 0 ? 0 : 2) + (g.IsY ? 1 : 0);
                    // VendorBlobCodec's int16-pad, its float arithmetic as it is.
                    body.Add("        {");
                    body.Add("          short v;");
                    body.Add(F($"          if (st.TouchpadFinger{n}Active)"));
                    body.Add("          {");
                    body.Add(F($"            float unit = Math.Clamp(st.TouchpadFinger{n}{axis} / {span}, 0f, 1f) - 0.5f;"));
                    if (g.Invert) body.Add("            unit = -unit;");
                    body.Add("            v = (short)Math.Clamp((int)Math.Round(unit * 2f * 32767f), -32767, 32767);");
                    body.Add("          }");
                    body.Add(g.OrStick
                        ? F($"          else v = (short)global::HIDMaestro.Internal.WireArith.Linear(sl[{slot}], {g.Min}, {g.Max}, {(g.Invert ? "true" : "false")}, 0.5);")
                        : "          else v = 0;");
                    body.Add(F($"          d[{g.Dst}] = (byte)v; d[{g.Dst + 1}] = (byte)(v >> 8);"));
                    body.Add("        }");
                    break;
                }
                case GpPadPressureOp g:
                    // VendorBlobCodec's uint16-pressure: a pressure not above
                    // 0 on a contact is full pressure.
                    Whole(g.Dst, 2);
                    body.Add(F($"        {{ float p = st.TouchpadFinger{g.Finger}Pressure;"));
                    body.Add(F($"          int v = global::HIDMaestro.Internal.WireArith.Linear(p > 0 ? p : st.TouchpadFinger{g.Finger}Active ? 1.0 : 0.0, {g.Min}, {g.Max}, false, 0);"));
                    body.Add(F($"          d[{g.Dst}] = (byte)v; d[{g.Dst + 1}] = (byte)(v >> 8); }}"));
                    break;
                case GpTriggerBitOp g:
                    AddOr(g.Bit >> 3, F($"(global::HIDMaestro.Internal.WireArith.Linear(sl[{(int)g.Src}], 0, {g.Max}, false, 0) >= {g.At} ? 0x{1 << (g.Bit & 7):X2}u : 0u)"));
                    break;
                case GpTenBitOp g:
                    Whole(g.Dst, 2);
                    body.Add(F($"        {{ int delta = (int)Math.Round((double){RawSource(g.Src)} * {g.Resolution} / {g.SourceResolution}, MidpointRounding.AwayFromZero);"));
                    body.Add(F($"          int v = Math.Clamp({g.Center} {(g.Invert ? "-" : "+")} delta, 0, 1023); d[{g.Dst}] = (byte)(v >> 8); d[{g.Dst + 1}] = (byte)v; }}"));
                    break;
                case CounterOp k:
                    Whole(k.Dst, k.Width);
                    body.AddRange(Counter(k, "        "));
                    break;
                case GpCrc32Op g:
                {
                    Whole(g.Dst, 4);
                    // The register after the prefix, folded at build time.
                    uint start = Crc32.Update(0xFFFFFFFFu, g.Prefix);
                    tail.Add(F($"        {{ uint crc = global::HIDMaestro.Internal.Programs.Crc32.Update(0x{start:X8}u, d + {g.From}, {Math.Max(0, g.To - g.From + 1)}) ^ 0xFFFFFFFFu;"));
                    tail.Add(F($"          d[{g.Dst}] = (byte)crc; d[{g.Dst + 1}] = (byte)(crc >> 8); d[{g.Dst + 2}] = (byte)(crc >> 16); d[{g.Dst + 3}] = (byte)(crc >> 24); }}"));
                    break;
                }
                case GpButtonsOp g:
                {
                    buttons = true;
                    // One masked shift per report byte and distance.
                    var groups = new SortedDictionary<(int dst, int shift), int>();
                    for (int i = 0; i < g.Moves.Length; i += 2)
                    {
                        int k = g.Moves[i], r = g.Moves[i + 1];
                        var key = (r >> 3, k - (r & 7));
                        groups[key] = (groups.TryGetValue(key, out var m) ? m : 0) | (1 << (r & 7));
                    }
                    foreach (var ((dst, shift), mask) in groups)
                        AddOr(dst, shift > 0 ? F($"((b >> {shift}) & 0x{mask:X2}u)")
                                 : shift < 0 ? F($"((b << {-shift}) & 0x{mask:X2}u)")
                                 : F($"(b & 0x{mask:X2}u)"));
                    break;
                }
                case GpFlagsOp g:
                    for (int i = 0; i < g.Flags.Length; i++)
                    {
                        hat |= g.Flags[i] is GpFlag.DpadUp or GpFlag.DpadDown or GpFlag.DpadLeft or GpFlag.DpadRight;
                        resolved |= g.Flags[i] >= GpFlag.DpadUpResolved;
                        AddOr(g.Bits[i] >> 3, F($"({Flag(g.Flags[i])} ? 0x{1 << (g.Bits[i] & 7):X2}u : 0u)"));
                    }
                    break;
                case GpLinearOp g:
                    whole[g.Dst] = true;
                    if (g.Store == GpStore.Word) whole[g.Dst + 1] = true;
                    body.Add(F($"        {{ int v = global::HIDMaestro.Internal.WireArith.Linear(sl[{(int)g.Src}], {g.Min}, {g.Max}, {(g.Invert ? "true" : "false")}, {D(g.Rest)});"));
                    body.Add(g.Store switch
                    {
                        GpStore.ByteClamped => F($"          d[{g.Dst}] = (byte)Math.Clamp(v, 0, 255); }}"),
                        GpStore.Byte => F($"          d[{g.Dst}] = (byte)v; }}"),
                        _ => F($"          d[{g.Dst}] = (byte)v; d[{g.Dst + 1}] = (byte)(v >> 8); }}"),
                    });
                    break;
                case GpStick12Op g:
                    // VendorBlobCodec's stick12-pair: the middle byte holds
                    // the high nibble of x and the low nibble of y.
                    Whole(g.Dst, 3);
                    body.Add(F($"        {{ int x = global::HIDMaestro.Internal.WireArith.Linear(sl[{(int)g.SrcX}], {g.Min}, {g.Max}, false, 0.5);"));
                    body.Add(F($"          int y = global::HIDMaestro.Internal.WireArith.Linear(sl[{(int)g.SrcY}], {g.Min}, {g.Max}, {(g.InvertY ? "true" : "false")}, 0.5);"));
                    body.Add(F($"          d[{g.Dst}] = (byte)x; d[{g.Dst + 1}] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)); d[{g.Dst + 2}] = (byte)(y >> 4); }}"));
                    break;
                case GpScaledOp g:
                {
                    // WireArith.Scaled with the ratio in place. A divisor of 1
                    // leaves the product as it is, and one that is a power of
                    // two is the same correctly rounded value as a product by
                    // its reciprocal. The value is clamped before it is
                    // rounded, which rounds the same: the int16 bounds are
                    // whole, and a value past one rounds to it or past it.
                    // Inside the bounds, the bias 1.5 x 2^52 rounds to nearest
                    // with ties to even, as Math.Round does. A NaN writes 0.
                    string src = F($"{(g.Negate ? "-" : "")}st.{g.Src}");
                    string product = g.Den == 1 ? F($"(double)f * {D(g.Num)}")
                        : IsPowerOfTwo(Math.Abs(g.Den)) ? F($"(double)f * {D(g.Num)} * {D(1 / g.Den)}")
                        : F($"(double)f * {D(g.Num)} / {D(g.Den)}");
                    body.Add(F($"        {{ float f = {src};"));
                    body.Add(F($"          int v = float.IsNaN(f) ? 0 : (int)BitConverter.DoubleToInt64Bits(Math.Min(Math.Max({product}, -32768.0), 32767.0) + 6755399441055744.0);"));
                    for (int k = 0; k < g.Count; k++)
                    {
                        int at = g.Dst + k * g.Stride;
                        whole[at] = whole[at + 1] = true;
                        body.Add(F($"          d[{at}] = (byte)v; d[{at + 1}] = (byte)(v >> 8);"));
                    }
                    body.Add("        }");
                    break;
                }
                default:
                    throw new InvalidOperationException("no state emission for " + op.GetType().Name);
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine(F($"    {Slashes} {v.Spec.Name}, a state program"));
        // Called through a function pointer, never inlined, so it is compiled
        // optimized at its first call rather than tiered: a submit never runs
        // it as Tier0 code while a background recompile waits.
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveOptimization)]");
        sb.AppendLine(F($"    public static unsafe void {name}(in global::HIDMaestro.HMGamepadState st, float* sl, byte* d, uint* c)"));
        sb.AppendLine("    {");
        if (buttons) sb.AppendLine("        uint b = (uint)st.Buttons;");
        if (hat) sb.AppendLine("        uint h = (byte)st.Hat;");
        if (resolved) sb.AppendLine("        global::HIDMaestro.Internal.VendorBlobCodec.ResolveDpad(in st, out bool ru, out bool rd, out bool rl, out bool rr);");
        sb.Append(string.Join("\n", Head(v.Template, size, whole, orTerms, PathMutation.None))).Append('\n');
        sb.Append(string.Join("\n", body)).Append('\n');
        if (tail.Count > 0) sb.Append(string.Join("\n", tail)).Append('\n');
        sb.AppendLine("    }");
        foreach (var t in tables) sb.AppendLine(t);
        return sb.ToString();
    }

    static string RawSource(GpRaw r) => r switch
    {
        GpRaw.SensorTimestamp => "st.SensorTimestamp",
        GpRaw.TouchpadPacketCounter => "st.TouchpadPacketCounter",
        _ => F($"st.{r}"),
    };

    /// <summary>A pressure-sensitive button's pressure field and its
    /// pressed test over b and h.</summary>
    static (string Field, string Down) Pressure(GpPressure p) => p switch
    {
        GpPressure.A => ("PressureA", "((b & 0x1u) != 0)"),
        GpPressure.B => ("PressureB", "((b & 0x2u) != 0)"),
        GpPressure.X => ("PressureX", "((b & 0x4u) != 0)"),
        GpPressure.Y => ("PressureY", "((b & 0x8u) != 0)"),
        GpPressure.LeftBumper => ("PressureLeftBumper", "((b & 0x10u) != 0)"),
        GpPressure.RightBumper => ("PressureRightBumper", "((b & 0x20u) != 0)"),
        GpPressure.DpadUp => ("PressureDpadUp", Flag(GpFlag.DpadUp)),
        GpPressure.DpadRight => ("PressureDpadRight", Flag(GpFlag.DpadRight)),
        GpPressure.DpadDown => ("PressureDpadDown", Flag(GpFlag.DpadDown)),
        _ => ("PressureDpadLeft", Flag(GpFlag.DpadLeft)),
    };

    /// <summary>A flag as a bool expression over st, sl and the hat h. A
    /// direction holds for its hat values, N 1 to NW 8, and for none past 8.</summary>
    static string Flag(GpFlag f) => f switch
    {
        GpFlag.LeftTriggerOn => "(sl[4] * 255.0 > 0.5)",
        GpFlag.RightTriggerOn => "(sl[5] * 255.0 > 0.5)",
        GpFlag.DpadUp => F($"(h < 9u && ((0x{HatMask(1, 2, 8):X}u >> (int)h) & 1u) != 0)"),
        GpFlag.DpadDown => F($"(h < 9u && ((0x{HatMask(4, 5, 6):X}u >> (int)h) & 1u) != 0)"),
        GpFlag.DpadLeft => F($"(h < 9u && ((0x{HatMask(6, 7, 8):X}u >> (int)h) & 1u) != 0)"),
        GpFlag.DpadRight => F($"(h < 9u && ((0x{HatMask(2, 3, 4):X}u >> (int)h) & 1u) != 0)"),
        GpFlag.DpadUpResolved => "ru",
        GpFlag.DpadDownResolved => "rd",
        GpFlag.DpadLeftResolved => "rl",
        GpFlag.DpadRightResolved => "rr",
        GpFlag.Finger0Active => "st.TouchpadFinger0Active",
        GpFlag.Finger1Active => "st.TouchpadFinger1Active",
        GpFlag.BatteryCharging => "st.BatteryCharging",
        GpFlag.BatteryFull => "st.BatteryFull",
        GpFlag.MicMuted => "st.MicMuted",
        GpFlag.HeadphonesConnected => "st.HeadphonesConnected",
        GpFlag.CableConnected => "(st.BatteryCharging || st.BatteryFull)",
        _ => throw new InvalidOperationException("no flag " + f),
    };

    static int HatMask(params int[] values) => values.Aggregate(0, (m, x) => m | (1 << x));

    /// <summary>True for a power of two, whose reciprocal is exact.</summary>
    static bool IsPowerOfTwo(double d)
    {
        if (!double.IsFinite(d) || d <= 0) return false;
        long bits = BitConverter.DoubleToInt64Bits(d);
        return (bits & 0xFFFFFFFFFFFFFL) == 0 && ((bits >> 52) & 0x7FF) is > 0 and < 0x7FF;
    }

    static string D(double d) => d.ToString("R", Inv);

    /// <summary>A comment's two slashes in emitted code.</summary>
    const string Slashes = "/" + "/";

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
        IEnumerable<(ValidProgram program, PathMutation mutation)>? mutants = null, IEnumerable<ValidProgram>? states = null)
    {
        var stateList = (states ?? Enumerable.Empty<ValidProgram>()).GroupBy(p => p.Hash).Select(g => g.First()).OrderBy(p => p.Hash, StringComparer.Ordinal).ToList();
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
        foreach (var v in stateList) sb.Append(EmitStateKernel(v));
        sb.AppendLine(F($"    {Slashes}/ <summary>Each state program's kernel by the SHA-256 of its canonical form.</summary>"));
        sb.AppendLine("    public static readonly Dictionary<string, nint> StateByHash = new()");
        sb.AppendLine("    {");
        foreach (var v in stateList) sb.AppendLine(F($"        [\"{v.Hash}\"] = (nint)(delegate*<in global::HIDMaestro.HMGamepadState, float*, byte*, uint*, void>)&{v.KernelName},"));
        sb.AppendLine("    };");
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
