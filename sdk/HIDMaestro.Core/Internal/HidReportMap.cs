using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

internal enum HidReportKind : byte { Input = 0, Output = 1, Feature = 2 }

/// <summary>A run of usages, page and ID together (<c>page &lt;&lt; 16 | id</c>).</summary>
internal readonly record struct HidUsageRange(uint Min, uint Max)
{
    public int Count => (int)(Max - Min) + 1;
}

/// <summary>One main item: <see cref="Count"/> elements of
/// <see cref="BitSize"/> bits at <see cref="BitOffset"/> of the report's
/// data (the report ID byte, when there is one, comes before bit 0).</summary>
internal sealed class HidField
{
    public HidReportKind Kind;
    public byte ReportId;
    public int BitOffset;
    public int BitSize;
    public int Count;
    public int LogicalMin;
    public int LogicalMax;
    public bool Constant;
    public bool Variable;
    public bool Relative;
    public int Collection = -1;
    public HidUsageRange[] Usages = Array.Empty<HidUsageRange>();

    public int UsageCount
    {
        get { int n = 0; foreach (var r in Usages) n += r.Count; return n; }
    }

    /// <summary>The usage of element <paramref name="i"/> of a variable
    /// item. Elements past the list take its last usage (HID 1.11,
    /// 6.2.2.8).</summary>
    public uint UsageAt(int i)
    {
        foreach (var r in Usages)
        {
            if (i < r.Count) return r.Min + (uint)i;
            i -= r.Count;
        }
        return Usages.Length == 0 ? 0 : Usages[^1].Max;
    }

    /// <summary>Position of <paramref name="usage"/> in the usage list, or
    /// -1. An array element holding <c>LogicalMin + index</c> selects
    /// it.</summary>
    public int IndexOf(uint usage)
    {
        int at = 0;
        foreach (var r in Usages)
        {
            if (usage >= r.Min && usage <= r.Max) return at + (int)(usage - r.Min);
            at += r.Count;
        }
        return -1;
    }

    public static ushort PageOf(uint usage) => (ushort)(usage >> 16);
    public static ushort IdOf(uint usage) => (ushort)usage;
}

internal sealed class HidCollection
{
    public int Index;
    public ushort UsagePage;
    public ushort Usage;
    public bool Is(ushort page, ushort usage) => UsagePage == page && Usage == usage;
}

internal sealed class HidReportLayout
{
    public HidReportKind Kind;
    public byte ReportId;
    public int Bits;
    public readonly List<HidField> Fields = new();
    public int DataBytes => (Bits + 7) / 8;
    /// <summary>Bytes on the wire: the report ID byte, when used, plus the data.</summary>
    public int FrameBytes => DataBytes + (ReportId != 0 ? 1 : 0);
}

/// <summary>Every report a HID report descriptor declares, field by field,
/// with the top-level collection each field belongs to (issue #67). The
/// keyboard and mouse packers write through it, so a profile's own
/// descriptor decides where each key, button and axis lands. Parsing
/// follows the HID 1.11 item rules (section 6.2.2): globals persist and
/// stack with Push/Pop, locals reset after every main item, a 4-byte usage
/// carries its own page, and each report ID counts its bits from zero.</summary>
internal sealed class HidReportMap
{
    public readonly List<HidCollection> Collections = new();
    public readonly List<HidReportLayout> Reports = new();
    public bool UsesReportIds;

    public HidReportLayout? Find(HidReportKind kind, byte reportId)
    {
        foreach (var r in Reports)
            if (r.Kind == kind && r.ReportId == reportId) return r;
        return null;
    }

    public HidCollection? FirstCollection(ushort page, ushort usage)
    {
        foreach (var c in Collections)
            if (c.Is(page, usage)) return c;
        return null;
    }

    /// <summary>The reports of one kind that hold fields of a collection.</summary>
    public IEnumerable<HidReportLayout> ReportsOf(HidCollection c, HidReportKind kind)
    {
        foreach (var r in Reports)
        {
            if (r.Kind != kind) continue;
            foreach (var f in r.Fields)
                if (f.Collection == c.Index) { yield return r; break; }
        }
    }

    private struct Globals
    {
        public ushort UsagePage;
        public int LogicalMin;
        public uint LogicalMaxRaw;
        public int LogicalMaxSize;
        public int ReportSize;
        public int ReportCount;
        public byte ReportId;
    }

    /// <summary>A Usage, or a Usage Minimum / Maximum pair, completed with
    /// the usage page in force when it was declared. An extended (4-byte)
    /// usage carries its own page.</summary>
    private struct LocalUsage
    {
        public uint Min, Max;
        public bool Extended;
    }

