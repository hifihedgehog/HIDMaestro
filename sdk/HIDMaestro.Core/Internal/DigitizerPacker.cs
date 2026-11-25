using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HIDMaestro.Internal;

/// <summary>One controller's touch and pen state for <see cref="DigitizerPacker"/>:
/// the contacts its last touch submit reported, the frames the packer hands
/// back, and the scan time's clock. Used under the controller's submit
/// lock.</summary>
internal sealed class DigitizerBuffers
{
    internal readonly List<byte[]> Frames = new();
    internal readonly List<byte[]> Spare = new();
    /// <summary>The contacts the last touch frame reported down or in
    /// range, by identifier.</summary>
    internal readonly Dictionary<int, HMTouchContact> Down = new();
    /// <summary>The buttons the last touch frame reported pressed.</summary>
    internal HMMouseButtons Buttons;
    internal readonly List<(HMTouchContact C, bool Lift)> Items = new();
    internal readonly HashSet<int> Seen = new();
    internal byte[]? Pen;
    internal (int X, int Y) PenAt;
    internal long ScanFrom;
    internal long LastScan = -1;
}

/// <summary>Packs <see cref="HMTouchContact"/> and <see cref="HMPenState"/>
/// into the input reports a profile's own descriptor declares, built once per
/// profile from <see cref="HidReportMap"/>, as
/// <see cref="KeyboardMousePacker"/> packs a mouse.
///
/// <para>Collections, by top-level usage (HID Usage Tables 1.5, section 16):
/// a touchscreen is Digitizer 0x04 and a touchpad Digitizer 0x05, each with a
/// Finger logical collection per contact a report carries. A pen is
/// Digitizer 0x02, or 0x01 for an external digitizer, with a Stylus
/// collection. The fields Windows requires of a touch report are Tip Switch,
/// Contact Identifier, X and Y in each finger and Contact Count in the
/// report. A pen needs Tip Switch, X and Y (Microsoft, touchscreen and pen
/// required HID top-level collections).</para>
///
/// <para>The parse keeps only top-level collections, so a touch report's
/// fields are split into fingers by their usages: a contact usage the current
/// finger already has from an earlier field starts the next finger. A field
/// that repeats one usage across its elements, as Microsoft's sample
/// touchscreen and the Dell XPS 13 9360's Elan screen do with X and Y, writes
/// the same value to each.</para></summary>
internal sealed class DigitizerPacker
{
    private const ushort PageGenericDesktop = 0x01, PageButton = 0x09, PageDigitizer = 0x0D;
    private const ushort UsageDigitizer = 0x01, UsagePen = 0x02, UsageTouchScreen = 0x04, UsageTouchPad = 0x05;
    private const ushort TipPressure = 0x30, InRange = 0x32, Invert = 0x3C, XTilt = 0x3D, YTilt = 0x3E,
        Azimuth = 0x3F, Twist = 0x41, TipSwitch = 0x42, BarrelSwitch = 0x44, Eraser = 0x45, Confidence = 0x47,
        Width = 0x48, Height = 0x49, ContactIdentifier = 0x51, ContactCount = 0x54, ScanTime = 0x56,
        SecondaryBarrel = 0x5A;

    private static uint U(ushort page, ushort id) => ((uint)page << 16) | id;
    private static readonly uint X = U(PageGenericDesktop, 0x30), Y = U(PageGenericDesktop, 0x31);

    /// <summary>The elements of one finger, or of a report's own fields, by
    /// usage. A usage can hold several elements.</summary>
    private sealed class Slot
    {
        public readonly Dictionary<uint, List<(HidField F, int E)>> At = new();
        private readonly Dictionary<uint, HidField> _lastField = new();

        public bool Has(uint u) => At.ContainsKey(u);

        /// <summary>True when <paramref name="u"/> is here from a field other
        /// than <paramref name="f"/>.</summary>
        public bool HasFromAnotherField(uint u, HidField f) => _lastField.TryGetValue(u, out var g) && !ReferenceEquals(g, f);

        public void Add(uint u, HidField f, int e)
        {
            if (!At.TryGetValue(u, out var list)) At[u] = list = new List<(HidField, int)>();
            list.Add((f, e));
            _lastField[u] = f;
        }

        public HidField? First(uint u) => At.TryGetValue(u, out var l) && l.Count > 0 ? l[0].F : null;

