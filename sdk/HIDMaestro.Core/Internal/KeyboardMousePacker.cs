using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

/// <summary>One controller's working space for <see cref="KeyboardMousePacker"/>:
/// the frames the packer hands back and the usage lists it sorts. A packer
/// is shared by every controller of its profile, so each controller keeps
/// its own of these and uses it under its submit lock. Once each buffer has
/// grown to what the controller sends, a submit allocates nothing.</summary>
internal sealed class KeyboardMouseBuffers
{
    /// <summary>The frames of the last mouse submit, in send order.</summary>
    internal readonly List<byte[]> Frames = new();
    internal readonly List<byte[]> Motion = new();
    internal byte[]? Extra, Keyboard, Consumer, Release, RawFrame;
    internal uint[] Usages = new uint[16], Rest = new uint[16], Fits = new uint[16];
    internal bool[] Flags = new bool[16];

    /// <summary>Room for <paramref name="n"/> usages in every list.</summary>
    internal void Reserve(int n)
    {
        if (Usages.Length >= n) return;
        int size = Math.Max(n, Usages.Length * 2);
        Usages = new uint[size];
        Rest = new uint[size];
        Fits = new uint[size];
        Flags = new bool[size];
    }

    /// <summary>The motion frames kept between mouse submits. A movement
    /// split across more reports than this lets the rest go once they are
    /// sent, so one large movement does not hold its frames for the
    /// controller's life.</summary>
    internal const int MotionFramesKept = 16;

    /// <summary>Let go of the frames past <see cref="MotionFramesKept"/>
    /// after their submit has sent them. Costs a count check otherwise.</summary>
    internal void TrimMotion()
    {
        if (Motion.Count <= MotionFramesKept) return;
        Motion.RemoveRange(MotionFramesKept, Motion.Count - MotionFramesKept);
        Motion.TrimExcess();
        Frames.Clear();
        Frames.TrimExcess();
    }
}

/// <summary>Packs <see cref="HMMouseState"/>, keys and consumer usages into
/// the input reports a profile's own descriptor declares (issue #67). Built
/// once per profile from <see cref="HidReportMap"/>.
///
/// <para>Collections, by top-level usage: a mouse is Generic Desktop 0x02,
/// a keyboard 0x06, consumer controls Consumer 0x01 (HID Usage Tables 1.5,
/// sections 4 and 15). Windows hands the first two to mouhid and kbdhid
/// and every other collection to Raw Input as a generic HID device, so a
/// second collection is where a mouse puts buttons past the five
/// RAWMOUSE reports.</para></summary>
internal sealed class KeyboardMousePacker
{
    private const ushort PageGenericDesktop = 0x01;
    private const ushort PageKeyboard = 0x07;
    private const ushort PageLed = 0x08;
    private const ushort PageButton = 0x09;
    private const ushort PageConsumer = 0x0C;

    public readonly HidReportMap Map;

    // Mouse: the reports of the mouse collection that carry its parts, in
    // report ID order, and where each part lands. Most mice use one report.
    // Some split buttons from motion (the Xiaomi receiver sends buttons,
    // wheel and pan in report 1 and X and Y in report 2).
    private readonly List<HidReportLayout> _mouseReports = new();
    private readonly (HidField F, int E)? _x, _y, _wheel, _pan;
    // Button n (1-based, mouse numbering) -> element.
    private readonly Dictionary<int, (HidField F, int E)> _mouseButtons = new();
    private readonly int _mouseButtonMax;
    // Buttons past the mouse collection's own: a second, non-system
    // collection's Button usages, numbered on from _mouseButtonMax.
    private readonly HidReportLayout? _extraButtonReport;
    private readonly Dictionary<int, (HidField F, int E)> _extraButtons = new();

    private readonly HidReportLayout? _keyboardReport;
    private readonly HidReportLayout? _keyboardLedReport;
    private readonly HidReportLayout? _consumerReport;
    // The longest input report, the size of a release frame's buffer.
    private readonly int _maxInputFrameBytes;

