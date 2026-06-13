using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

namespace HIDMaestroTest;

// End-to-end input-latency benchmark, methodology-matched to VIIPER's
// published E2E figures (docs/testing/e2e_latency.md): a single emulated
// controller, a single button transition per iteration, and a tight reader
// loop that detects the state change. VIIPER reads via SDL3 (which on
// Windows/Xbox360 routes through XInput); this measures the same XInput
// surface directly so the comparison is like-for-like.
//
// The clock is shared (one process, one Stopwatch/QPC), so there is no
// cross-process clock-skew to correct for. We measure from the SubmitState
// call to the moment XInputGetState first reports the toggled A button.
//
// Detection is by button bit, NOT by dwPacketNumber: the XUSB companion
// increments PacketCount on every GET_STATE (driver/companion.c), so the
// packet number advances on every poll regardless of input change and is
// useless as a change detector here. The A-button bit (0x1000) reflects the
// actual GipData the SDK wrote, so it tracks real propagation.
//
//   HIDMaestroTest latency [profile-id] [iterations]
//     profile-id  default xbox-360-wired (XInput-backed, matches VIIPER)
//     iterations  default 10000

partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD_L
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE_L
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD_L Gamepad;
    }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint XInputGetState_L(uint dwUserIndex, out XINPUT_STATE_L pState);

    const ushort XINPUT_A = 0x1000;

    static HMGamepadState LatState(HMController ctrl, bool aPressed) => new HMGamepadState
    {
        Axes = HMGamepadStateHelpers.StandardAxes(ctrl.Profile),
        Buttons = aPressed ? HMButton.A : HMButton.None,
    };

    // Compile fully optimized on first call. Without this the method starts in
    // tier-0 and the runtime swaps in an optimized build mid-loop via on-stack
    // replacement at a fixed back-edge count, producing a one-time multi-ms
    // stall (deterministically around iteration 4895) that is a JIT artifact,
    // not device latency. AggressiveOptimization skips tier-0 for this method,
    // so the measured loop never gets recompiled out from under itself.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int LatencyBench(string[] args)
    {
        string profileId = "xbox-360-wired";
        int iters = 10000;
        foreach (var a in args)
        {
            if (int.TryParse(a, out int n)) { if (n >= 100) iters = n; }
            else profileId = a;
        }

        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "(unknown CPU)";
        Console.WriteLine($"  CPU: {cpu}");
        Console.WriteLine($"  Cores: {Environment.ProcessorCount}");

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
        Thread.CurrentThread.Priority = ThreadPriority.Highest;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.Write("  Installing driver... "); ctx.InstallDriver(); Console.WriteLine("OK");
        var profile = ctx.GetProfile(profileId);
        if (profile == null) return Error($"Profile '{profileId}' not found");
        Console.Write($"  Creating controller ({profile.Name})... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        // Find the XInput slot our controller landed on. Submit a neutral
        // frame each attempt so the slot reports Connected promptly.
        int slot = -1;
        var find = Stopwatch.StartNew();
        while (find.ElapsedMilliseconds < 6000 && slot < 0)
        {
            var neutral = LatState(ctrl, false);
            ctrl.SubmitState(in neutral);
            for (uint s = 0; s < 4; s++)
                if (XInputGetState_L(s, out _) == 0) { slot = (int)s; break; }
            if (slot < 0) Thread.Sleep(50);
        }
        if (slot < 0) return Error("No XInput slot appeared for the controller within 6s");
        Console.WriteLine($"  XInput slot: {slot}");

        double freq = Stopwatch.Frequency;

        // Warm up: drive the toggle for a bit so the path is hot and the
        // initial connect/allocation transients are out of the sample.
        for (int i = 0; i < 200; i++)
        {
            var s = LatState(ctrl, (i & 1) == 0);
            ctrl.SubmitState(in s);
            Thread.Sleep(2);
        }

        // Establish a known A=0 baseline before the first timed transition.
        {
            var s = LatState(ctrl, false);
            ctrl.SubmitState(in s);
            var bdl = Stopwatch.GetTimestamp() + (long)(freq * 0.1);
            while (Stopwatch.GetTimestamp() < bdl)
            {
                XInputGetState_L((uint)slot, out var st);
                if ((st.Gamepad.wButtons & XINPUT_A) == 0) break;
            }
        }

        // Allocate the axes dict ONCE and reuse it. StandardAxes news up a
        // Dictionary per call; calling it 10k times inside the measured loop
        // drives gen0 GCs, and a collection that lands in a timed window
        // suspends this thread and surfaces as a multi-millisecond outlier
        // that has nothing to do with device latency. HMGamepadState is a
        // struct, so the per-iteration value is stack-only; only Buttons
        // changes between frames.
        var axesReuse = HMGamepadStateHelpers.StandardAxes(ctrl.Profile);

        var lat = new double[iters];
        long timeouts = 0;
        int press = 0;
        double maxVal = -1; int maxIdx = -1; int over1ms = 0;
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        for (int i = 0; i < iters; i++)
        {
            press ^= 1;
            ushort want = press == 1 ? XINPUT_A : (ushort)0;
            var state = new HMGamepadState { Axes = axesReuse, Buttons = press == 1 ? HMButton.A : HMButton.None };

            long t0 = Stopwatch.GetTimestamp();
            ctrl.SubmitState(in state);
            long spinDeadline = t0 + (long)(freq * 0.05); // 50 ms safety cap
            long t1;
            while (true)
            {
                XInputGetState_L((uint)slot, out var st);
                if ((st.Gamepad.wButtons & XINPUT_A) == want) { t1 = Stopwatch.GetTimestamp(); break; }
                if (Stopwatch.GetTimestamp() > spinDeadline) { t1 = Stopwatch.GetTimestamp(); timeouts++; break; }
            }
            double us = (t1 - t0) / freq * 1e6; // microseconds
            lat[i] = us;
            if (us > maxVal) { maxVal = us; maxIdx = i; }
            if (us > 1000.0) over1ms++;
        }
        int dg0 = GC.CollectionCount(0) - gc0, dg1 = GC.CollectionCount(1) - gc1, dg2 = GC.CollectionCount(2) - gc2;

        Array.Sort(lat);
        double Pct(double p)
        {
            int idx = (int)Math.Round(p / 100.0 * (iters - 1));
            return lat[Math.Clamp(idx, 0, iters - 1)];
        }
        double mean = 0; foreach (var x in lat) mean += x; mean /= iters;

        Console.WriteLine();
        Console.WriteLine($"  === XInput E2E single-press latency ===");
        Console.WriteLine($"  profile     {profile.Name} ({profileId})");
        Console.WriteLine($"  iterations  {iters}");
        Console.WriteLine($"  min         {lat[0]:F2} us");
        Console.WriteLine($"  median      {Pct(50):F2} us");
        Console.WriteLine($"  mean        {mean:F2} us");
        Console.WriteLine($"  p90         {Pct(90):F2} us");
        Console.WriteLine($"  p99         {Pct(99):F2} us");
        Console.WriteLine($"  max         {lat[iters - 1]:F2} us  (at iter {maxIdx} of {iters})");
        Console.WriteLine($"  >1ms samples {over1ms}");
        Console.WriteLine($"  GC in loop  gen0={dg0} gen1={dg1} gen2={dg2}");
        Console.WriteLine($"  timeouts    {timeouts} (>50ms; should be 0)");
        Console.WriteLine();
        Console.WriteLine($"  Reference (VIIPER docs/testing/e2e_latency.md, published):");
        Console.WriteLine($"    Windows / Ryzen 9 3900X  E2E single-press  168.31 us");
        Console.WriteLine($"    Steam Deck LCD           E2E single-press   89.08 us");
        return 0;
    }
}
