// signal-stress's delivery paths besides the DualShock 4's ReadFile and the
// Xbox 360 pad's Windows.Gaming.Input readings (SignalStress.cs). Each pass
// is the same schedule, the same paired comparison and the same check on
// the submits with no further submit for 4 ms, on one more path:
//   x360-xinput   xbox-360-wired through XInputGetState, polled. xinput1_4
//                 asks the companion for the state (IOCTL_XUSB_GET_STATE),
//                 which reads the input section as the call comes in
//                 (driver\companion.c, BuildXusbStateForGetState), so no
//                 wakeup stands between a frame and its reading: the pass
//                 covers the GIP bytes the SDK writes and the companion's
//                 state built from them.
//   x360-hid      xbox-360-wired's HID collection, read with ReadFile. The
//                 profile has no idle repeat, and the driver's worker reads
//                 the section when it is signaled or a frame was handed to
//                 it (driver.c, the worker's wait), so a frame that misses
//                 both at a pause waits for the program's next submit, 600 ms
//                 or more on.
//   dualsense     dualsense on USB, ReadFile. The SDK repeats the last frame
//                 4 ms after it (the profile's stream), so a frame that
//                 misses its wakeup waits 4 ms.
//   dualsense-bt  dualsense-bt, ReadFile, a stream of 1.25 ms: a frame that
//                 misses its wakeup waits 1.25 ms, under the 2 ms bound on
//                 the final submits, which therefore cannot see it there.
//                 Once a host reads feature 0x05, 0x09 or 0x20 the device
//                 sends the vendor report 0x31, read at the bytes the
//                 profile's extended report gives its sticks.
//   generic       logitech-f310, the F310 in DirectInput mode, ReadFile: a
//                 plain HID gamepad descriptor with no report programs, no
//                 extended report and no idle repeat, the case of most
//                 catalog profiles. Nothing but the next submit recovers a
//                 frame whose wakeup is lost there.
//   usbip         dualsense-composite over USB/IP, its HID interface read
//                 with ReadFile. Its frames go from the SDK's USB/IP device
//                 to the host's requests with no HIDMaestro driver and no
//                 signal section on the way (HMContext,
//                 CreateUsbipController), so its pass has no gate modes and
//                 no pairs.
// Each carries the count in the two sticks' X axes the profile lays out, as
// the Windows.Gaming.Input pass does, the left stick's step plus the right
// stick's times the steps an axis has. An axis has 200 steps, or fewer where
// a step would span less than 3.5 of its values: 65 on an 8-bit axis. A
// reading counts only when both axes land within a third of a step of a
// step's value, more than the one value either way that the SDK's rounding,
// its centered mapping or an older SDK's truncation moves a value
// (StressDecodeSelfTest). After the first count, a report off every step
// fails the pass, and so does a count above every count submitted by then,
// so a decode that drifts fails loudly.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

internal static partial class Program
{
    /// <summary>The passes --passes= names, in the order a run takes them,
    /// and the profile each creates. A run without --passes= takes the first
    /// two, the battery's.</summary>
    static readonly (string Name, string Profile)[] StressPasses =
    {
        ("ds4", Ds4), ("x360-wgi", Xbox360), ("x360-xinput", Xbox360), ("x360-hid", Xbox360),
        ("dualsense", "dualsense"), ("dualsense-bt", "dualsense-bt"), ("generic", "logitech-f310"), ("usbip", "dualsense-composite"),
    };

    /// <summary>The SDK's idle repeat of a controller in ms, one period
    /// after the last frame (StreamClock): the profile's stream, or its
    /// extended report's idle frame interval. 0 when it has neither.</summary>
    static double IdleRepeatMs(HMController c) =>
        c.Profile.Inner.Stream?.PeriodMs ?? (c.Profile.Inner.ExtendedReport?.IdleFrameIntervalMs ?? 0);

    /// <summary>A count in two axes: the steps of each axis, the axis value
    /// of a step, and the step an axis value lands on, or -1 between
    /// steps.</summary>
    sealed class AxisCode
    {
        /// <summary>The fewest values of an axis a step spans: a third of
        /// that, the tolerance of <see cref="StepOf"/>, is more than the one
        /// value either way the SDK's encodings move a value.</summary>
        const double MinStepValues = 3.5;

