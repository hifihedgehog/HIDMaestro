// usbip-audio [runs] [seconds]: a DualSense composite's speaker and
// microphone streaming in this process, with this probe on the wire in the
// host controller's place, as the usbip mode drives a device. Each stream
// keeps two isochronous URBs of ten 1 ms packets in flight, and each URB
// answered is submitted again at once. Over each window of [seconds] (2
// unless given) a consumer feeds the microphone a URB's PCM for each mic
// URB answered. Over each window it reads the CPU cycles of this process, which are the
// device's (its audio pump, its connection's reader) and this probe's own
// loop, the same code on both sides of a comparison. Then the same window
// with the device attached and no stream, the audio engine at rest. Each
// URB's reply is timed against its stream's grid: the stream's first reply
// plus 10 ms per URB, the device's own clock, so the lateness of a reply is
// how far the device's completion and the loopback hop land past the grid
// point. The lateness is relative to the earliest reply of the window, so
// a constant offset reads as zero.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static void UsbipAudio(int runs, double seconds)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var server = UsbipServer.GetOrStart();
        var profile = ctx.GetProfile(Composite)!.Inner;
        const int Packets = 10, SpeakerPacket = 392, MicPacket = 196, InFlight = 2;
        const string name = "USB/IP audio streaming, " + Composite;

        var device = new UsbipEmulatedDevice(profile, UsbipIndex);
        server.Register(device);
        try
        {
            using var w = new UsbipWire(server.Port, device.BusId);
            var pcm = new byte[Packets * SpeakerPacket];
            // A URB's worth of microphone PCM, fed as a consumer feeds it,
            // 48 kHz, two channels of 16 bits: 192 bytes a millisecond.
            var mic = new byte[Packets * 192];
            var self = Process.GetCurrentProcess();
            long urbTicks = Stopwatch.Frequency * Packets / 1000;
            int capacity = (int)(seconds * 1000 / Packets * 2) + 16;
            var speakerAt = new long[capacity];
            var micAt = new long[capacity];

            // One window: both streams kept full for the time given. Returns
            // the replies counted, each stream's reply times in its array.
            (int Speaker, int Mic) Stream(double windowSeconds)
            {
                for (int i = 0; i < InFlight; i++)
                {
                    w.SubmitIsoOut(1, pcm, Packets, SpeakerPacket);
                    w.SubmitIsoIn(2, Packets, MicPacket);
                }
                int speaker = 0, micReplies = 0;
                long end = Stopwatch.GetTimestamp() + (long)(windowSeconds * Stopwatch.Frequency);
                bool more = true;
                int left = 2 * InFlight;
                while (left > 0)
                {
                    uint seq = w.ReadAny();
                    long now = Stopwatch.GetTimestamp();
                    bool isIn = (seq & 1) == 1;
                    if (isIn) { if (micReplies < micAt.Length) micAt[micReplies++] = now; }
                    else if (speaker < speakerAt.Length) speakerAt[speaker++] = now;
                    if (more && now >= end) more = false;
                    if (more)
                    {
                        if (isIn)
                        {
                            device.Audio.SubmitMicSamples(mic);
                            w.SubmitIsoIn(2, Packets, MicPacket);
                        }
                        else
                        {
                            pcm[0] = (byte)speaker;
                            w.SubmitIsoOut(1, pcm, Packets, SpeakerPacket);
                        }
                    }
                    else left--;
                }
                return (speaker, micReplies);
            }

            // Past the stream's start, the JIT's recompiles and the event
            // thread's first stream change.
            Stream(1.5);
            var cyclesStreaming = new List<double>(runs);
            var cyclesAtRest = new List<double>(runs);
            var lateness = new List<double>(runs * capacity);
            var perUrb = new List<double>(runs);
            for (int run = 0; run < runs; run++)
            {
                QueryProcessCycleTime(self.Handle, out ulong c0);
                long t0 = Stopwatch.GetTimestamp();
                var (speaker, micCount) = Stream(seconds);
                long t1 = Stopwatch.GetTimestamp();
                QueryProcessCycleTime(self.Handle, out ulong c1);
                double wall = (t1 - t0) / (double)Stopwatch.Frequency;
                cyclesStreaming.Add((c1 - c0) / 1e6 / wall);
                if (speaker + micCount > 0) perUrb.Add((c1 - c0) / 1000.0 / (speaker + micCount));
                Lateness(speakerAt, speaker, urbTicks, lateness);
                Lateness(micAt, micCount, urbTicks, lateness);

                // At rest: attached, nothing in flight. Past the engine's
                // own park, which comes at most 250 ms after its last URB.
                System.Threading.Thread.Sleep(300);
                QueryProcessCycleTime(self.Handle, out ulong r0);
                long s0 = Stopwatch.GetTimestamp();
                System.Threading.Thread.Sleep((int)(seconds * 1000));
                long s1 = Stopwatch.GetTimestamp();
                QueryProcessCycleTime(self.Handle, out ulong r1);
                cyclesAtRest.Add((r1 - r0) / 1e6 / ((s1 - s0) / (double)Stopwatch.Frequency));
            }
            Report($"{name}: the process's megacycles per second while both stream", cyclesStreaming, runs, "");
            Report($"{name}: the process's kilocycles per URB answered", perUrb, runs, "");
            Report($"{name}: a reply's lateness past its stream's 10 ms grid, microseconds", lateness, runs, "");
            Report($"{name}: the process's megacycles per second attached at rest", cyclesAtRest, runs, "");
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(UsbipIndex);
        }
    }

    /// <summary>Each reply's time past its grid point: the earliest reply's
    /// offset from the grid is taken as zero, so the device's constant
    /// phase does not count.</summary>
    static void Lateness(long[] at, int count, long urbTicks, List<double> into)
    {
        if (count < 2) return;
        long min = long.MaxValue;
        for (int i = 0; i < count; i++) min = Math.Min(min, at[i] - at[0] - i * urbTicks);
        for (int i = 0; i < count; i++)
            into.Add((at[i] - at[0] - i * urbTicks - min) * 1e6 / Stopwatch.Frequency);
    }
}