    public bool HasMouse => _mouseReports.Count > 0;
    public bool HasKeyboard => _keyboardReport != null;
    public bool HasConsumer => _consumerReport != null;
    public bool HasKeyboardLeds => _keyboardLedReport != null;
    /// <summary>Highest button number SubmitMouse can reach.</summary>
    public int ButtonCount => _mouseButtonMax + _extraButtons.Count;

    public KeyboardMousePacker(byte[] descriptor)
    {
        Map = HidReportMap.Parse(descriptor);

        var mouse = Map.FirstCollection(PageGenericDesktop, 0x02);
        if (mouse != null)
        {
            foreach (var r in Map.ReportsOf(mouse, HidReportKind.Input))
            {
                bool used = false;
                foreach (var f in r.Fields)
                {
                    if (f.Collection != mouse.Index || f.Constant || !f.Variable) continue;
                    for (int e = 0; e < f.Count; e++)
                    {
                        uint u = f.UsageAt(e);
                        ushort page = HidField.PageOf(u), id = HidField.IdOf(u);
                        if (page == PageButton && id > 0) used |= _mouseButtons.TryAdd(id, (f, e));
                        else if (page == PageGenericDesktop && id == 0x30 && _x == null) { _x = (f, e); used = true; }
                        else if (page == PageGenericDesktop && id == 0x31 && _y == null) { _y = (f, e); used = true; }
                        else if (page == PageGenericDesktop && id == 0x38 && _wheel == null) { _wheel = (f, e); used = true; }
                        else if (page == PageConsumer && id == 0x238 && _pan == null) { _pan = (f, e); used = true; }
                    }
                }
                if (used) _mouseReports.Add(r);
            }
            _mouseReports.Sort((a, b) => a.ReportId.CompareTo(b.ReportId));
            foreach (var n in _mouseButtons.Keys) _mouseButtonMax = Math.Max(_mouseButtonMax, n);

            // A second collection that only a Raw Input reader opens: not a
            // mouse, keyboard, consumer or system control collection.
            foreach (var c in Map.Collections)
            {
                if (c.Index == mouse.Index || IsSystemCollection(c)) continue;
                foreach (var r in Map.ReportsOf(c, HidReportKind.Input))
                {
                    foreach (var f in r.Fields)
                    {
                        if (f.Collection != c.Index || f.Constant || !f.Variable) continue;
                        for (int e = 0; e < f.Count; e++)
                        {
                            uint u = f.UsageAt(e);
                            if (HidField.PageOf(u) != PageButton || HidField.IdOf(u) == 0) continue;
                            _extraButtons.TryAdd(_mouseButtonMax + HidField.IdOf(u), (f, e));
                            _extraButtonReport ??= r;
                        }
                    }
                    if (_extraButtonReport != null) break;
                }
                if (_extraButtonReport != null) break;
            }
        }

        var kb = Map.FirstCollection(PageGenericDesktop, 0x06);
        if (kb != null)
        {
            foreach (var r in Map.ReportsOf(kb, HidReportKind.Input))
                if (HasPage(r, kb, PageKeyboard)) { _keyboardReport = r; break; }
            foreach (var r in Map.ReportsOf(kb, HidReportKind.Output))
                if (HasPage(r, kb, PageLed)) { _keyboardLedReport = r; break; }
        }

        var cc = Map.FirstCollection(PageConsumer, 0x01);
        if (cc != null)
            foreach (var r in Map.ReportsOf(cc, HidReportKind.Input))
                if (HasPage(r, cc, PageConsumer)) { _consumerReport = r; break; }

        foreach (var r in Map.Reports)
            if (r.Kind == HidReportKind.Input) _maxInputFrameBytes = Math.Max(_maxInputFrameBytes, r.FrameBytes);
    }