        /// <summary>Write <paramref name="value"/>, clamped to the field's
        /// range, to every element of <paramref name="u"/>.</summary>
        public void Put(byte[] frame, uint u, int value)
        {
            if (!At.TryGetValue(u, out var list)) return;
            foreach (var (f, e) in list) HidReportMap.Write(frame, f, e, Math.Clamp(value, f.LogicalMin, f.LogicalMax));
        }

        public int Get(ReadOnlySpan<byte> frame, uint u, bool withId)
        {
            if (!At.TryGetValue(u, out var list) || list.Count == 0) return 0;
            var (f, e) = list[0];
            return HidReportMap.ReadData(withId ? frame[1..] : frame, f, e);
        }
    }

    private static bool IsContactUsage(uint u) =>
        u == X || u == Y
        || (HidField.PageOf(u) == PageDigitizer && HidField.IdOf(u) is TipSwitch or InRange or Confidence or ContactIdentifier
                or Width or Height or TipPressure or Azimuth);

    public readonly HidReportLayout? TouchReport;
    public readonly HMTouchLayout? TouchLayout;
    private readonly List<Slot> _fingers = new();
    private readonly Slot _touchLevel = new();
    private readonly List<(int Button, HidField F, int E)> _touchButtons = new();

    public readonly HidReportLayout? PenReport;
    public readonly HMPenLayout? PenLayout;
    private readonly Slot _pen = new();

    public bool HasTouch => TouchReport != null;
    public bool HasPen => PenReport != null;

    // The Device Configuration collection's Device Mode (Digitizer 0x0E,
    // usage 0x52), which Windows writes with SET_FEATURE: 2 for a
    // touchscreen's multiple input, 3 for a touchpad's own reports and 0 for
    // its mouse collection (Microsoft, touchscreen and touchpad
    // configuration collections). Windows' inbox MTConfig driver holds the
    // collection, and a program's own write to it never reaches the device.
    private readonly (HidField F, int E)? _deviceMode;

    /// <summary>The Device Mode a host's feature write sets, or null when the
    /// write is not the configuration collection's.</summary>
    public int? DecodeDeviceMode(byte reportId, ReadOnlySpan<byte> data)
    {
        if (_deviceMode is not { } at || at.F.ReportId != reportId) return null;
        return HidReportMap.ReadData(data, at.F, at.E);
    }

