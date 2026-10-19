// idle-live [runs] [seconds] [profile]: the idle repeat's whole cost on the
// machine, live and elevated: one controller of the profile (dualsense-bt
// unless one is named) created through HMContext as a program creates it,
// one consumer state submitted and then nothing, and one reader, a child
// process of this probe reading the device's HID input reports as a game
// does. Over a window of [seconds] it reads the CPU cycles
// (QueryProcessCycleTime) of the SDK's process, of the reader, and of every
// WUDFHost process, which host the UMDF2 driver, and the reports the reader
// read. Each run prints one line:
//   idle-live,<run>,<sdk Mcycles/s>,<reader Mcycles/s>,<driver hosts Mcycles/s>,<reads/s>
// The same code built against the tree before lane DD-4 (-p:HmDd4Base=true)
// measures a dualsense-bt that does not repeat at rest. A script takes p50
// and p95 over 30 runs of each. WUDFHost processes are only read, never
// stopped: the sum includes every UMDF2 device on the machine, so the runs
// of the two trees alternate on one machine with the same devices attached.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class IdleLive
{
    const string DefaultId = "dualsense-bt";

    public static void Run(int runs, double seconds, string? profileId = null)
    {
        string id = profileId ?? DefaultId;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile(id) ?? throw new InvalidOperationException($"no {id}");
        for (int run = 1; run <= runs; run++)
        {
            using var c = ctx.CreateController(profile, Program.s_key);
            c.SubmitState(new HMGamepadState { Buttons = HMButton.A });
            // The controller's own input interface, found under its devnode:
            // a HID path carries no HIDMaestro name to search for.
            string? path = Program.OwnInputInterface(c);
            if (path == null) { Console.WriteLine($"idle-live,{run},no HID input interface under {id}'s devnode"); return; }
            using var reader = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                ArgumentList = { "idle-reader", path },
            })!;
            // Past the device's start and the tiered JIT's recompiles of the
            // repeat path, so the window reads the steady state.
            Thread.Sleep(3000);
            var hosts = Process.GetProcessesByName("WUDFHost");
            ulong self0 = Cycles(Process.GetCurrentProcess()), reader0 = Cycles(reader), hosts0 = Sum(hosts);
            reader.StandardInput.WriteLine("mark");
            long t0 = Stopwatch.GetTimestamp();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            ulong self1 = Cycles(Process.GetCurrentProcess()), reader1 = Cycles(reader), hosts1 = Sum(hosts);
            long t1 = Stopwatch.GetTimestamp();
            reader.StandardInput.WriteLine("mark");
            reader.StandardInput.Close();
            string? counted = reader.StandardOutput.ReadLine();
            // The reader ends on the second mark. One that does not is this
            // probe's own child, stopped here.
            if (!reader.WaitForExit(5000)) reader.Kill();
            double wall = (t1 - t0) / (double)Stopwatch.Frequency;
            long reads = long.TryParse(counted, out long n) ? n : -1;
            Console.WriteLine(FormattableString.Invariant(
                $"idle-live,{run},{(self1 - self0) / wall / 1e6:F3},{(reader1 - reader0) / wall / 1e6:F3},{(hosts1 - hosts0) / wall / 1e6:F3},{reads / wall:F1}"));
            foreach (var h in hosts) h.Dispose();
        }
    }

    /// <summary>The reader: input reports read as fast as the device gives
    /// them, counted between the two "mark" lines on stdin, the count printed
    /// once the second mark ends the reads. A device that sends nothing at
    /// rest leaves a read pending, which the second mark cancels.</summary>
    public static int Reader(string path)
    {
        using var h = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (h.IsInvalid) { Console.WriteLine("-1"); return 1; }
        using var stream = new FileStream(h, FileAccess.Read, 0, isAsync: true);
        using var stop = new CancellationTokenSource();
        int marks = 0;
        var input = new Thread(() =>
        {
            while (Console.ReadLine() is not null)
                if (Interlocked.Increment(ref marks) >= 2) break;
            Volatile.Write(ref marks, 2);
            stop.Cancel();
        }) { IsBackground = true };
        input.Start();
        long count = 0;
        var buffer = new byte[256];
        while (Volatile.Read(ref marks) < 2)
        {
            try
            {
                stream.ReadAsync(buffer, stop.Token).AsTask().GetAwaiter().GetResult();
                if (Volatile.Read(ref marks) == 1) count++;
            }
            catch (OperationCanceledException) { break; }
        }
        Console.WriteLine(count);
        return 0;
    }

    /// <summary>A process's CPU cycles. A process this run cannot read
    /// fails the run, rather than read as 0 and print a column of zeros.</summary>
    static ulong Cycles(Process p)
    {
        try
        {
            if (QueryProcessCycleTime(p.Handle, out ulong c)) return c;
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"idle-live cannot read the cycles of {p.ProcessName} ({p.Id}): {e.Message}", e);
        }
    }

    static ulong Sum(Process[] ps) => ps.Aggregate(0UL, (s, p) => s + Cycles(p));

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
}