    private static bool IsSystemCollection(HidCollection c) =>
        (c.UsagePage == PageGenericDesktop && (c.Usage == 0x02 || c.Usage == 0x06 || c.Usage == 0x80))
        || (c.UsagePage == PageConsumer && c.Usage == 0x01);

    private static bool HasPage(HidReportLayout r, HidCollection c, ushort page)
    {
        foreach (var f in r.Fields)
        {
            if (f.Collection != c.Index || f.Constant) continue;
            foreach (var u in f.Usages)
                if (HidField.PageOf(u.Min) == page) return true;
        }
        return false;
    }

    /// <summary>A cleared frame of report <paramref name="r"/> with its ID,
    /// in <paramref name="buffer"/>, which is made the report's size the
    /// first time.</summary>
    private static byte[] Frame(ref byte[]? buffer, HidReportLayout r)
    {
        if (buffer == null || buffer.Length != r.FrameBytes) buffer = new byte[r.FrameBytes];
        else Array.Clear(buffer);
        if (r.ReportId != 0) buffer[0] = r.ReportId;
        return buffer;
    }

    /// <summary>The same for the frame at <paramref name="i"/> of a list
    /// whose positions each keep one report.</summary>
    private static byte[] FrameAt(List<byte[]> frames, int i, HidReportLayout r)
    {
        if (i == frames.Count) frames.Add(new byte[r.FrameBytes]);
        byte[]? frame = frames[i];
        frame = Frame(ref frame, r);
        frames[i] = frame;
        return frame;
    }

    /// <summary>Add <paramref name="u"/> to the first <paramref name="n"/>
    /// usages unless it is there. Returns the new count.</summary>
    private static int AddOnce(uint[] usages, int n, uint u)
    {
        for (int i = 0; i < n; i++)
            if (usages[i] == u) return n;
        usages[n] = u;
        return n + 1;
    }

    // ── Mouse ────────────────────────────────────────────────────────────

    /// <summary>The frames one <see cref="HMMouseState"/> becomes, in send
    /// order. A relative movement past a field's range is split across as
    /// many reports as it takes, so none of it is lost. Buttons and an
    /// absolute position repeat in each. The extra-button report follows
    /// when it changed.</summary>
    public List<byte[]> PackMouse(in HMMouseState s, ref uint lastExtraButtons, ref bool extraSent, KeyboardMouseBuffers b)
    {
        if (_mouseReports.Count == 0)
            throw new InvalidOperationException("This profile's descriptor declares no mouse collection (Generic Desktop, Mouse).");
        var frames = b.Frames;
        frames.Clear();
        int next = 0;
        // A part the descriptor does not declare owes nothing, or the loop
        // below would wait forever for a field to carry it.
        int rx = _x != null ? s.X : 0, ry = _y != null ? s.Y : 0;
        int rw = _wheel != null ? s.Wheel : 0, rp = _pan != null ? s.Pan : 0;
        do
        {
            foreach (var r in _mouseReports)
            {
                var frame = FrameAt(b.Motion, next++, r);
                foreach (var (n, at) in _mouseButtons)
                    if (n <= 32 && at.F.ReportId == r.ReportId)
                        HidReportMap.Write(frame, at.F, at.E, (int)(((uint)s.Buttons >> (n - 1)) & 1u));
                if (In(r, _x)) rx = Put(frame, _x, rx);
                if (In(r, _y)) ry = Put(frame, _y, ry);
                if (In(r, _wheel)) rw = Put(frame, _wheel, rw);
                if (In(r, _pan)) rp = Put(frame, _pan, rp);
                frames.Add(frame);
            }
        }
        while (rx != 0 || ry != 0 || rw != 0 || rp != 0);

        if (_extraButtonReport != null)
        {
            uint extra = 0;
            foreach (var (n, _) in _extraButtons)
                if (n <= 32 && (((uint)s.Buttons >> (n - 1)) & 1u) != 0) extra |= 1u << (n - 1);
            if (!extraSent || extra != lastExtraButtons)
            {
                var frame = Frame(ref b.Extra, _extraButtonReport);
                foreach (var (n, at) in _extraButtons)
                    if (n <= 32) HidReportMap.Write(frame, at.F, at.E, (int)((extra >> (n - 1)) & 1u));
                frames.Add(frame);
                lastExtraButtons = extra;
                extraSent = true;
            }
        }
        return frames;
    }

