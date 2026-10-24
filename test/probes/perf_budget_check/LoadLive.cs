// load [runs] [seconds]: a consumer's whole load, live and elevated. Four
// controllers up at once, created one after another as a program creates
// them: two xbox-360-wired, one dualshock-4-v2 and one dualsense-bt, each
// with its own identity (--key=<k> gives <k>-0 to <k>-3) and its own reader,
// a child process of this probe reading the controller's HID input reports
// as a game does (the rate mode's reader). Each run has two windows of
// [seconds] (3 unless given):
//   active   one thread submits every controller once a millisecond, each
//            tick a point of a 1 ms grid waited for on a high-resolution
//            waitable timer, as one consumer loop drives several pads
//   at rest  nothing submits, so only the idle repeats a profile's real
//            device sends go out
// Over each window it reads the CPU cycles of this process, of the readers
// together and of every WUDFHost process, which host the UMDF2 drivers, and
// each controller's reports read. Then, with the three others submitting
// every millisecond on that thread, the dualshock-4-v2's latency as the e2e
// mode takes it: SubmitState to the driver's acknowledgement (AckPass) and
// to the ReadFile of a host reader in this process (HostReadPass), each 50
// frames a run. Rows give p50 and p95 over the runs. WUDFHost processes are
// only read, never stopped. Their sum includes every UMDF2 device on the
// machine, so the builds compared alternate on one machine with the same
// devices attached. No keyboard or mouse profile is ever part of the set.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HIDMaestro;

internal static partial class Program
{
    static readonly string[] LoadSet = { Xbox360, Xbox360, Ds4, "dualsense-bt" };

    const string LoadActive = "load, every pad submitting every millisecond";
    const string LoadRest = "load, at rest";

    sealed class LoadWindow
    {
        public readonly List<double> SelfMcps = new(), ReadersMcps = new(), HostsMcps = new(), Submits = new();
        public readonly List<double>[] Reads;
        public LoadWindow(int n) => Reads = Enumerable.Range(0, n).Select(_ => new List<double>()).ToArray();
    }

