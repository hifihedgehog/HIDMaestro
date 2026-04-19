using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// XInput state-latency meter. Polls XInputGetState on the given slots
// at ~1 kHz for a fixed duration, logging (timestamp_us, slot, packet#,
// wButtons, LX, LY, RX, RY) to a CSV. Pair with HIDMaestroTest emulate
// which submits a time-varying test pattern; the CSV lets us measure
// propagation latency (submission timestamp vs. dwPacketNumber advance)
// and detect freezes (packet# stops advancing while SDK still submits).
//
// Usage:
//   xinput_latency_meter.exe <duration_sec> <out.csv> [slot ...]
//
// If no slots are specified, polls all four (0-3). xinputhid does not
// always allocate consecutive slot numbers across virtuals — a common
// assignment for 2x xbox-series-xs-bt is slots 0 and 2, not 0 and 1.
// If you want targeted slots only, pass them explicitly.

internal static class P
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }
    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    static int Main(string[] args)
    {
        if (args.Length < 2) {
            Console.Error.WriteLine("usage: xinput_latency_meter <duration_sec> <out.csv> [slot ...]");
            return 1;
        }
        if (!int.TryParse(args[0], out int seconds) || seconds <= 0) { Console.Error.WriteLine("bad duration"); return 1; }
        string outPath = args[1];
        var slots = new System.Collections.Generic.List<uint>();
        for (int i = 2; i < args.Length; i++)
            if (uint.TryParse(args[i], out uint s) && s < 4) slots.Add(s);
        if (slots.Count == 0) {
            // Default: poll all four slots. xinputhid allocation is not
            // guaranteed to use consecutive slot numbers when multiple
            // virtuals are present (e.g. 2x xinputhid often lands on
            // slots 0 + 2).
            slots.AddRange(new uint[] { 0u, 1u, 2u, 3u });
        }

        using var sw = new StreamWriter(outPath);
        sw.WriteLine("elapsed_us,slot,rc,packet,wButtons,LX,LY,RX,RY,LT,RT");

        var clock = Stopwatch.StartNew();
        long endTicks = (long)(Stopwatch.Frequency * (double)seconds);

        while (clock.ElapsedTicks < endTicks)
        {
            long elapsedTicks = clock.ElapsedTicks;
            long elapsedUs = (long)((double)elapsedTicks / Stopwatch.Frequency * 1_000_000);
            foreach (uint slot in slots)
            {
                uint rc = XInputGetState(slot, out var st);
                sw.WriteLine($"{elapsedUs},{slot},0x{rc:X},{st.dwPacketNumber},0x{st.Gamepad.wButtons:X4},{st.Gamepad.sThumbLX},{st.Gamepad.sThumbLY},{st.Gamepad.sThumbRX},{st.Gamepad.sThumbRY},{st.Gamepad.bLeftTrigger},{st.Gamepad.bRightTrigger}");
            }
            // ~1 kHz per slot-sweep; tight loop + YieldProcessor kept Thread.Sleep out
            // of the hot path so we don't add 1ms granular jitter to the measurement.
            long next = elapsedTicks + Stopwatch.Frequency / 1000; // 1 ms
            while (clock.ElapsedTicks < next) Thread.SpinWait(10);
        }
        sw.Flush();
        Console.WriteLine($"{slots.Count} slot(s), {seconds}s, wrote {outPath}");
        return 0;
    }
}
