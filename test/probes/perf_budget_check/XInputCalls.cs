// xinput-calls [runs] [priority-ab]: a diagnostic, never a row of the release comparison.
// On xbox-360-wired, the time of each XInputGetState call around a new
// frame. After each SubmitState it polls XInputGetState as the e2e mode's
// XInput pass does, and records the call that first returns the frame, every
// call before it, and CompanionParked, the signal section's count at 128 of
// the WAIT_FOR_INPUT requests other readers hold, read just before the
// submit. 2.1.0's GET_STATE reports a frame no turn has read yet under the
// companion's report lock before it answers, and reporting completes the
// parked requests. Through 2.0.3 GET_STATE decoded the section with no lock.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HIDMaestro;

internal static partial class Program
{
    // With "priority-ab" the passes run three times: the companion's host at
    // the priority class its INF set, at Normal, then at its INF's class
    // again. The host is the WUDFHost that loaded HMXInput.dll after this
    // probe created its controller, so no other program's host is touched,
    // and its class goes back once the passes end.
    static int XInputCalls(int runs, bool priorityAb)
    {
        if (!Elevated("xinput-calls")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var before = XInputConnected();
        var hostsBefore = new HashSet<int>(Array.ConvertAll(Process.GetProcessesByName("WUDFHost"), p => p.Id));
        using var c = TryCreate(ctx, ctx.GetProfile(Xbox360)!, Xbox360, s_key);
        if (c == null) return 1;
        Process? host = null;
        if (priorityAb)
        {
            foreach (var p in Process.GetProcessesByName("WUDFHost"))
            {
                if (hostsBefore.Contains(p.Id)) continue;
                try
                {
                    foreach (ProcessModule m in p.Modules)
                        if (string.Equals(m.ModuleName, "HMXInput.dll", StringComparison.OrdinalIgnoreCase)) { host = p; break; }
                }
                catch (Exception ex) { Console.WriteLine($"  WUDFHost {p.Id}: modules not readable, {ex.Message}"); }
                if (host != null) break;
            }
            if (host == null) { Console.WriteLine("  xbox-360-wired: no new WUDFHost with HMXInput.dll"); return 1; }
            Console.WriteLine($"  xbox-360-wired: the companion's host is WUDFHost {host.Id}, priority class {host.PriorityClass}");
        }
        var installed = host?.PriorityClass ?? ProcessPriorityClass.Normal;
        var phases = priorityAb
            ? new[] { ("as installed", installed), ("Normal", ProcessPriorityClass.Normal), ("as installed again", installed) }
            : new[] { ("", installed) };
        try
        {
            foreach (var (label, cls) in phases)
            {
                if (host != null)
                {
                    host.PriorityClass = cls;
                    host.Refresh();
                    Console.WriteLine($"  == the companion's host at {host.PriorityClass} ({label})");
                }
                int code = XInputCallsPass(c, runs, before);
                if (code != 0) return code;
            }
        }
        finally
        {
            if (host != null) { try { host.PriorityClass = installed; } catch { } }
        }
        return 0;
    }

    static int XInputCallsPass(HMController c, int runs, HashSet<int> before)
    {
        int slot = -1;
        var wait = Stopwatch.StartNew();
        while (slot < 0 && wait.ElapsedMilliseconds < 5000)
        {
            foreach (int s in XInputConnected())
                if (!before.Contains(s)) { slot = s; break; }
            if (slot < 0) Thread.Sleep(50);
        }
        if (slot < 0)
        {
            Console.WriteLine("  xbox-360-wired: no XInput slot appeared");
            return 1;
        }
        IntPtr signal = SignalView(c);
        var a = new HMGamepadState { Buttons = HMButton.A };
        var b = new HMGamepadState { Buttons = HMButton.B };
        c.SubmitState(b);
        Thread.Sleep(200);
        var latency = new List<double>(runs * 50);
        var hitCall = new List<double>(runs * 50);
        var otherCall = new List<double>(runs * 200);
        var calls = new List<double>(runs * 50);
        var parked = new List<double>(runs * 50);
        double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
        var rng = new Random(84);
        bool flip = true;
        int missed = 0;
        for (int run = 0; run < runs; run++)
        {
            for (int i = 0; i < 50; i++)
            {
                Spin(rng.Next(2_000, 19_000));
                flip = !flip;
                ushort want = flip ? XInputB : XInputA;
                if (signal != IntPtr.Zero) parked.Add(ReadParked(signal));
                long t0 = Stopwatch.GetTimestamp();
                c.SubmitState(flip ? b : a);
                long deadline = t0 + Stopwatch.Frequency / 10;
                long hit = 0;
                int n = 0;
                while (true)
                {
                    long s0 = Stopwatch.GetTimestamp();
                    if (s0 >= deadline) break;
                    int r = XInputGetState(slot, out var st);
                    long s1 = Stopwatch.GetTimestamp();
                    n++;
                    if (r == 0 && (st.Buttons & (XInputA | XInputB)) == want)
                    {
                        hit = s1;
                        hitCall.Add(Us(s1 - s0));
                        break;
                    }
                    otherCall.Add(Us(s1 - s0));
                }
                if (hit != 0)
                {
                    latency.Add(Us(hit - t0));
                    calls.Add(n);
                }
                else { missed++; Thread.Sleep(200); }
            }
        }
        Report("xbox-360-wired: SubmitState to an XInputGetState that shows it, microseconds", latency, runs, $", {missed} not seen within 100 ms");
        Report("xbox-360-wired: the XInputGetState call that first shows the frame, microseconds", hitCall, runs, "");
        Report("xbox-360-wired: an XInputGetState call before that one, microseconds", otherCall, runs, "");
        Report("xbox-360-wired: XInputGetState calls until the frame shows", calls, runs, "");
        if (signal != IntPtr.Zero)
            Report("xbox-360-wired: WAIT_FOR_INPUT requests parked at the submit (CompanionParked)", parked, runs, "");
        else
            Console.WriteLine("  xbox-360-wired: this SDK has no signal section, so no parked count");
        return 0;
    }

    static unsafe int ReadParked(IntPtr signal) => Volatile.Read(ref *(int*)((byte*)signal + 128));
}