    public DigitizerPacker(HidReportMap map)
    {
        var touch = map.FirstCollection(PageDigitizer, UsageTouchScreen) ?? map.FirstCollection(PageDigitizer, UsageTouchPad);
        if (touch != null)
        {
            foreach (var r in map.ReportsOf(touch, HidReportKind.Input))
            {
                var fingers = new List<Slot>();
                var level = new Slot();
                Slot? current = null;
                foreach (var f in r.Fields)
                {
                    if (f.Collection != touch.Index || f.Constant || !f.Variable) continue;
                    for (int e = 0; e < f.Count; e++)
                    {
                        uint u = f.UsageAt(e);
                        if (!IsContactUsage(u)) { level.Add(u, f, e); continue; }
                        if (current == null || current.HasFromAnotherField(u, f))
                        {
                            current = new Slot();
                            fingers.Add(current);
                        }
                        current.Add(u, f, e);
                    }
                }
                // Each finger carries what Windows requires of a contact.
                if (fingers.Count == 0 || !fingers.TrueForAll(s => s.Has(U(PageDigitizer, TipSwitch)) && s.Has(X) && s.Has(Y)
                                                                    && s.Has(U(PageDigitizer, ContactIdentifier))))
                    continue;
                TouchReport = r;
                _fingers = fingers;
                _touchLevel = level;
                foreach (var (u, list) in level.At)
                    if (HidField.PageOf(u) == PageButton && HidField.IdOf(u) is > 0 and <= 16)
                        foreach (var (f, e) in list) _touchButtons.Add((HidField.IdOf(u), f, e));
                break;
            }
            if (TouchReport != null)
            {
                var first = _fingers[0];
                HidField idField = first.First(U(PageDigitizer, ContactIdentifier))!;
                HidField? count = _touchLevel.First(U(PageDigitizer, ContactCount));
                // The Contact Count Maximum feature field bounds the answer
                // Windows reads as the most contacts (Microsoft, touchscreen
                // required collections). Its answer is the profile's.
                int most = count != null ? Math.Min(count.LogicalMax, idField.LogicalMax + 1) : _fingers.Count;
                foreach (var fr in map.ReportsOf(touch, HidReportKind.Feature))
                    foreach (var f in fr.Fields)
                        if (f.Collection == touch.Index && !f.Constant && f.IndexOf(U(PageDigitizer, 0x55)) >= 0)
                            most = Math.Min(most, f.LogicalMax);
                uint buttons = 0;
                foreach (var b in _touchButtons) buttons |= 1u << (b.Button - 1);
                TouchLayout = new HMTouchLayout
                {
                    IsTouchpad = touch.Usage == UsageTouchPad,
                    ContactsPerReport = _fingers.Count,
                    MaxContacts = count != null ? Math.Max(_fingers.Count, most) : _fingers.Count,
                    MaxContactId = idField.LogicalMax,
                    X = RangeOf(first.First(X))!.Value,
                    Y = RangeOf(first.First(Y))!.Value,
                    Width = RangeOf(first.First(U(PageDigitizer, Width))),
                    Height = RangeOf(first.First(U(PageDigitizer, Height))),
                    Pressure = RangeOf(first.First(U(PageDigitizer, TipPressure))),
                    Azimuth = RangeOf(first.First(U(PageDigitizer, Azimuth))),
                    Buttons = (HMMouseButtons)buttons,
                };
            }
        }

        var config = map.FirstCollection(PageDigitizer, 0x0E);
        if (config != null)
            foreach (var r in map.ReportsOf(config, HidReportKind.Feature))
                foreach (var f in r.Fields)
                {
                    if (f.Collection != config.Index || f.Constant || !f.Variable) continue;
                    for (int e = 0; e < f.Count && _deviceMode == null; e++)
                        if (f.UsageAt(e) == U(PageDigitizer, 0x52)) _deviceMode = (f, e);
                }

        var pen = map.FirstCollection(PageDigitizer, UsagePen) ?? map.FirstCollection(PageDigitizer, UsageDigitizer);
        if (pen != null)
        {
            foreach (var r in map.ReportsOf(pen, HidReportKind.Input))
            {
                var slot = new Slot();
                foreach (var f in r.Fields)
                {
                    if (f.Collection != pen.Index || f.Constant || !f.Variable) continue;
                    for (int e = 0; e < f.Count; e++)
                    {
                        uint u = f.UsageAt(e);
                        // The first stylus's fields. A second transducer's go
                        // with a report this one does not read.
                        if (!slot.HasFromAnotherField(u, f)) slot.Add(u, f, e);
                    }
                }
                // A report with a Contact Identifier is a touch report.
                if (!slot.Has(U(PageDigitizer, TipSwitch)) || !slot.Has(X) || !slot.Has(Y)
                    || slot.Has(U(PageDigitizer, ContactIdentifier))) continue;
                PenReport = r;
                _pen = slot;
                break;
            }
            if (PenReport != null)
            {
                HMPenButtons buttons = HMPenButtons.None;
                if (_pen.Has(U(PageDigitizer, BarrelSwitch))) buttons |= HMPenButtons.Barrel;
                if (_pen.Has(U(PageDigitizer, Eraser))) buttons |= HMPenButtons.Eraser;
                if (_pen.Has(U(PageDigitizer, Invert))) buttons |= HMPenButtons.Invert;
                if (_pen.Has(U(PageDigitizer, SecondaryBarrel))) buttons |= HMPenButtons.SecondaryBarrel;
                PenLayout = new HMPenLayout
                {
                    X = RangeOf(_pen.First(X))!.Value,
                    Y = RangeOf(_pen.First(Y))!.Value,
                    Pressure = RangeOf(_pen.First(U(PageDigitizer, TipPressure))),
                    TiltX = RangeOf(_pen.First(U(PageDigitizer, XTilt))),
                    TiltY = RangeOf(_pen.First(U(PageDigitizer, YTilt))),
                    Twist = RangeOf(_pen.First(U(PageDigitizer, Twist))),
                    Buttons = buttons,
                    HasInRange = _pen.Has(U(PageDigitizer, InRange)),
                };
            }
        }
    }