        public readonly int Steps;
        public readonly double Step;
        public int Lap => Steps * Steps;

        public AxisCode(int steps)
        {
            Steps = steps;
            Step = 0.9 / steps;
        }

        /// <summary>The code for an axis of <paramref name="span"/> values
        /// past its minimum: 200 steps, or fewer where a step would span less
        /// than <see cref="MinStepValues"/>. Null below 32 steps, whose lap of
        /// 1,024 counts a burst or two can pass between readings.</summary>
        public static AxisCode? ForSpan(long span)
        {
            int steps = (int)Math.Min(200, Math.Floor(0.9 * span / MinStepValues));
            return steps >= 32 ? new AxisCode(steps) : null;
        }

        public float Value(int step) => (float)(0.05 + step * Step);

        /// <summary>The step of a value normalized to 0..1, or -1 when it is
        /// off every step by more than a third of one.</summary>
        public int StepOf(double normalized)
        {
            double s = (normalized - 0.05) / Step;
            int r = (int)Math.Round(s);
            return r >= 0 && r < Steps && Math.Abs(s - r) <= 1.0 / 3 ? r : -1;
        }
    }

    /// <summary>Counts as a reader sees them: codes that go up a lap at a
    /// time, unwrapped into the counts the probe submitted. The first
    /// reading counts once it carries a count already submitted. A reading
    /// whose count comes out above the highest submitted is either a frame
    /// older than the last one read, whose code a lap back reads as a lap
    /// ahead, when that older count is within <see cref="OlderWindow"/> of
    /// the last, or else a misread. One reader thread only.</summary>
    sealed class CountTracker
    {
        public const int OlderWindow = 256;
        readonly int _lap;
        int _last = -1, _prev, _unwrapped, _lastBad = -1;
        public int Misread, MisreadValue, Older, OlderValue, OlderAfter;

        public CountTracker(int lap) => _lap = lap;

        public bool Accept(int code, int highest, out int count)
        {
            count = 0;
            if (code == _last || (_last < 0 && code > highest)) return false;
            int delta = ((code - _prev) % _lap + _lap) % _lap;
            int c = _last >= 0 ? _unwrapped + delta : code;
            if (c > highest)
            {
                int older = c - _lap;
                if (code != _lastBad)
                {
                    if (older >= 0 && _unwrapped - older <= OlderWindow)
                    {
                        if (Older++ == 0) { OlderValue = older; OlderAfter = _unwrapped; }
                    }
                    else if (Misread++ == 0) MisreadValue = c;
                }
                _lastBad = code;
                return false;
            }
            _unwrapped = c;
            _prev = code;
            _last = code;
            count = c;
            return true;
        }

        public int Last => _last < 0 ? -1 : _unwrapped;
    }

    /// <summary>Where a HID report carries the two sticks' X axes: by HidP
    /// in a report the descriptor declares them in, or at the bytes of the
    /// profile's extended report for a vendor report the descriptor leaves
    /// opaque (dualsense-bt's 0x31).</summary>
    sealed class StickReport : IDisposable
    {
        readonly IntPtr _pp;
        readonly (ushort Page, ushort Usage, ushort Link, byte Report, long Min, long Max, int Bits)[] _axes;
        readonly byte _vendorId;
        readonly int _vendorLeft = -1, _vendorRight = -1;
        public readonly AxisCode Code;
        public readonly string Describe;

        StickReport(IntPtr pp, (ushort, ushort, ushort, byte, long, long, int)[] axes, byte vendorId, int vendorLeft, int vendorRight, AxisCode code, string describe)
        {
            _pp = pp; _axes = axes; _vendorId = vendorId; _vendorLeft = vendorLeft; _vendorRight = vendorRight; Code = code; Describe = describe;
        }