    public static HidReportMap Parse(byte[] d)
    {
        var map = new HidReportMap();
        var g = new Globals();
        var stack = new Stack<Globals>();
        var locals = new List<LocalUsage>();
        uint? usageMin = null;
        int usageMinSize = 0;
        // Delimiters open alternative usage sets. As in hid-core.c, only
        // usages outside any set and those of the first set count.
        int delimiterDepth = 0, delimiterBranch = 0;
        int depth = 0;
        int tlc = -1;

        int i = 0;
        while (i < d.Length)
        {
            byte prefix = d[i];
            if (prefix == 0xFE)
            {
                if (i + 1 >= d.Length) break;
                i += 3 + d[i + 1];
                continue;
            }
            int size = prefix & 0x03;
            if (size == 3) size = 4;
            if (i + 1 + size > d.Length) break;
            uint data = 0;
            for (int b = 0; b < size; b++) data |= (uint)d[i + 1 + b] << (8 * b);
            int sdata = size switch
            {
                1 => (sbyte)data,
                2 => (short)data,
                4 => (int)data,
                _ => 0,
            };
            int type = (prefix >> 2) & 0x03;
            int tag = prefix >> 4;
            i += 1 + size;

            switch (type)
            {
                case 0:   // Main
                    ConcatenateLastUsagePage(locals, g.UsagePage);
                    switch (tag)
                    {
                        case 0x8: AddField(map, HidReportKind.Input, data, g, locals, tlc); break;
                        case 0x9: AddField(map, HidReportKind.Output, data, g, locals, tlc); break;
                        case 0xB: AddField(map, HidReportKind.Feature, data, g, locals, tlc); break;
                        case 0xA:
                            if (depth == 0)
                            {
                                // The collection's usage is the first local
                                // one (hid-core.c open_collection).
                                uint u = locals.Count > 0 ? locals[0].Min : (uint)g.UsagePage << 16;
                                tlc = map.Collections.Count;
                                map.Collections.Add(new HidCollection
                                {
                                    Index = tlc,
                                    UsagePage = HidField.PageOf(u),
                                    Usage = HidField.IdOf(u),
                                });
                            }
                            depth++;
                            break;
                        case 0xC:
                            if (depth > 0) depth--;
                            break;
                    }
                    locals.Clear();
                    usageMin = null;
                    delimiterDepth = delimiterBranch = 0;
                    break;
                case 1:   // Global
                    switch (tag)
                    {
                        case 0x0: g.UsagePage = (ushort)data; break;
                        case 0x1: g.LogicalMin = sdata; break;
                        case 0x2: g.LogicalMaxRaw = data; g.LogicalMaxSize = size; break;
                        case 0x7: g.ReportSize = (int)data; break;
                        case 0x8: g.ReportId = (byte)data; map.UsesReportIds = true; break;
                        case 0x9: g.ReportCount = (int)data; break;
                        case 0xA: stack.Push(g); break;
                        case 0xB: if (stack.Count > 0) g = stack.Pop(); break;
                    }
                    break;
                case 2:   // Local
                    if (tag == 0xA)
                    {
                        if (data != 0) { delimiterDepth++; delimiterBranch++; }
                        else if (delimiterDepth > 0) delimiterDepth--;
                        break;
                    }
                    if (delimiterBranch > 1 && tag <= 0x2) break;
                    switch (tag)
                    {
                        case 0x0:
                            locals.Add(new LocalUsage
                            {
                                Min = Complete(data, size, g.UsagePage),
                                Max = Complete(data, size, g.UsagePage),
                                Extended = size == 4,
                            });
                            break;
                        case 0x1:
                            usageMin = data;
                            usageMinSize = size;
                            break;
                        case 0x2:
                            // hid-core.c adds every usage from the minimum to
                            // this maximum, completed with the page in force
                            // at the maximum.
                            if (usageMin is uint min)
                            {
                                uint hi = Complete(data, size, g.UsagePage);
                                uint lo = size == 4
                                    ? (usageMinSize == 4 ? min : (hi & 0xFFFF0000) | (min & 0xFFFF))
                                    : Complete(min, 2, g.UsagePage);
                                if (hi >= lo) locals.Add(new LocalUsage { Min = lo, Max = hi, Extended = size == 4 });
                            }
                            usageMin = null;
                            break;
                    }
                    break;
            }
        }
        return map;
    }