    private static HMRange? RangeOf(HidField? f) => f == null ? null : new HMRange(f.LogicalMin, f.LogicalMax);

    private static byte[] FrameAt(List<byte[]> pool, int i, HidReportLayout r)
    {
        while (pool.Count <= i) pool.Add(new byte[r.FrameBytes]);
        var frame = pool[i];
        if (frame.Length != r.FrameBytes) pool[i] = frame = new byte[r.FrameBytes];
        else Array.Clear(frame);
        if (r.ReportId != 0) frame[0] = r.ReportId;
        return frame;
    }

    // ── Touch ────────────────────────────────────────────────────────────

    /// <summary>The reports one touch submit becomes, in send order.
    /// <paramref name="contacts"/> is every contact down or in range now. One
    /// the last submit reported and this one does not is lifted where it was:
    /// Tip Switch and In Range off, once. When a submit holds more contacts
    /// than one report carries, its reports follow Windows' hybrid mode: the
    /// first carries the frame's whole Contact Count and the rest carry 0,
    /// all with one Scan Time. When the contacts and the lifts together pass
    /// <see cref="HMTouchLayout.MaxContacts"/>, the lifts go first in a frame
    /// of their own, with the contacts that stay where the last frame put
    /// them.</summary>
    public List<byte[]> PackTouch(ReadOnlySpan<HMTouchContact> contacts, HMMouseButtons buttons, DigitizerBuffers s)
    {
        if (TouchReport == null || TouchLayout == null)
            throw new InvalidOperationException("This profile's descriptor declares no touchscreen or touchpad collection (Digitizer, Touch Screen or Touch Pad) whose fingers carry Tip Switch, Contact Identifier, X and Y.");
        var layout = TouchLayout;
        if (contacts.Length > layout.MaxContacts)
            throw new ArgumentException($"{contacts.Length} contacts is more than this descriptor carries, {layout.MaxContacts}.", nameof(contacts));
        s.Seen.Clear();
        foreach (var c in contacts)
        {
            if ((uint)c.Id > (uint)layout.MaxContactId)
                throw new ArgumentOutOfRangeException(nameof(contacts), $"Contact {c.Id} is outside this descriptor's Contact Identifier range, 0 to {layout.MaxContactId}.");
            if (!s.Seen.Add(c.Id))
                throw new ArgumentException($"Contact {c.Id} appears twice in one submit.", nameof(contacts));
        }
        if (s.ScanFrom == 0) s.ScanFrom = Stopwatch.GetTimestamp();

        var frames = s.Frames;
        frames.Clear();
        int pooled = 0;
        var items = s.Items;
        items.Clear();
        int lifts = 0;
        foreach (var (id, _) in s.Down)
            if (!s.Seen.Contains(id)) lifts++;

        if (lifts > 0 && contacts.Length + lifts > layout.MaxContacts)
        {
            // The lifts first, beside the contacts that stay as they were.
            foreach (var (id, last) in s.Down) items.Add((last, !s.Seen.Contains(id)));
            EmitFrame(items, buttons, s, frames, ref pooled);
            items.Clear();
            lifts = 0;
        }
        foreach (var c in contacts) items.Add((c, false));
        if (lifts > 0)
            foreach (var (id, last) in s.Down)
                if (!s.Seen.Contains(id)) items.Add((last, true));
        EmitFrame(items, buttons, s, frames, ref pooled);

        s.Down.Clear();
        foreach (var c in contacts) s.Down[c.Id] = c;
        s.Buttons = buttons;
        return frames;
    }

    private void EmitFrame(List<(HMTouchContact C, bool Lift)> items, HMMouseButtons buttons, DigitizerBuffers s, List<byte[]> frames, ref int pooled)
    {
        var r = TouchReport!;
        int per = _fingers.Count;
        int reports = Math.Max(1, (items.Count + per - 1) / per);
        int scan = NextScan(s);
        for (int n = 0; n < reports; n++)
        {
            var frame = FrameAt(s.Spare, pooled++, r);
            for (int k = 0; k < per; k++)
            {
                int i = n * per + k;
                if (i >= items.Count) break;
                PutContact(frame, _fingers[k], items[i].C, items[i].Lift);
            }
            _touchLevel.Put(frame, U(PageDigitizer, ContactCount), n == 0 ? items.Count : 0);
            _touchLevel.Put(frame, U(PageDigitizer, ScanTime), scan);
            foreach (var (button, f, e) in _touchButtons)
                HidReportMap.Write(frame, f, e, (int)(((uint)buttons >> (button - 1)) & 1u));
            frames.Add(frame);
        }
    }