        /// <summary>The decode for the controller's two X axes on this
        /// interface, or null with the reason.</summary>
        public static StickReport? For(HMController c, HidDevice dev, out string why)
        {
            var sticks = c.Profile.Sticks;
            if (sticks.Count < 2) { why = $"{sticks.Count} sticks"; return null; }
            var want = new[] { sticks[0].XAxis, sticks[1].XAxis };
            if (!HidD_GetPreparsedData(dev.Handle, out IntPtr pp)) { why = $"HidD_GetPreparsedData error {Marshal.GetLastWin32Error()}"; return null; }
            var caps = new byte[72 * 128];
            ushort n = 128;
            if (HidP_GetValueCaps(HidP_Input, caps, ref n, pp) != HIDP_STATUS_SUCCESS) { HidD_FreePreparsedData(pp); why = "HidP_GetValueCaps failed"; return null; }
            var axes = new (ushort Page, ushort Usage, ushort Link, byte Report, long Min, long Max, int Bits)[2];
            for (int a = 0; a < 2; a++)
            {
                ushort page = (ushort)((int)want[a] >> 8), usage = (ushort)((int)want[a] & 0xFF);
                bool found = false;
                for (int i = 0; i < n && !found; i++)
                {
                    int o = i * 72;
                    if (BitConverter.ToUInt16(caps, o) != page) continue;
                    bool range = caps[o + 12] != 0;
                    ushort u0 = BitConverter.ToUInt16(caps, o + 56), u1 = range ? BitConverter.ToUInt16(caps, o + 58) : u0;
                    if (usage < u0 || usage > u1) continue;
                    int bits = BitConverter.ToUInt16(caps, o + 18);
                    long min = BitConverter.ToInt32(caps, o + 40), max = BitConverter.ToInt32(caps, o + 44);
                    // A 16-bit maximum written in a 2-byte item, 0xFFFF, reads
                    // as -1: the field is unsigned, its range the bit size's.
                    if (max <= min) { min = 0; max = (1L << bits) - 1; }
                    axes[a] = (page, usage, BitConverter.ToUInt16(caps, o + 6), caps[o + 2], min, max, bits);
                    found = true;
                }
                if (!found) { HidD_FreePreparsedData(pp); why = $"no input value for usage page 0x{page:X2} usage 0x{usage:X2}"; return null; }
            }
            // The vendor report's stick bytes, from the profile's extended report.
            byte vendorId = 0;
            int vl = -1, vr = -1;
            var ext = c.Profile.Inner.ExtendedReport;
            if (ext != null && ext.ReportIdByte != 0 && ext.ReportIdByte != axes[0].Report)
            {
                foreach (var f in ext.Fields)
                {
                    if (f.Semantic == "leftStickX") vl = f.Byte ?? -1;
                    if (f.Semantic == "rightStickX") vr = f.Byte ?? -1;
                }
                if (vl > 0 && vr > 0) vendorId = ext.ReportIdByte;
            }
            long span = Math.Min(axes[0].Max - axes[0].Min, axes[1].Max - axes[1].Min);
            if (vendorId != 0) span = Math.Min(span, 255);
            var code = AxisCode.ForSpan(span);
            if (code == null) { HidD_FreePreparsedData(pp); why = $"axes of {span + 1} values, too few for a count"; return null; }
            string describe = $"{want[0]} and {want[1]} in report 0x{axes[0].Report:X2} ({axes[0].Bits} and {axes[1].Bits} bits), {code.Steps} steps an axis"
                + (vendorId != 0 ? $", and bytes {vl} and {vr} of vendor report 0x{vendorId:X2}" : "");
            why = "";
            return new StickReport(pp, axes, vendorId, vl, vr, code, describe);
        }

        /// <summary>The code a report carries, or -1.</summary>
        public int CodeOf(byte[] report, int length)
        {
            if (length < 1) return -1;
            int left, right;
            if (_vendorId != 0 && report[0] == _vendorId)
            {
                if (length <= Math.Max(_vendorLeft, _vendorRight)) return -1;
                left = Code.StepOf(report[_vendorLeft] / 255.0);
                right = Code.StepOf(report[_vendorRight] / 255.0);
            }
            else
            {
                left = StepOfAxis(0, report, length);
                right = StepOfAxis(1, report, length);
            }
            return left < 0 || right < 0 ? -1 : right * Code.Steps + left;
        }