    private static bool In(HidReportLayout r, (HidField F, int E)? at) =>
        at is { } a && a.F.ReportId == r.ReportId;

    /// <summary>Write one pointer value, and return what is still owed: the
    /// part of a relative movement past the field's range, or zero. A
    /// relative field writes 0, no motion, for a movement it cannot carry at
    /// all, one of a sign its range lacks or none where its range excludes
    /// zero, and owes nothing for it. A part that overshoots owes nothing
    /// back, so every split ends. corsair-k70-rgb-mk2-low-profile declares
    /// its relative X and Y as 1 to 32767, as the real keyboard does:
    /// clamping a click's X of 0 up to 1 owed -1, then -2, and the split
    /// never ended.</summary>
    private static int Put(byte[] frame, (HidField F, int E)? at, int value)
    {
        if (at is not { } a) return 0;
        var f = a.F;
        int v = Math.Clamp(value, f.LogicalMin, f.LogicalMax);
        if (f.Relative && Math.Sign(v) != Math.Sign(value)) v = 0;
        HidReportMap.Write(frame, f, a.E, v);
        if (!f.Relative || v == 0) return 0;
        int rest = value - v;
        return Math.Sign(rest) == Math.Sign(value) ? rest : 0;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────

    /// <summary>One keyboard report. Modifiers and any key the descriptor
    /// lists as a bit go in as bits, and the rest fill the key array. More keys
    /// than the array holds report ErrorRollOver in every slot, as the HID
    /// 1.11 boot keyboard does (Appendix C).</summary>
    public byte[] PackKeyboard(HMKeyModifiers modifiers, ReadOnlySpan<HMKey> keys, KeyboardMouseBuffers b)
    {
        if (_keyboardReport == null)
            throw new InvalidOperationException("This profile's descriptor declares no keyboard collection (Generic Desktop, Keyboard).");
        var frame = Frame(ref b.Keyboard, _keyboardReport);
        b.Reserve(8 + keys.Length);
        // Every usage held, each once.
        uint[] pressed = b.Usages;
        int n = 0;
        for (int m = 0; m < 8; m++)
            if (((int)modifiers & (1 << m)) != 0) n = AddOnce(pressed, n, ((uint)PageKeyboard << 16) | (uint)(0xE0 + m));
        foreach (var k in keys)
            if (k != HMKey.None) n = AddOnce(pressed, n, ((uint)PageKeyboard << 16) | (byte)k);

        // A usage the report lists as a bit goes on its bit.
        bool[] asBit = b.Flags;
        Array.Clear(asBit, 0, n);
        foreach (var f in _keyboardReport.Fields)
        {
            if (f.Constant || !f.Variable) continue;
            for (int e = 0; e < f.Count; e++)
            {
                uint u = f.UsageAt(e);
                if (HidField.PageOf(u) != PageKeyboard) continue;
                int at = Array.IndexOf(pressed, u, 0, n);
                HidReportMap.Write(frame, f, e, at >= 0 ? 1 : 0);
                if (at >= 0) asBit[at] = true;
            }
        }

        // The rest fill the key arrays in usage order.
        uint[] rest = b.Rest;
        int restCount = 0;
        for (int i = 0; i < n; i++)
            if (!asBit[i]) rest[restCount++] = pressed[i];
        Array.Sort(rest, 0, restCount);

        uint[] fits = b.Fits;
        foreach (var f in _keyboardReport.Fields)
        {
            if (f.Constant || f.Variable) continue;
            if (f.Usages.Length == 0 || HidField.PageOf(f.Usages[0].Min) != PageKeyboard) continue;
            // The usages this array can name leave the rest, in order.
            int fitCount = 0, kept = 0;
            for (int i = 0; i < restCount; i++)
            {
                if (f.IndexOf(rest[i]) >= 0) fits[fitCount++] = rest[i];
                else rest[kept++] = rest[i];
            }
            restCount = kept;
            int empty = EmptyArrayValue(f);
            if (fitCount > f.Count)
            {
                int roll = f.IndexOf(((uint)PageKeyboard << 16) | 0x01);
                for (int e = 0; e < f.Count; e++)
                    HidReportMap.Write(frame, f, e, roll >= 0 ? f.LogicalMin + roll : empty);
            }
            else
            {
                for (int e = 0; e < f.Count; e++)
                    HidReportMap.Write(frame, f, e, e < fitCount ? f.LogicalMin + f.IndexOf(fits[e]) : empty);
            }
        }
        return frame;
    }

    /// <summary>What an unused array slot holds. A slot whose value is in
    /// the logical range selects a usage (hid-core.c
    /// hid_array_value_is_valid), so it is usage 0 of the field's page (no
    /// event) when the list has it, and otherwise a value outside the
    /// range.</summary>
    private static int EmptyArrayValue(HidField f)
    {
        if (f.Usages.Length > 0)
        {
            int zero = f.IndexOf((uint)HidField.PageOf(f.Usages[0].Min) << 16);
            if (zero >= 0) return f.LogicalMin + zero;
        }
        if (f.LogicalMin > 0 || f.LogicalMax < 0) return 0;
        long top = f.BitSize >= 31 ? int.MaxValue : (1L << f.BitSize) - 1;
        return f.LogicalMax < top ? f.LogicalMax + 1 : 0;
    }

    // ── Consumer controls ────────────────────────────────────────────────

    /// <summary>One consumer-control report: each usage on a bit sets the
    /// bit, the rest fill the usage array.</summary>
    public byte[] PackConsumer(ReadOnlySpan<ushort> usages, KeyboardMouseBuffers b)
    {
        if (_consumerReport == null)
            throw new InvalidOperationException("This profile's descriptor declares no consumer control collection (Consumer, Consumer Control).");
        var frame = Frame(ref b.Consumer, _consumerReport);
        b.Reserve(usages.Length);
        // The usages held, in the order given, repeats kept.
        uint[] pressed = b.Usages;
        int n = 0;
        foreach (var u in usages)
            if (u != 0) pressed[n++] = ((uint)PageConsumer << 16) | u;

        // A usage the report lists as a bit goes on its bit, and every
        // repeat of it with it.
        bool[] onBit = b.Flags;
        Array.Clear(onBit, 0, n);
        foreach (var f in _consumerReport.Fields)
        {
            if (f.Constant || !f.Variable) continue;
            for (int e = 0; e < f.Count; e++)
            {
                uint u = f.UsageAt(e);
                bool on = false;
                for (int i = 0; i < n; i++)
                    if (pressed[i] == u) { on = true; onBit[i] = true; }
                HidReportMap.Write(frame, f, e, on ? 1 : 0);
            }
        }

        uint[] rest = b.Rest;
        int restCount = 0;
        for (int i = 0; i < n; i++)
            if (!onBit[i]) rest[restCount++] = pressed[i];

        uint[] fits = b.Fits;
        foreach (var f in _consumerReport.Fields)
        {
            if (f.Constant || f.Variable) continue;
            int fitCount = 0, kept = 0;
            for (int i = 0; i < restCount; i++)
            {
                if (f.IndexOf(rest[i]) >= 0) fits[fitCount++] = rest[i];
                else rest[kept++] = rest[i];
            }
            restCount = kept;
            int empty = EmptyArrayValue(f);
            for (int e = 0; e < f.Count; e++)
                HidReportMap.Write(frame, f, e, e < fitCount ? f.LogicalMin + f.IndexOf(fits[e]) : empty);
        }
        return frame;
    }

    // ── Release ──────────────────────────────────────────────────────────

    /// <summary>The descriptor's first input report, 0 without IDs: the
    /// report a data-only frame (SubmitRawReport) goes out under unless the
    /// profile names a dataReportId
    /// (ControllerProfile.KeyboardMouseDataReportId). The driver prefixes
    /// the same one.</summary>
    public byte FirstInputReportId
    {
        get
        {
            foreach (var r in Map.Reports)
                if (r.Kind == HidReportKind.Input) return r.ReportId;
            return 0;
        }
    }

    /// <summary>The report that lets go of everything <paramref name="frame"/>
    /// holds (issue #67): buttons and keys up, relative motion and the wheel
    /// zero, array slots empty, a hat switch centered, and absolute Generic
    /// Desktop axes (X through Dial) where they were, so a pointer does not
    /// jump. The driver sends it if the consumer goes away with input held.
    /// The frame is written to the start of <c>b.Release</c>.</summary>
    /// <returns>Its length, or -1 when <paramref name="frame"/> is not one
    /// of the descriptor's input reports.</returns>
    public int ReleaseOf(ReadOnlySpan<byte> frame, KeyboardMouseBuffers b)
    {
        byte id = Map.UsesReportIds && frame.Length > 0 ? frame[0] : (byte)0;
        var r = Map.Find(HidReportKind.Input, id);
        if (r == null || frame.Length < r.FrameBytes) return -1;
        var release = b.Release ??= new byte[_maxInputFrameBytes];
        frame[..r.FrameBytes].CopyTo(release);
        foreach (var f in r.Fields)
        {
            if (f.Constant) continue;
            if (!f.Variable)
            {
                int empty = EmptyArrayValue(f);
                for (int e = 0; e < f.Count; e++) HidReportMap.Write(release, f, e, empty);
                continue;
            }
            for (int e = 0; e < f.Count; e++)
            {
                uint u = f.UsageAt(e);
                bool desktop = HidField.PageOf(u) == PageGenericDesktop;
                ushort usage = HidField.IdOf(u);
                if (!f.Relative && desktop && usage >= 0x30 && usage <= 0x37) continue;
                // A hat's center is its null state, a value outside its range.
                HidReportMap.Write(release, f, e, desktop && usage == 0x39 ? EmptyArrayValue(f) : 0);
            }
        }
        return r.FrameBytes;
    }

    // ── Keyboard LEDs ────────────────────────────────────────────────────

    /// <summary>The LED state in an output report the host sent, or null
    /// when the report is not the keyboard's LED report. Both backends
    /// split the report the device receives at its first byte (see
    /// <see cref="HMOutputPacket.ReportId"/>): that byte is the report ID
    /// when the descriptor declares IDs, and the report's own first byte
    /// when it declares none, so the LED fields then start one byte
    /// earlier, in <paramref name="reportId"/>.</summary>
    public HMKeyboardLeds? DecodeLeds(byte reportId, ReadOnlySpan<byte> data)
    {
        var r = _keyboardLedReport;
        if (r == null) return null;
        scoped ReadOnlySpan<byte> fields = data;
        if (r.ReportId != 0)
        {
            if (reportId != r.ReportId) return null;
        }
        else
        {
            Span<byte> whole = data.Length < 256 ? stackalloc byte[1 + data.Length] : new byte[1 + data.Length];
            whole[0] = reportId;
            data.CopyTo(whole[1..]);
            fields = whole;
        }
        int leds = 0;
        foreach (var f in r.Fields)
        {
            if (f.Constant || !f.Variable) continue;
            for (int e = 0; e < f.Count; e++)
            {
                uint u = f.UsageAt(e);
                ushort id = HidField.IdOf(u);
                if (HidField.PageOf(u) != PageLed || id < 1 || id > 8) continue;
                if (HidReportMap.ReadData(fields, f, e) != 0) leds |= 1 << (id - 1);
            }
        }
        return (HMKeyboardLeds)leds;
    }
}
