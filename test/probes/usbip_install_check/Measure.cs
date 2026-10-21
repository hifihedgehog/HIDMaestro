// V: the USB/IP input path with KMDF's verifier values on and off. KMDF reads
// Services\usbip2_ude\Parameters\Wdf when the driver loads, so each run sets
// the values (or leaves them out) on a fresh install, restarts the host
// controller while nothing has used it, and measures a composite on the
// driver that load gave. SubmitState to the ReadFile that returns a report
// carrying the new state, fifty submits a run, thirty runs, each submit after
// a random 2 to 19 ms, as perf_budget_check's e2e pass times the host's read.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

internal static partial class Program
{
    const int MeasureRuns = 30, SubmitsPerRun = 50;

    static void Measure(bool on)
    {
        string m = on ? "verifier-on:" : "verifier-off:";
        HMContext.InstallUsbipBackend();
        var (hc, hub, _) = Installed();
        Record($"{m} the host controller answers", hc != null && hub != null && Answers(), $"{hc} {hub}");
        if (hc == null || hub == null) return;

        if (on)
        {
            using var k = Registry.LocalMachine.CreateSubKey(WdfKey);
            k.SetValue("VerifierOn", 1, RegistryValueKind.DWord);
            k.SetValue("VerboseOn", 1, RegistryValueKind.DWord);
        }
        Record($"{m} KMDF's debug values are {(on ? "set" : "absent")} before the driver loads again",
               on ? WdfDebugValues().Count == 2 : WdfDebugValues().Count == 0, string.Join(",", WdfDebugValues()));

        // Nothing has been attached through it since the sandbox started:
        // its root hub has no USB device below it, present or not.
        bool unused = !Pnp.IdList("USB").Any(id => string.Equals(Pnp.Parent(id), hub, StringComparison.OrdinalIgnoreCase));
        Record($"{m} the host controller has carried no device", unused);
        if (!unused) return;
        var restart = Run(System.IO.Path.Combine(Environment.SystemDirectory, "pnputil.exe"), 120_000, "/restart-device", hc);
        Note($"{m} pnputil /restart-device {hc} exited {restart.Exit}: {restart.Output.Trim().Replace(Environment.NewLine, " ")}");
        Record($"{m} the host controller restarted and answers", restart.Exit == 0 && Answers());

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        using var c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, "sandbox-composite");
        // The SDK clears the stale values on its first composite create. The
        // driver loaded above keeps what it read until it loads again.
        Record($"{m} the composite create leaves no KMDF debug values in the service key", WdfDebugValues().Count == 0, string.Join(",", WdfDebugValues()));
        var paths = new List<string>();
        var sw = Stopwatch.StartNew();
        while (paths.Count == 0 && sw.ElapsedMilliseconds < 30_000)
        {
            paths = Hid.Paths(0x054C, 0x0CE6);
            if (paths.Count == 0) Thread.Sleep(250);
        }
        Record($"{m} the composite's HID interface appears", paths.Count > 0);
        if (paths.Count == 0) return;
        Note($"{m} device installation {Pnp.InstallState(60_000)} before the measurement");
        Thread.Sleep(1000);

        // Which byte tells A from B, and its value in each.
        Sdk.Controller = c;
        var stateA = new HMGamepadState { Buttons = HMButton.A };
        var stateB = new HMGamepadState { Buttons = HMButton.B };
        var a = ReadWith(paths[0], stateA);
        var b = ReadWith(paths[0], stateB);
        var diff = SteadyDifferences(a, b);
        Record($"{m} a byte tells the two states apart", diff.Count > 0, $"byte(s) {string.Join(",", diff)}");
        if (diff.Count == 0) return;
        int at = diff[0];
        byte va = a.Frames![0][at], vb = b.Frames![0][at];

        using var watch = new Hid.Watch(paths[0], at);
        Record($"{m} the reader opens the interface", watch.Open);
        var rng = new Random(84);
        var samples = new List<double>(MeasureRuns * SubmitsPerRun);
        int missed = 0;
        bool flip = false;
        c.SubmitState(stateA);
        Thread.Sleep(200);
        for (int run = 0; run < MeasureRuns; run++)
        {
            for (int i = 0; i < SubmitsPerRun; i++)
            {
                Spin(rng.Next(2_000, 19_000));
                flip = !flip;
                watch.Arm(flip ? vb : va);
                long t0 = Stopwatch.GetTimestamp();
                c.SubmitState(flip ? stateB : stateA);
                long deadline = t0 + Stopwatch.Frequency / 10;
                long hit = 0;
                while (Stopwatch.GetTimestamp() < deadline && (hit = watch.Hit) == 0) Thread.SpinWait(16);
                if (hit != 0) samples.Add((hit - t0) * 1e6 / Stopwatch.Frequency);
                else { missed++; Thread.Sleep(200); }
            }
        }
        samples.Sort();
        double P(double q) => samples.Count == 0 ? double.NaN : samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(q * samples.Count) - 1)];
        Note(FormattableString.Invariant(
            $"{m} SubmitState to a ReadFile that shows it, microseconds: p50 {P(0.50):F1}, p95 {P(0.95):F1}, max {(samples.Count > 0 ? samples[^1] : double.NaN):F1} ({samples.Count} samples, {MeasureRuns} runs), {missed} not seen within 100 ms"));
        Record($"{m} the reads saw nearly every submit", samples.Count >= MeasureRuns * SubmitsPerRun * 9 / 10, $"{samples.Count} of {MeasureRuns * SubmitsPerRun}");
    }

    static void Spin(int microseconds)
    {
        long until = Stopwatch.GetTimestamp() + microseconds * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(32);
    }
}