    static int Load(int runs, string[] args)
    {
        double seconds = 3;
        foreach (string a in args)
            if (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0) seconds = s;
        if (!Elevated("load")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var pads = new List<HMController>();
        var readers = new List<Process>();
        var labels = new List<string>();
        try
        {
            for (int n = 0; n < LoadSet.Length; n++)
            {
                string id = LoadSet[n];
                var profile = ctx.GetProfile(id);
                if (profile == null) { Check($"load: {id} a catalog profile", false, "no such id"); return 1; }
                // One create at a time: never two in parallel in one process.
                var c = ctx.CreateController(profile, s_key == null ? null : $"{s_key}-{n}");
                pads.Add(c);
                if (!OnEmbeddedDriver(c, id)) return 5;
                string? path = OwnInputInterface(c);
                if (path == null) { Check($"load: {id} a HID input interface under its devnode", false, "none in 5 s"); return 1; }
                readers.Add(Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                    ArgumentList = { "rate-reader", path },
                })!);
                labels.Add(LoadSet.Count(x => x == id) > 1 ? $"{id} {LoadSet.Take(n + 1).Count(x => x == id)}" : id);
            }
            // Each phase's wall time, so a run that takes far longer than its
            // windows says where (one did on 2026-10-24: 2,737 s against
            // 159 s, MEAS-load-2 run 02).
            var phase = Stopwatch.StartNew();
            Console.WriteLine($"  load: {pads.Count} pads and readers up, {phase.Elapsed.TotalSeconds:F1} s");
            var a = new HMGamepadState { Buttons = HMButton.A };
            var b = new HMGamepadState { Buttons = HMButton.B };
            foreach (var c in pads) c.SubmitState(a);
            // Past the readers' opens and the tiered JIT's recompiles of the
            // submit path, so the windows read the steady state.
            Thread.Sleep(1500);
            var hosts = Process.GetProcessesByName("WUDFHost");
            var active = new LoadWindow(pads.Count);
            var rest = new LoadWindow(pads.Count);
            using var timer = HighResolutionTimer();
            for (int run = 0; run < runs; run++)
            {
                LoadRun(pads, a, b, readers, hosts, seconds, timer, active);
                // The last frames' reads and a repeat's restart go out first.
                Thread.Sleep(250);
                LoadRun(pads, a, b, readers, hosts, seconds, null, rest);
            }
            foreach (var h in hosts) h.Dispose();
            Console.WriteLine($"  load: windows done, {phase.Elapsed.TotalSeconds:F1} s");
            ReportLoad(LoadActive, active, labels, runs);
            ReportLoad(LoadRest, rest, labels, runs);

            // The dualshock-4-v2's latency while the other three submit on
            // the consumer thread every millisecond.
            int ds4 = Array.IndexOf(LoadSet, Ds4);
            var others = pads.Where((_, i) => i != ds4).ToArray();
            using var stop = new ManualResetEventSlim(false);
            var loop = new Thread(() =>
            {
                using var t = HighResolutionTimer();
                long period = Stopwatch.Frequency / 1000, next = Stopwatch.GetTimestamp();
                bool flip = false;
                while (!stop.IsSet)
                {
                    WaitUntil(t, next += period);
                    flip = !flip;
                    foreach (var c in others) c.SubmitState(flip ? b : a);
                }
            }) { IsBackground = true, Name = "load consumer" };
            loop.Start();
            try
            {
                string text = "load, dualshock-4-v2 beside three pads submitting every millisecond";
                AckPass(pads[ds4], text, runs);
                Console.WriteLine($"  load: acknowledgement pass done, {phase.Elapsed.TotalSeconds:F1} s");
                HostReadPass(pads[ds4], new ProfileArg(text, Ds4, Array.Empty<byte[]>()), runs);
                Console.WriteLine($"  load: host read pass done, {phase.Elapsed.TotalSeconds:F1} s");
            }
            finally
            {
                stop.Set();
                loop.Join(2000);
            }
        }
        finally
        {
            foreach (var r in readers)
            {
                try { r.StandardInput.Close(); } catch (System.IO.IOException) { }
                // A reader ends when its input closes. One that does not is
                // this probe's own child, stopped here.
                if (!r.WaitForExit(5000)) r.Kill();
                r.Dispose();
            }
            // Disposed one at a time, in the order created.
            foreach (var c in pads) c.Dispose();
        }
        return 0;
    }

    static void LoadRun(List<HMController> pads, in HMGamepadState a, in HMGamepadState b, List<Process> readers, Process[] hosts,
        double seconds, Microsoft.Win32.SafeHandles.SafeWaitHandle? timer, LoadWindow into)
    {
        var self = Process.GetCurrentProcess();
        long w0 = Stopwatch.GetTimestamp();
        long[] reads0 = readers.Select(ReaderCount).ToArray();
        ulong self0 = Cycles(self), readers0 = readers.Aggregate(0UL, (s, p) => s + Cycles(p)), hosts0 = hosts.Aggregate(0UL, (s, p) => s + Cycles(p));
        long t0 = Stopwatch.GetTimestamp();
        long end = t0 + (long)(seconds * Stopwatch.Frequency);
        long submits = 0;
        if (timer != null)
        {
            long period = Stopwatch.Frequency / 1000, next = t0;
            bool flip = false;
            while (Stopwatch.GetTimestamp() < end)
            {
                // The grid point, not the last wake plus a period, so a late
                // wake does not lower the rate.
                WaitUntil(timer, next += period);
                flip = !flip;
                foreach (var c in pads) c.SubmitState(flip ? b : a);
                submits += pads.Count;
            }
        }
        else
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
        long t1 = Stopwatch.GetTimestamp();
        ulong self1 = Cycles(self), readers1 = readers.Aggregate(0UL, (s, p) => s + Cycles(p)), hosts1 = hosts.Aggregate(0UL, (s, p) => s + Cycles(p));
        long[] reads1 = readers.Select(ReaderCount).ToArray();
        double wall = (t1 - t0) / (double)Stopwatch.Frequency;
        into.SelfMcps.Add((self1 - self0) / wall / 1e6);
        into.ReadersMcps.Add((readers1 - readers0) / wall / 1e6);
        into.HostsMcps.Add((hosts1 - hosts0) / wall / 1e6);
        into.Submits.Add(submits / wall);
        for (int i = 0; i < readers.Count; i++) into.Reads[i].Add((reads1[i] - reads0[i]) / wall);
        // A window that took a second longer than asked, with its counts
        // and reads, says where a slow run's time went.
        double whole = (Stopwatch.GetTimestamp() - w0) / (double)Stopwatch.Frequency;
        if (whole > seconds + 1)
            Console.WriteLine(FormattableString.Invariant(
                $"  load: a {(timer != null ? "active" : "rest")} window took {whole:F1} s for {seconds:F1} s ({wall:F1} s between its counts), reads {string.Join(" ", reads1.Zip(reads0, (x, y) => x - y))}"));
    }

    static void ReportLoad(string name, LoadWindow w, List<string> labels, int runs)
    {
        Report($"{name}: frames submitted per second, all pads", w.Submits, runs, "");
        Report($"{name}: the SDK process's megacycles a second", w.SelfMcps, runs, "");
        Report($"{name}: the driver hosts' megacycles a second", w.HostsMcps, runs, "");
        Report($"{name}: the readers' megacycles a second", w.ReadersMcps, runs, "");
        Report($"{name}: all three together, megacycles a second",
            w.SelfMcps.Select((x, i) => x + w.HostsMcps[i] + w.ReadersMcps[i]).ToList(), runs, "");
        for (int i = 0; i < labels.Count; i++)
            Report($"{name}: {labels[i]}, reports read per second", w.Reads[i], runs, "");
    }
}