    private static void PutContact(byte[] frame, Slot finger, in HMTouchContact c, bool lift)
    {
        finger.Put(frame, U(PageDigitizer, TipSwitch), lift || c.Hover ? 0 : 1);
        finger.Put(frame, U(PageDigitizer, InRange), lift ? 0 : 1);
        finger.Put(frame, U(PageDigitizer, Confidence), c.Palm ? 0 : 1);
        finger.Put(frame, U(PageDigitizer, ContactIdentifier), c.Id);
        finger.Put(frame, X, c.X);
        finger.Put(frame, Y, c.Y);
        finger.Put(frame, U(PageDigitizer, Width), c.Width);
        finger.Put(frame, U(PageDigitizer, Height), c.Height);
        finger.Put(frame, U(PageDigitizer, TipPressure), lift || c.Hover ? 0 : c.Pressure);
        finger.Put(frame, U(PageDigitizer, Azimuth), c.Azimuth);
    }

    /// <summary>The next Scan Time, in the 100 µs units Windows reads it in,
    /// wrapped at the field's range, and always past the last one so no two
    /// frames share it.</summary>
    private int NextScan(DigitizerBuffers s)
    {
        HidField? f = _touchLevel.First(U(PageDigitizer, ScanTime));
        if (f == null) return 0;
        long modulus = (long)f.LogicalMax - f.LogicalMin + 1;
        if (modulus <= 1) return f.LogicalMin;
        long ticks = (Stopwatch.GetTimestamp() - s.ScanFrom) * 10_000 / Stopwatch.Frequency;
        if (s.LastScan >= 0 && ticks <= s.LastScan) ticks = s.LastScan + 1;
        s.LastScan = ticks;
        return (int)(f.LogicalMin + ticks % modulus);
    }

    // ── Pen ──────────────────────────────────────────────────────────────

    /// <summary>One pen report. Out of range, every switch is off and the
    /// pressure zero, at the last position in range.</summary>
    public byte[] PackPen(in HMPenState p, DigitizerBuffers s)
    {
        if (PenReport == null)
            throw new InvalidOperationException("This profile's descriptor declares no pen collection (Digitizer, Pen or Digitizer) carrying Tip Switch, X and Y.");
        var r = PenReport;
        if (s.Pen == null || s.Pen.Length != r.FrameBytes) s.Pen = new byte[r.FrameBytes];
        else Array.Clear(s.Pen);
        var frame = s.Pen;
        if (r.ReportId != 0) frame[0] = r.ReportId;
        bool inRange = p.InRange || p.Tip;
        if (inRange) s.PenAt = (p.X, p.Y);
        _pen.Put(frame, U(PageDigitizer, TipSwitch), inRange && p.Tip ? 1 : 0);
        _pen.Put(frame, U(PageDigitizer, InRange), inRange ? 1 : 0);
        _pen.Put(frame, U(PageDigitizer, BarrelSwitch), inRange && (p.Buttons & HMPenButtons.Barrel) != 0 ? 1 : 0);
        _pen.Put(frame, U(PageDigitizer, Eraser), inRange && (p.Buttons & HMPenButtons.Eraser) != 0 ? 1 : 0);
        _pen.Put(frame, U(PageDigitizer, Invert), inRange && (p.Buttons & HMPenButtons.Invert) != 0 ? 1 : 0);
        _pen.Put(frame, U(PageDigitizer, SecondaryBarrel), inRange && (p.Buttons & HMPenButtons.SecondaryBarrel) != 0 ? 1 : 0);
        _pen.Put(frame, X, s.PenAt.X);
        _pen.Put(frame, Y, s.PenAt.Y);
        _pen.Put(frame, U(PageDigitizer, TipPressure), inRange && p.Tip ? p.Pressure : 0);
        _pen.Put(frame, U(PageDigitizer, XTilt), inRange ? p.TiltX : 0);
        _pen.Put(frame, U(PageDigitizer, YTilt), inRange ? p.TiltY : 0);
        _pen.Put(frame, U(PageDigitizer, Twist), inRange ? p.Twist : 0);
        return frame;
    }