    private static uint Complete(uint data, int size, ushort page) =>
        size == 4 ? data : ((uint)page << 16) | (data & 0xFFFF);

    /// <summary>HID 1.11, 6.2.2.8: at a main item the last declared Usage
    /// Page completes the usages before it. As hid-core.c
    /// (hid_concatenate_last_usage_page) and hid-tools
    /// (_concatenate_usages) apply it: walk back from the last usage and
    /// re-complete each short one until one already carries that page, so
    /// a page declared after its usages reaches them, and usages declared
    /// under an earlier page keep it.</summary>
    private static void ConcatenateLastUsagePage(List<LocalUsage> locals, ushort page)
    {
        for (int i = locals.Count - 1; i >= 0; i--)
        {
            var u = locals[i];
            if (u.Extended) continue;
            if (HidField.PageOf(u.Max) == page) break;
            u.Min = ((uint)page << 16) | (u.Min & 0xFFFF);
            u.Max = ((uint)page << 16) | (u.Max & 0xFFFF);
            locals[i] = u;
        }
    }

    private static HidUsageRange[] ResolveUsages(List<LocalUsage> locals)
    {
        var list = new HidUsageRange[locals.Count];
        for (int i = 0; i < locals.Count; i++) list[i] = new HidUsageRange(locals[i].Min, locals[i].Max);
        return list;
    }

    private static void AddField(HidReportMap map, HidReportKind kind, uint flags, Globals g,
                                 List<LocalUsage> locals, int tlc)
    {
        var layout = map.Find(kind, g.ReportId);
        if (layout == null)
        {
            layout = new HidReportLayout { Kind = kind, ReportId = g.ReportId };
            map.Reports.Add(layout);
        }
        // Logical Maximum is signed on the wire, but a field whose minimum
        // is not negative reads its maximum unsigned (26 FF 00 is 255, and
        // so is 25 FF next to a minimum of 0, which hid-core and HIDP agree on).
        int logicalMax = g.LogicalMaxSize switch
        {
            1 => (sbyte)g.LogicalMaxRaw,
            2 => (short)g.LogicalMaxRaw,
            4 => (int)g.LogicalMaxRaw,
            _ => 0,
        };
        if (g.LogicalMin >= 0 && logicalMax < g.LogicalMin)
            logicalMax = (int)Math.Min(g.LogicalMaxRaw, int.MaxValue);

        var f = new HidField
        {
            Kind = kind,
            ReportId = g.ReportId,
            BitOffset = layout.Bits,
            BitSize = g.ReportSize,
            Count = g.ReportCount,
            LogicalMin = g.LogicalMin,
            LogicalMax = logicalMax,
            Constant = (flags & 0x01) != 0,
            Variable = (flags & 0x02) != 0,
            Relative = (flags & 0x04) != 0,
            Collection = tlc,
            Usages = ResolveUsages(locals),
        };
        layout.Fields.Add(f);
        layout.Bits += g.ReportSize * g.ReportCount;
    }

    // ── Bit access ───────────────────────────────────────────────────────

    /// <summary>Write element <paramref name="element"/> of a field into a
    /// frame that starts with the report ID byte when the report has one.</summary>
    public static void Write(byte[] frame, HidField f, int element, int value)
    {
        int bit = f.BitOffset + element * f.BitSize + (f.ReportId != 0 ? 8 : 0);
        uint v = (uint)value;
        for (int k = 0; k < f.BitSize; k++, bit++)
        {
            int byteIndex = bit >> 3;
            if (byteIndex >= frame.Length) return;
            byte mask = (byte)(1 << (bit & 7));
            if (((v >> k) & 1) != 0) frame[byteIndex] |= mask;
            else frame[byteIndex] &= (byte)~mask;
        }
    }

    /// <summary>Read element <paramref name="element"/> of a field from a
    /// report's DATA (no report ID byte), sign-extended when the field's
    /// logical minimum is negative.</summary>
    public static int ReadData(ReadOnlySpan<byte> data, HidField f, int element)
    {
        int bit = f.BitOffset + element * f.BitSize;
        uint v = 0;
        for (int k = 0; k < f.BitSize && k < 32; k++, bit++)
        {
            int byteIndex = bit >> 3;
            if (byteIndex >= data.Length) break;
            if ((data[byteIndex] & (1 << (bit & 7))) != 0) v |= 1u << k;
        }
        if (f.LogicalMin < 0 && f.BitSize < 32 && (v & (1u << (f.BitSize - 1))) != 0)
            v |= ~0u << f.BitSize;
        return (int)v;
    }
}
