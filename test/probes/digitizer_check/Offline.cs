// offline: the touch and pen packer over real descriptors, no device. The
// descriptors are the kernel's HID test devices (hid-tools tests_kernel,
// data\kernel-digitizers.json) and the spike's two. For each: what the packer
// finds, a two-contact frame read back, a lift, a hybrid frame where a
// submit holds more contacts than one report carries, the release frame,
// and for a pen a report in range, out of range and released. The reports
// are read back by a reader of this probe's own that walks the report's
// fields in order and starts a finger at each Tip Switch, apart from the
// packer's grouping.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int s_checks, s_fails;

    static void Check(string what, bool ok, string detail = "")
    {
        s_checks++;
        if (!ok) s_fails++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // What each descriptor is: a touchscreen, a touchpad, a pen, or a
    // touchscreen with a pen.
    static readonly Dictionary<string, string> Kinds = new()
    {
        ["spike-touchscreen"] = "screen", ["spike-pen"] = "pen",
        ["TestElanXPS9360"] = "screen", ["Testilitek_222a_001c"] = "screen", ["Testn_trig_1b96_0c01"] = "screen+pen",
        ["Testegalax_capacitive_0eef_790a"] = "screen", ["Test3m_0596_051c"] = "screen", ["Testatmel_03eb_840b"] = "screen+pen",
        ["TestTouchpadXPS9360"] = "pad", ["TestSurfaceBook2"] = "pad", ["Testite_06cb_2968"] = "pad",
        ["Testsynaptics_06cb_ce08"] = "pad", ["Testelan_04f3_313a"] = "pad", ["Testdell_06cb_75db"] = "pad",
        ["TestXPPen_ArtistPro16Gen2_28bd_095b"] = "pen", ["TestXPPen_Artist24_28bd_093a"] = "pen",
        ["TestHuion_Kamvas_Pro_19_256c_006b"] = "screen+pen", ["TestWacom_2d1f_014b"] = "pen",
    };

    /// <summary>catalog: every shipped profile with a touch or pen
    /// collection, which now delivers every report and lifts its contacts
    /// when its program dies, and the delivery it had before.</summary>
    static int Catalog()
    {
        Console.WriteLine("=== Shipped profiles with a touch or pen collection ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        int seen = 0, found = 0;
        foreach (var profile in ctx.AllProfiles)
        {
            var inner = profile.Inner;
            if (!inner.HasDescriptor) continue;
            seen++;
            var kbm = inner.GetOrBuildKeyboardMousePacker();
            if (!kbm.HasTouch && !kbm.HasPen) continue;
            found++;
            bool before = inner.IsKeyboardOrMouse || (string.Equals(inner.Type, "other", StringComparison.OrdinalIgnoreCase) && (kbm.HasMouse || kbm.HasKeyboard));
            Console.WriteLine($"  {profile.Id}: type {inner.Type}, touch {kbm.HasTouch}, pen {kbm.HasPen}, every report delivered before {before}, now {inner.DeliversEveryReport(kbm)}");
        }
        Console.WriteLine($"  {found} of {seen} profiles with a descriptor carry a touch or pen collection");
        return 0;
    }

    static int Offline()
    {
        Console.WriteLine("=== The touch and pen packer over real descriptors ===");
        var list = new List<(string Name, byte[] Bytes)>
        {
            ("spike-touchscreen", Convert.FromHexString(TouchDescriptor)),
            ("spike-pen", Convert.FromHexString(PenDescriptor)),
        };
        string data = Path.Combine(AppContext.BaseDirectory, "data", "kernel-digitizers.json");
        using (var doc = JsonDocument.Parse(File.ReadAllText(data)))
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add((e.GetProperty("name").GetString()!, Convert.FromHexString(e.GetProperty("descriptor").GetString()!)));
        foreach (var (name, bytes) in list) OneOffline(name, bytes);
        PickChecks();
        Console.WriteLine();
        Console.WriteLine($"=== {s_checks - s_fails}/{s_checks} checks passed ===");
        return s_fails == 0 ? 0 : 1;
    }

    static void OneOffline(string name, byte[] bytes)
    {
        var map = HidReportMap.Parse(bytes);
        var d = new KeyboardMousePacker(map).Digitizer;
        string kind = Kinds.TryGetValue(name, out var k) ? k : "?";
        Console.WriteLine($"-- {name} ({bytes.Length} bytes, {kind}) --");
        bool wantTouch = kind.Contains("screen") || kind == "pad", wantPen = kind.Contains("pen");
        Check($"{name}: touch collection found {wantTouch}, pen found {wantPen}", d.HasTouch == wantTouch && d.HasPen == wantPen,
              $"touch {d.HasTouch}, pen {d.HasPen}");
        if (d.HasTouch)
        {
            var t = d.TouchLayout!;
            Console.WriteLine($"  touch: {(t.IsTouchpad ? "touchpad" : "touchscreen")}, {t.ContactsPerReport} per report, at most {t.MaxContacts}, ids to {t.MaxContactId}, " +
                              $"X {t.X.Minimum}..{t.X.Maximum}, Y {t.Y.Minimum}..{t.Y.Maximum}, buttons {t.Buttons}, report 0x{d.TouchReport!.ReportId:X2} {d.TouchReport.FrameBytes} bytes");
            Check($"{name}: a {(kind == "pad" ? "touchpad" : "touchscreen")}", t.IsTouchpad == (kind == "pad"));
            Touch(name, d, bytes);
        }
        if (d.HasPen)
        {
            var p = d.PenLayout!;
            Console.WriteLine($"  pen: X {p.X.Minimum}..{p.X.Maximum}, Y {p.Y.Minimum}..{p.Y.Maximum}, pressure {Show(p.Pressure)}, tilt {Show(p.TiltX)}/{Show(p.TiltY)}, " +
                              $"buttons {p.Buttons}, in range {p.HasInRange}, report 0x{d.PenReport!.ReportId:X2} {d.PenReport.FrameBytes} bytes");
            Pen(name, d);
        }
    }

    static string Show(HMRange? r) => r is { } v ? $"{v.Minimum}..{v.Maximum}" : "none";

    // ── The probe's own reader ───────────────────────────────────────────

    const uint TipSwitchU = 0x000D0042, InRangeU = 0x000D0032, ConfidenceU = 0x000D0047, ContactIdU = 0x000D0051,
        ContactCountU = 0x000D0054, ScanTimeU = 0x000D0056, PressureU = 0x000D0030, XU = 0x00010030, YU = 0x00010031;

    record Finger(int Tip, int InRange, int Confidence, int Id, int X, int Y);

    /// <summary>One input main item of a descriptor, read by this probe's
    /// own walk of its items: its report, bit offset, size and count, its
    /// usages, its logical minimum, and the Finger logical collection
    /// (Digitizer 0x22) it sits in, counted in descriptor order, or -1.</summary>
    record Item(byte ReportId, int Bit, int Size, int Count, uint[] Usages, int LogicalMin, bool Constant, int Finger);

    static readonly Dictionary<byte[], List<Item>> s_walks = new(ReferenceEqualityComparer.Instance);

    /// <summary>The input items of <paramref name="d"/>, walked apart from
    /// HidReportMap: globals with Push and Pop, locals reset at each main
    /// item, and the collection nesting kept, so a field knows its finger.</summary>
    static List<Item> Walk(byte[] d)
    {
        if (s_walks.TryGetValue(d, out var known)) return known;
        var items = new List<Item>();
        ushort page = 0; int logMin = 0, size = 0, count = 0; byte id = 0;
        var stack = new Stack<(ushort, int, int, int, byte)>();
        var usages = new List<uint>();
        uint? usageMin = null;
        var collections = new Stack<int>();   // finger ordinal or -1, per open collection
        int fingers = 0;
        var bits = new Dictionary<byte, int>();
        for (int i = 0; i < d.Length;)
        {
            byte prefix = d[i];
            int n = prefix & 3; if (n == 3) n = 4;
            uint data = 0;
            for (int k = 0; k < n && i + 1 + k < d.Length; k++) data |= (uint)d[i + 1 + k] << (8 * k);
            int sdata = n == 1 ? (sbyte)data : n == 2 ? (short)data : (int)data;
            int type = (prefix >> 2) & 3, tag = prefix >> 4;
            i += 1 + n;
            uint Full(uint u) => n == 4 ? u : ((uint)page << 16) | (u & 0xFFFF);
            if (type == 0)
            {
                if (tag == 0xA)
                {
                    uint u = usages.Count > 0 ? usages[0] : 0;
                    bool finger = data == 2 && u == 0x000D0022;
                    collections.Push(finger ? fingers++ : (collections.Count > 0 ? collections.Peek() : -1));
                }
                else if (tag == 0xC) { if (collections.Count > 0) collections.Pop(); }
                else if (tag == 0x8)
                {
                    bits.TryGetValue(id, out int at);
                    items.Add(new Item(id, at, size, count, usages.ToArray(), logMin, (data & 1) != 0, collections.Count > 0 ? collections.Peek() : -1));
                    bits[id] = at + size * count;
                }
                usages.Clear(); usageMin = null;
            }
            else if (type == 1)
            {
                switch (tag)
                {
                    case 0x0: page = (ushort)data; break;
                    case 0x1: logMin = sdata; break;
                    case 0x7: size = (int)data; break;
                    case 0x8: id = (byte)data; break;
                    case 0x9: count = (int)data; break;
                    case 0xA: stack.Push((page, logMin, size, count, id)); break;
                    case 0xB: if (stack.Count > 0) (page, logMin, size, count, id) = stack.Pop(); break;
                }
            }
            else if (type == 2)
            {
                if (tag == 0x0) usages.Add(Full(data));
                else if (tag == 0x1) usageMin = Full(data);
                else if (tag == 0x2 && usageMin is uint lo) { for (uint u = lo; u <= Full(data); u++) usages.Add(u); usageMin = null; }
            }
        }
        s_walks[d] = items;
        return items;
    }

    static int ReadBits(ReadOnlySpan<byte> data, int bit, int size, bool signed)
    {
        uint v = 0;
        for (int k = 0; k < size && k < 32; k++, bit++)
            if ((bit >> 3) < data.Length && (data[bit >> 3] & (1 << (bit & 7))) != 0) v |= 1u << k;
        if (signed && size < 32 && (v & (1u << (size - 1))) != 0) v |= ~0u << size;
        return (int)v;
    }

    /// <summary>A touch frame as fingers, each from its own Finger
    /// collection, with the frame's Contact Count and Scan Time.</summary>
    static (List<Finger> Fingers, int Count, int Scan) Read(byte[] frame, byte[] descriptor)
    {
        byte rid = Walk(descriptor).Any(it => it.ReportId != 0) ? frame[0] : (byte)0;
        var data = rid != 0 ? frame.AsSpan(1) : frame.AsSpan();
        var byFinger = new SortedDictionary<int, Dictionary<uint, int>>();
        int count = -1, scan = -1;
        foreach (var it in Walk(descriptor))
        {
            if (it.ReportId != rid || it.Constant || it.Usages.Length == 0) continue;
            for (int e = 0; e < it.Count; e++)
            {
                uint u = it.Usages[Math.Min(e, it.Usages.Length - 1)];
                int v = ReadBits(data, it.Bit + e * it.Size, it.Size, it.LogicalMin < 0);
                if (it.Finger < 0)
                {
                    if (u == ContactCountU) count = v;
                    else if (u == ScanTimeU) scan = v;
                    continue;
                }
                if (!byFinger.TryGetValue(it.Finger, out var f)) byFinger[it.Finger] = f = new Dictionary<uint, int>();
                f.TryAdd(u, v);
            }
        }
        var fingers = byFinger.Values.Select(f => new Finger(
            f.GetValueOrDefault(TipSwitchU, -1), f.GetValueOrDefault(InRangeU, -1), f.GetValueOrDefault(ConfidenceU, -1),
            f.GetValueOrDefault(ContactIdU, -1), f.GetValueOrDefault(XU, -1), f.GetValueOrDefault(YU, -1))).ToList();
        return (fingers, count, scan);
    }

    static int At(HMRange r, int quarter) => r.Minimum + (int)((long)(r.Maximum - r.Minimum) * quarter / 4);

    static void Touch(string name, DigitizerPacker d, byte[] desc)
    {
        var t = d.TouchLayout!;
        var r = d.TouchReport!;
        var s = new DigitizerBuffers();
        int idA = Math.Min(1, t.MaxContactId), idB = Math.Min(2, t.MaxContactId);
        if (idA == idB) idA = 0;
        var a = new HMTouchContact { Id = idA, X = At(t.X, 1), Y = At(t.Y, 1) };
        var b = new HMTouchContact { Id = idB, X = At(t.X, 3), Y = At(t.Y, 3) };

        var frames = d.PackTouch(new[] { a, b }, HMMouseButtons.None, s).Select(f => f.ToArray()).ToList();
        int want = (2 + t.ContactsPerReport - 1) / t.ContactsPerReport;
        var read = frames.Select(f => Read(f, desc)).ToList();
        var all = read.SelectMany(x => x.Fingers.Take(t.ContactsPerReport)).ToList();
        var fa = all.FirstOrDefault(f => f.Id == idA && f.Tip == 1);
        var fb = all.FirstOrDefault(f => f.Id == idB && f.Tip == 1);
        Check($"{name}: two contacts in {want} report(s), each down at its place with its id, Contact Count 2",
              frames.Count == want && read[0].Count == 2 && fa is { } p && p.X == a.X && p.Y == a.Y && fb is { } q && q.X == b.X && q.Y == b.Y,
              string.Join(" | ", read.Select(x => $"count {x.Count} scan {x.Scan}: " + string.Join(", ", x.Fingers.Take(t.ContactsPerReport).Select(f => $"id {f.Id} tip {f.Tip} ({f.X},{f.Y})")))));

        frames = d.PackTouch(new[] { a }, HMMouseButtons.None, s).Select(f => f.ToArray()).ToList();
        read = frames.Select(f => Read(f, desc)).ToList();
        all = read.SelectMany(x => x.Fingers.Take(t.ContactsPerReport)).ToList();
        var stay = all.FirstOrDefault(f => f.Id == idA);
        var gone = all.FirstOrDefault(f => f.Id == idB);
        Check($"{name}: a contact left out of the next submit is lifted where it was, the other stays",
              read[0].Count == 2 && stay is { Tip: 1 } && gone is { Tip: 0 } g && g.X == b.X && g.Y == b.Y && (g.InRange is -1 or 0),
              string.Join(" | ", read.Select(x => $"count {x.Count}: " + string.Join(", ", x.Fingers.Take(t.ContactsPerReport).Select(f => $"id {f.Id} tip {f.Tip} range {f.InRange}")))));

        var release = new byte[r.FrameBytes];
        int n = d.ReleaseOf(frames[^1], release, r);
        var rel = Read(release, desc);
        Check($"{name}: its release frame lifts every contact the frame carried, ids and count kept",
              n == r.FrameBytes && rel.Count == read[^1].Count && rel.Fingers.Take(t.ContactsPerReport).All(f => f.Tip == 0)
              && rel.Fingers.Take(Math.Min(rel.Count, t.ContactsPerReport)).Select(f => f.Id).SequenceEqual(read[^1].Fingers.Take(Math.Min(read[^1].Count, t.ContactsPerReport)).Select(f => f.Id)),
              $"count {rel.Count}, " + string.Join(", ", rel.Fingers.Take(t.ContactsPerReport).Select(f => $"id {f.Id} tip {f.Tip}")));

        // Lift everything, then a frame of more contacts than one report
        // carries where the descriptor counts that high.
        d.PackTouch(ReadOnlySpan<HMTouchContact>.Empty, HMMouseButtons.None, s);
        int many = t.ContactsPerReport + 1;
        if (t.MaxContacts >= many && t.MaxContactId + 1 >= many)
        {
            var contacts = Enumerable.Range(0, many).Select(i => new HMTouchContact { Id = i, X = At(t.X, 2), Y = At(t.Y, 2) }).ToArray();
            d.PackTouch(ReadOnlySpan<HMTouchContact>.Empty, HMMouseButtons.None, s);
            frames = d.PackTouch(contacts, HMMouseButtons.None, s).Select(f => f.ToArray()).ToList();
            read = frames.Select(f => Read(f, desc)).ToList();
            Check($"{name}: {many} contacts go as a hybrid frame of 2 reports, Contact Count {many} then 0, one Scan Time",
                  frames.Count == 2 && read[0].Count == many && read[1].Count == 0 && read[0].Scan == read[1].Scan,
                  string.Join(" | ", read.Select(x => $"count {x.Count} scan {x.Scan}")));
        }
        else Console.WriteLine($"  no hybrid frame: at most {t.MaxContacts} contacts, {t.ContactsPerReport} per report");

        // The identifier range is enforced.
        bool refused = false;
        try { d.PackTouch(new[] { new HMTouchContact { Id = t.MaxContactId + 1 } }, HMMouseButtons.None, s); }
        catch (ArgumentOutOfRangeException) { refused = true; }
        Check($"{name}: an identifier past {t.MaxContactId} is refused", refused);
    }

    static void Pen(string name, DigitizerPacker d)
    {
        var p = d.PenLayout!;
        var r = d.PenReport!;
        var s = new DigitizerBuffers();
        var data = (byte[] f) => r.ReportId != 0 ? f.AsSpan(1).ToArray() : f;
        int Value(byte[] frame, uint usage)
        {
            foreach (var f in r.Fields)
            {
                if (f.Constant || !f.Variable) continue;
                for (int e = 0; e < f.Count; e++)
                    if (f.UsageAt(e) == usage) return HidReportMap.ReadData(data(frame), f, e);
            }
            return -1;
        }
        int pressure = p.Pressure is { } pr ? At(pr, 2) : 0;
        var down = new HMPenState { InRange = true, Tip = true, X = At(p.X, 1), Y = At(p.Y, 3), Pressure = pressure };
        var frame = d.PackPen(down, s).ToArray();
        Check($"{name}: a pen down reads tip 1, in range, its place and pressure",
              Value(frame, TipSwitchU) == 1 && (!p.HasInRange || Value(frame, InRangeU) == 1) && Value(frame, XU) == down.X && Value(frame, YU) == down.Y
              && (p.Pressure == null || Value(frame, PressureU) == pressure),
              $"tip {Value(frame, TipSwitchU)} range {Value(frame, InRangeU)} ({Value(frame, XU)},{Value(frame, YU)}) pressure {Value(frame, PressureU)}");
        var away = d.PackPen(new HMPenState { InRange = false, X = 1, Y = 1 }, s).ToArray();
        Check($"{name}: out of range it reads every switch off at its last place",
              Value(away, TipSwitchU) == 0 && (!p.HasInRange || Value(away, InRangeU) == 0) && Value(away, XU) == down.X && Value(away, YU) == down.Y
              && (p.Pressure is not { } low || Value(away, PressureU) == Math.Max(0, low.Minimum)),
              $"tip {Value(away, TipSwitchU)} range {Value(away, InRangeU)} ({Value(away, XU)},{Value(away, YU)})");
        var release = new byte[r.FrameBytes];
        int n = d.ReleaseOf(frame, release, r);
        Check($"{name}: the release of a pen down takes it out of range where it was",
              n == r.FrameBytes && Value(release, TipSwitchU) == 0 && (!p.HasInRange || Value(release, InRangeU) == 0) && Value(release, XU) == down.X);
    }
}