    // ── Release ──────────────────────────────────────────────────────────

    /// <summary>The frames that lift every contact the last touch submit left
    /// down or in range, each where it was, and let go of every button, in
    /// the hybrid form a frame of that many contacts takes: the first report
    /// with the whole count, the rest with 0, one Scan Time past the last.
    /// A touchpad button pressed with no contact down takes one report with
    /// none. Empty when nothing is down. A hybrid frame's last report
    /// carries Contact Count 0, so a release built from that report alone
    /// would lift nothing (<see cref="ReleaseOf"/>).</summary>
    public List<byte[]> LiftFrames(DigitizerBuffers s)
    {
        var frames = new List<byte[]>();
        if (TouchReport == null || (s.Down.Count == 0 && s.Buttons == HMMouseButtons.None)) return frames;
        var r = TouchReport;
        int per = _fingers.Count;
        var down = new List<HMTouchContact>(s.Down.Values);
        int scan = 0;
        if (_touchLevel.First(U(PageDigitizer, ScanTime)) is { } f)
        {
            long modulus = (long)f.LogicalMax - f.LogicalMin + 1;
            scan = modulus > 1 ? (int)(f.LogicalMin + (s.LastScan + 1) % modulus) : f.LogicalMin;
        }
        int reports = Math.Max(1, (down.Count + per - 1) / per);
        for (int n = 0; n < reports; n++)
        {
            var frame = new byte[r.FrameBytes];
            if (r.ReportId != 0) frame[0] = r.ReportId;
            for (int k = 0; k < per && n * per + k < down.Count; k++)
                PutContact(frame, _fingers[k], down[n * per + k], lift: true);
            _touchLevel.Put(frame, U(PageDigitizer, ContactCount), n == 0 ? down.Count : 0);
            _touchLevel.Put(frame, U(PageDigitizer, ScanTime), scan);
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>True when <paramref name="frame"/> is a report of the touch
    /// collection.</summary>
    public bool IsTouchFrame(ReadOnlySpan<byte> frame, bool withIds) =>
        TouchReport != null && frame.Length >= TouchReport.FrameBytes
        && (!withIds || frame[0] == TouchReport.ReportId);

    /// <summary>The report that lets go of what a touch or pen
    /// <paramref name="frame"/> holds, written to <paramref name="release"/>:
    /// each contact lifted where it was, with its identifier and the frame's
    /// Contact Count kept so Windows reads the lift of every contact the frame
    /// carried, and the Scan Time one past the frame's. A pen goes out of
    /// range where it was. The driver sends it if the consumer goes away with
    /// a contact down. -1 when the frame is neither report.</summary>
    public int ReleaseOf(ReadOnlySpan<byte> frame, byte[] release, HidReportLayout r)
    {
        if (frame.Length < r.FrameBytes) return -1;
        bool withId = r.ReportId != 0;
        if (ReferenceEquals(r, TouchReport))
        {
            frame[..r.FrameBytes].CopyTo(release);
            foreach (var finger in _fingers)
            {
                finger.Put(release, U(PageDigitizer, TipSwitch), 0);
                finger.Put(release, U(PageDigitizer, InRange), 0);
                finger.Put(release, U(PageDigitizer, TipPressure), 0);
            }
            if (_touchLevel.First(U(PageDigitizer, ScanTime)) is { } scan)
            {
                long modulus = (long)scan.LogicalMax - scan.LogicalMin + 1;
                int now = _touchLevel.Get(frame, U(PageDigitizer, ScanTime), withId);
                _touchLevel.Put(release, U(PageDigitizer, ScanTime), modulus > 1 ? (int)(scan.LogicalMin + (now - scan.LogicalMin + 1) % modulus) : now);
            }
            foreach (var (_, f, e) in _touchButtons) HidReportMap.Write(release, f, e, 0);
            return r.FrameBytes;
        }
        if (ReferenceEquals(r, PenReport))
        {
            frame[..r.FrameBytes].CopyTo(release);
            foreach (ushort id in new[] { TipSwitch, InRange, BarrelSwitch, Eraser, Invert, SecondaryBarrel, TipPressure })
                _pen.Put(release, U(PageDigitizer, id), 0);
            return r.FrameBytes;
        }
        return -1;
    }
}