        int StepOfAxis(int a, byte[] report, int length)
        {
            var x = _axes[a];
            if (x.Report != 0 && report[0] != x.Report) return -1;
            if (HidP_GetUsageValue(HidP_Input, x.Page, x.Link, x.Usage, out uint raw, _pp, report, (uint)length) != HIDP_STATUS_SUCCESS) return -1;
            return Code.StepOf(Normalized(raw, x.Min, x.Max, x.Bits));
        }

        public void Dispose() => HidD_FreePreparsedData(_pp);
    }

    /// <summary>A raw HidP value as 0..1 of its field's logical range. A
    /// signed field comes back in its bit size, two's complement.</summary>
    static double Normalized(uint raw, long min, long max, int bits)
    {
        long v = raw;
        if (min < 0 && bits < 32 && (v & (1L << (bits - 1))) != 0) v -= 1L << bits;
        return (double)(v - min) / (max - min);
    }

    const int HidP_Input = 0;

    [DllImport("hid.dll")]
    static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value,
        IntPtr preparsed, byte[] report, uint reportLength);

    /// <summary>A reading the tracker refused after the first count, an
    /// older frame or a misread: its time, the code it carried, the highest
    /// count submitted then, the last count read before it and that
    /// reading's time, and both reports' bytes.</summary>
    readonly record struct Misread(bool Older, long Time, int Code, int Highest, int Last, long LastTime, string Bytes, string LastBytes);

    /// <summary>The submit at or before a Stopwatch time, for its mode and
    /// block.</summary>
    static (int Count, long Time, int Mode, int Block) SubmitAt(List<(int Count, long Time, int Mode, int Block)> sent, long time)
    {
        int lo = 0, hi = sent.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sent[mid].Time <= time) lo = mid + 1; else hi = mid;
        }
        return sent[Math.Max(0, lo - 1)];
    }

    /// <summary>Each older frame and misread kept, at most 8: when it came
    /// and in which mode, both reports' bytes, and the newest count submitted
    /// that carries its code, with how long before the reading it was
    /// submitted. A frame older than the reading before it shows as a count a
    /// few submits back, and a report that mixes two frames' axes as a code
    /// no recent count carries.</summary>
    static void MisreadDetail(string name, List<(int Count, long Time, int Mode, int Block)> sent, List<Misread> misreads, AxisCode code)
    {
        if (sent.Count == 0) return;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        foreach (var m in misreads)
        {
            var at = SubmitAt(sent, m.Time);
            // The newest count up to the highest submitted with the code.
            int carrier = m.Highest - ((m.Highest - m.Code) % code.Lap + code.Lap) % code.Lap;
            string carried = carrier >= 1 && carrier <= sent.Count
                ? FormattableString.Invariant($"count {carrier}'s, submitted {m.Highest - carrier} submits and {Ms(m.Time - sent[carrier - 1].Time):F2} ms before the reading")
                : carrier == 0 ? "count 0's, the frame before the first submit" : "no submitted count's";
            Console.WriteLine(FormattableString.Invariant(
                $"  {name}, {(m.Older ? "older frame" : "misread")} at {Ms(m.Time - sent[0].Time):F2} ms into the pass, {ShortMode(at.Mode)} block {at.Block}: code {m.Code} (left step {m.Code % code.Steps}, right step {m.Code / code.Steps}), {m.Highest} submitted, the last count read {m.Last}, {Ms(m.Time - m.LastTime):F2} ms before. The code is {carried}. Report {m.Bytes}, the reading before {m.LastBytes}"));
        }
    }

    /// <summary>The decode check of a pass: no count above the counts
    /// submitted, no frame older than the one read before it, and no reading
    /// off every step once the first count was read.</summary>
    static void DecodeCheck(string name, CountTracker tracker, int counts, int before, int off, string? firstOff)
    {
        Check($"{name}: every reading carries a count the probe submitted, in the sticks' X axes, none older than the reading before it",
            tracker.Misread == 0 && tracker.Older == 0 && off == 0,
            $"{counts} counts read, {before} readings before the first count" +
            (off > 0 ? $", {off} after it off every step, the first {firstOff}" : "") +
            (tracker.Older > 0 ? $", {tracker.Older} older than the reading before, the first count {tracker.OlderValue} after {tracker.OlderAfter}" : "") +
            (tracker.Misread > 0 ? $", {tracker.Misread} with a count above every count submitted, the first {tracker.MisreadValue}" : ""));
    }

    /// <summary>One HID-path pass (header): the count in the sticks, read
    /// with ReadFile on the controller's state interface.</summary>
    static void StressHidSticks(HMController c, string name, double seconds, IntPtr signal, bool gateOff, bool lateDetail,
        uint inputBuffers, int missEvery, int missInputEvery, bool companion)
    {
        using var dev = OpenStateInterface(c, out string why);
        if (dev == null) { Check($"{name}: a HID interface with input reports", false, why); return; }
        using var sticks = StickReport.For(c, dev, out why);
        if (sticks == null) { Check($"{name}: the sticks' X axes in its input reports", false, why); return; }
        if (inputBuffers > 0) HidD_SetNumInputBuffers(dev.Handle, inputBuffers);
        double period = IdleRepeatMs(c);
        Console.WriteLine($"  {name}, SubmitState to ReadFile, the count in {sticks.Describe}, " +
            (period > 0 ? FormattableString.Invariant($"an idle repeat {period} ms after the last frame:") : "no idle repeat:"));
        var code = sticks.Code;
        var tracker = new CountTracker(code.Lap);
        var seen = new List<(int, long)>(1 << 20);
        var reads = lateDetail ? new List<(int, long)>(1 << 20) : null;
        var burstOf = lateDetail ? new List<(int Size, int After)>(1 << 20) : null;
        int highest = 0, before = 0, off = 0;
        string? firstOff = null;
        // The first misreads, each with its report's bytes and the reading
        // before it (MisreadDetail).
        var misreads = new List<Misread>();
        bool stop = false;
        var failures = new ReaderFailures();
        HidDevice current = dev;
        var reopened = new List<HidDevice>();
        var reader = new Thread(() =>
        {
            var buf = new byte[dev.InputLength];
            var lastBuf = new byte[buf.Length];
            int lastRead = 0;
            long lastAt = 0;
            int inARow = 0;
            while (!Volatile.Read(ref stop))
            {
                if (!ReadFile(Volatile.Read(ref current).Handle, buf, buf.Length, out int read, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    long at = Stopwatch.GetTimestamp();
                    if (Volatile.Read(ref stop)) break;
                    failures.Add(error, at, tracker.Last);
                    if (++inARow > 20) { failures.Stop(error, at, tracker.Last, "20 failed reads in a row"); return; }
                    if (error == ERROR_OPERATION_ABORTED) continue;
                    HidDevice? again;
                    string lost;
                    try { again = OpenStateInterface(c, out lost); }
                    catch (Exception ex) { again = null; lost = ex.GetType().Name + ": " + ex.Message; }
                    if (again == null) { failures.Stop(error, at, tracker.Last, "no interface to reopen: " + lost); return; }
                    if (inputBuffers > 0) HidD_SetNumInputBuffers(again.Handle, inputBuffers);
                    lock (reopened) reopened.Add(again);
                    Volatile.Write(ref current, again);
                    continue;
                }
                inARow = 0;
                long t = Stopwatch.GetTimestamp();
                int c0 = sticks.CodeOf(buf, read);
                if (c0 < 0)
                {
                    // Before the first count the pad shows its rest, between
                    // steps. After it every report carries a count.
                    if (tracker.Last < 0) before++;
                    else if (off++ == 0) firstOff = "bytes " + Convert.ToHexString(buf, 0, Math.Min(read, 16));
                    continue;
                }
                int h = Volatile.Read(ref highest), misread = tracker.Misread, older = tracker.Older;
                if (!tracker.Accept(c0, h, out int count))
                {
                    if ((tracker.Misread != misread || tracker.Older != older) && misreads.Count < 8)
                        lock (seen) misreads.Add(new Misread(tracker.Older != older, t, c0, h, tracker.Last, lastAt,
                            Convert.ToHexString(buf, 0, read), Convert.ToHexString(lastBuf, 0, lastRead)));
                    if (tracker.Last < 0) before++;
                    else if (reads != null && c0 == tracker.Last % code.Lap) lock (seen) reads.Add((tracker.Last, t));
                    continue;
                }
                Buffer.BlockCopy(buf, 0, lastBuf, 0, read);
                lastRead = read;
                lastAt = t;
                lock (seen)
                {
                    reads?.Add((count, t));
                    seen.Add((count, t));
                }
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "stress hid sticks" };
        var state = new HMGamepadState { Axes = new Dictionary<HMAxis, float>() };
        var leftX = c.Profile.Sticks[0].XAxis;
        var rightX = c.Profile.Sticks[1].XAxis;
        state.Axes[leftX] = code.Value(0);
        state.Axes[rightX] = code.Value(0);
        c.SubmitState(state);
        Thread.Sleep(300);
        reader.Start();
        var modes = signal == IntPtr.Zero || gateOff ? new[] { 0 }
            : companion ? new[] { 0, GateInput, GateCompanion, GateInput | GateCompanion } : new[] { 0, GateInput };
        var sent = StressSchedule(seconds, n =>
        {
            int k = n % code.Lap;
            state.Axes[leftX] = code.Value(k % code.Steps);
            state.Axes[rightX] = code.Value(k / code.Steps);
            Volatile.Write(ref highest, n);
            c.SubmitState(state);
        }, modes, signal, companion ? missEvery : 0, missInputEvery, burstOf);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        CancelIoEx(Volatile.Read(ref current).Handle, IntPtr.Zero);
        if (!reader.Join(2000)) { CancelIoEx(Volatile.Read(ref current).Handle, IntPtr.Zero); reader.Join(2000); }
        lock (reopened) foreach (var h in reopened) h.Dispose();
        List<(int, long)> got;
        List<(int, long)>? all;
        List<Misread> wrong;
        lock (seen) { got = seen.ToList(); all = reads?.ToList(); wrong = misreads.ToList(); }
        failures.Report($"{name} ReadFile", sent.Count > 0 ? sent[0].Time : 0, reopened.Count, "reopens");
        DecodeCheck($"{name} ReadFile", tracker, got.Count, before, off, firstOff);
        MisreadDetail($"{name} ReadFile", sent, wrong, code);
        StressVerdict($"{name} ReadFile", sent, got, boundFinals: true);
        if (all != null && burstOf != null) LateDetail($"{name} ReadFile", sent, burstOf, got, all, wgi: false, streamMs: period);
    }

    /// <summary>The Xbox 360 pad through XInputGetState (header), polled
    /// from the slot its create connected.</summary>
    static void StressXInput(HMController c, HashSet<int> before, double seconds, IntPtr signal, bool gateOff, bool lateDetail,
        int missEvery, int missInputEvery)
    {
        string name = $"{Xbox360} XInputGetState";
        int slot = -1;
        var wait = Stopwatch.StartNew();
        while (slot < 0 && wait.ElapsedMilliseconds < 10000)
        {
            slot = XInputConnected().Except(before).DefaultIfEmpty(-1).First();
            if (slot < 0) Thread.Sleep(100);
        }
        if (slot < 0) { Check($"{name}: an XInput slot the create connected", false, "none in 10 s"); return; }
        var sticks = c.Profile.Sticks;
        HMAxis leftX = sticks[0].XAxis, rightX = sticks[1].XAxis;
        // XInput's thumbs are 16-bit: 200 steps.
        var code = AxisCode.ForSpan(65535)!;
        var tracker = new CountTracker(code.Lap);
        var seen = new List<(int, long)>(1 << 20);
        var burstOf = lateDetail ? new List<(int Size, int After)>(1 << 20) : null;
        int highest = 0, early = 0, off = 0;
        string? firstOff = null;
        bool stop = false;
        var failures = new ReaderFailures();
        int rebound = 0;
        var poller = new Thread(() =>
        {
            uint packet = 0;
            int inARow = 0;
            while (!Volatile.Read(ref stop))
            {
                int rc = XInputGetState(slot, out var st);
                long t = Stopwatch.GetTimestamp();
                if (rc != 0)
                {
                    failures.Add(rc, t, tracker.Last, $"slot {slot}");
                    if (++inARow > 20)
                    {
                        // The pad left its slot: take the one that comes back in 5 s.
                        int back = -1;
                        var w = Stopwatch.StartNew();
                        while (back < 0 && !Volatile.Read(ref stop) && w.ElapsedMilliseconds < 5000)
                        {
                            back = XInputConnected().Except(before).DefaultIfEmpty(-1).First();
                            if (back < 0) Thread.Sleep(50);
                        }
                        if (back < 0) { failures.Stop(rc, t, tracker.Last, $"slot {slot} not connected and none came back in 5 s"); return; }
                        slot = back;
                        rebound++;
                        inARow = 0;
                    }
                    continue;
                }
                inARow = 0;
                if (st.PacketNumber == packet) continue;
                packet = st.PacketNumber;
                int x = code.StepOf((st.ThumbLX / 32768.0 + 1) / 2), y = code.StepOf((st.ThumbRX / 32768.0 + 1) / 2);
                if (x < 0 || y < 0)
                {
                    if (tracker.Last < 0) early++;
                    else if (off++ == 0) firstOff = $"ThumbLX {st.ThumbLX}, ThumbRX {st.ThumbRX}";
                    continue;
                }
                if (tracker.Accept(y * code.Steps + x, Volatile.Read(ref highest), out int count)) seen.Add((count, t));
                else if (tracker.Last < 0) early++;
            }
        }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "stress xinput" };
        var state = new HMGamepadState { Axes = new Dictionary<HMAxis, float>() };
        state.Axes[leftX] = code.Value(0);
        state.Axes[rightX] = code.Value(0);
        c.SubmitState(state);
        Thread.Sleep(500);
        poller.Start();
        Console.WriteLine($"  {name}, SubmitState to XInputGetState on slot {slot}, the count in {leftX} and {rightX}, {code.Steps} steps an axis:");
        var modes = signal == IntPtr.Zero || gateOff ? new[] { 0 } : new[] { 0, GateInput, GateCompanion, GateInput | GateCompanion };
        var sent = StressSchedule(seconds, n =>
        {
            int k = n % code.Lap;
            state.Axes[leftX] = code.Value(k % code.Steps);
            state.Axes[rightX] = code.Value(k / code.Steps);
            Volatile.Write(ref highest, n);
            c.SubmitState(state);
        }, modes, signal, missEvery, missInputEvery, burstOf);
        Thread.Sleep((int)StressBoundMs * 2);
        Volatile.Write(ref stop, true);
        poller.Join(2000);
        failures.Report(name, sent.Count > 0 ? sent[0].Time : 0, rebound, "rebinds to a slot that came back");
        DecodeCheck(name, tracker, seen.Count, early, off, firstOff);
        StressVerdict(name, sent, seen, boundFinals: true);
        if (burstOf != null) LateDetail(name, sent, burstOf, seen, seen, wgi: false, streamMs: 0);
    }

    /// <summary>Offline checks of the passes' count encodings for the
    /// selftest: every step of each axis back from each way the SDK puts a
    /// value on the wire, an axis value between steps refused, and counts
    /// unwrapped across laps with a misread caught.</summary>
    internal static IEnumerable<(string Name, bool Ok, string Detail)> StressDecodeSelfTest()
    {
        // The SDK's encodings (WireArith): min + RoundHalfEven(v x span),
        // the truncation an older descriptor path used, and the centered
        // mapping, rounded as a distance from the center, with the center
        // above and below the midpoint.
        static long Linear(double v, long min, long max) => min + (long)Math.Round(v * (max - min), MidpointRounding.ToEven);
        static long Truncated(double v, long min, long max) => min + (long)Math.Floor(v * (max - min));
        static long Centered(double v, long min, long center, long max)
        {
            double t = v - 0.5, low = 2.0 * (center - min), high = 2.0 * (max - center);
            return center + (long)Math.Round(t * low + Math.Max(t, 0.0) * (high - low), MidpointRounding.ToEven);
        }
        var fields = new (string Name, long Min, long Max, int Bits)[]
        {
            ("8-bit", 0, 255, 8), ("16-bit", 0, 65535, 16), ("signed 16-bit", -32768, 32767, 16), ("10-bit", 0, 1023, 10),
        };
        var wrong = new List<string>();
        var steps = new List<string>();
        foreach (var (fname, min, max, bits) in fields)
        {
            var code = AxisCode.ForSpan(max - min)!;
            steps.Add($"{fname} {code.Steps}");
            long mid = min + (max - min + 1) / 2;
            var encodings = new (string Name, Func<double, long> Encode)[]
            {
                ("round half even", v => Linear(v, min, max)),
                ("truncation", v => Truncated(v, min, max)),
                ($"centered at {mid}", v => Centered(v, min, mid, max)),
                ($"centered at {mid - 1}", v => Centered(v, min, mid - 1, max)),
            };
            foreach (var (ename, encode) in encodings)
                for (int k = 0; k < code.Steps; k++)
                {
                    long raw = encode(code.Value(k));
                    // As HidP returns it: the field's bits, unsigned.
                    uint wire = (uint)(raw & ((1L << bits) - 1));
                    int got = code.StepOf(Normalized(wire, min, max, bits));
                    if (got != k) { wrong.Add($"{fname} {ename} step {k} read {got}"); break; }
                }
        }
        yield return ("signal-stress: every step of an 8-bit, 16-bit, signed 16-bit and 10-bit axis reads back from round half even, truncation and a centered mapping",
            wrong.Count == 0, wrong.Count == 0 ? string.Join(", ", steps) + " steps" : string.Join("; ", wrong.Take(4)));

        // XInput's thumbs: the GIP value, round half even on 0..65535, less
        // 32768. A vendor byte: round half even on 0..255.
        var x16 = AxisCode.ForSpan(65535)!;
        var u8 = AxisCode.ForSpan(255)!;
        int xBad = Enumerable.Range(0, x16.Steps).Count(k => x16.StepOf(((short)(Linear(x16.Value(k), 0, 65535) - 32768) / 32768.0 + 1) / 2) != k);
        int vBad = Enumerable.Range(0, u8.Steps).Count(k => u8.StepOf(Linear(u8.Value(k), 0, 255) / 255.0) != k);
        // The rest of an 8-bit stick, 128, and the midpoint between two
        // steps are off every step.
        double between = (u8.Value(10) + u8.Value(11)) / 2.0;
        bool refused = u8.StepOf(128 / 255.0) == -1 && u8.StepOf(between) == -1 && u8.StepOf(0) == -1 && u8.StepOf(1) == -1;
        yield return ("signal-stress: XInput's thumbs and a vendor report's bytes read back at every step, and an 8-bit stick at rest or between steps reads none",
            xBad == 0 && vBad == 0 && refused, $"{xBad} XInput and {vBad} vendor steps wrong, rest and between {(refused ? "refused" : "read")}");

        // Counts as a reader sees them, on an 8-bit code's lap of 4,225: a
        // first reading above every count submitted, the pad's own state,
        // skipped; gaps of up to 4,224 across three laps; a count not yet
        // submitted, a misread; a count read again, no count; and the frame
        // before the last one read, after it, an older frame.
        var lap = u8.Lap;
        var tracker = new CountTracker(lap);
        var log = new List<string>();
        bool Feed(int count, int highest)
        {
            bool ok = tracker.Accept(count % lap, highest, out int c);
            log.Add(ok ? c.ToString() : "-");
            return ok;
        }
        bool trackOk = !Feed(lap - 1, 0) && Feed(0, 0) && Feed(1, 5) && Feed(4001, 4100) && Feed(4232, 4232) && Feed(8000, 8000)
            && Feed(8452, 8452) && Feed(12676, 12676) && !Feed(12680, 12677) && tracker.Misread == 1 && tracker.MisreadValue == 12680
            && Feed(12677, 12677) && !Feed(12677, 12677) && tracker.Misread == 1 && tracker.Last == 12677
            && !Feed(12676, 12680) && tracker.Older == 1 && tracker.OlderValue == 12676 && tracker.OlderAfter == 12677
            && tracker.Misread == 1 && Feed(12678, 12680) && tracker.Last == 12678;
        yield return ("signal-stress: counts unwrap across laps of 4,225, a first reading above every count submitted is skipped, one later is a misread, a repeat is no count, and a frame before the last one read is an older frame",
            trackOk, string.Join(" ", log));
    }
}
