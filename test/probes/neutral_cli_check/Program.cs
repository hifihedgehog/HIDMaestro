// End-to-end CLI neutral-input override check.
//
// Runs HIDMaestroTest.exe emulate, sends the interactive command
// "neutral on/off/toggle" through stdin, waits for the app's [ACK], and reads
// XInput externally. This verifies the user-facing CLI path, not just the SDK
// property.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal sealed class Program
{
    private const uint ERROR_SUCCESS = 0;
    private static int s_total;
    private static int s_failures;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    private static void Check(string label, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    private static string FormatState(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return $"pkt={s.dwPacketNumber} btn=0x{g.wButtons:X4} LT={g.bLeftTrigger} RT={g.bRightTrigger} " +
               $"LX={g.sThumbLX} LY={g.sThumbLY} RX={g.sThumbRX} RY={g.sThumbRY}";
    }

    private static HashSet<int> ConnectedSlots()
    {
        var result = new HashSet<int>();
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == ERROR_SUCCESS) result.Add((int)i);
        return result;
    }

    private static int WaitForNewSlot(HashSet<int> baseline, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            for (uint i = 0; i < 4; i++)
            {
                if (baseline.Contains((int)i)) continue;
                if (XInputGetState(i, out _) == ERROR_SUCCESS) return (int)i;
            }
            Thread.Sleep(50);
        }
        return -1;
    }

    private static bool IsNeutral(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return g.wButtons == 0
            && g.bLeftTrigger == 0
            && g.bRightTrigger == 0
            && Math.Abs((int)g.sThumbLX) <= 2
            && Math.Abs((int)g.sThumbLY) <= 2
            && Math.Abs((int)g.sThumbRX) <= 2
            && Math.Abs((int)g.sThumbRY) <= 2;
    }

    private static bool IsActive(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return g.wButtons != 0
            || g.bLeftTrigger > 8
            || g.bRightTrigger > 8
            || Math.Abs((int)g.sThumbLX) > 4000
            || Math.Abs((int)g.sThumbLY) > 4000
            || Math.Abs((int)g.sThumbRX) > 4000
            || Math.Abs((int)g.sThumbRY) > 4000;
    }

    private static bool WaitForActive(int slot, string label, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        XINPUT_STATE last = default;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (XInputGetState((uint)slot, out last) == ERROR_SUCCESS && IsActive(in last))
            {
                Check(label, true, FormatState(in last));
                return true;
            }
            Thread.Sleep(80);
        }
        Check(label, false, $"last={FormatState(in last)}");
        return false;
    }

    private static bool AssertNeutralStable(int slot, string label, int durationMs)
    {
        var sw = Stopwatch.StartNew();
        int samples = 0;
        XINPUT_STATE last = default;
        while (sw.ElapsedMilliseconds < durationMs)
        {
            samples++;
            if (XInputGetState((uint)slot, out last) != ERROR_SUCCESS || !IsNeutral(in last))
            {
                Check(label, false, $"sample={samples} state={FormatState(in last)}");
                return false;
            }
            Thread.Sleep(60);
        }
        Check(label, true, $"{samples} neutral samples; last={FormatState(in last)}");
        return true;
    }

    private sealed class EmulateProcess : IDisposable
    {
        public required Process Process { get; init; }
        public required ConcurrentQueue<string> Output { get; init; }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    try { Send("quit", 30_000); } catch { }
                    if (!Process.WaitForExit(60_000))
                    {
                        Process.Kill(entireProcessTree: true);
                        Process.WaitForExit(5_000);
                    }
                }
            }
            finally
            {
                Process.Dispose();
            }
        }

        public void Send(string command, int timeoutMs)
        {
            if (Process.HasExited)
                throw new InvalidOperationException($"Cannot send '{command}': process exited {Process.ExitCode}");

            Console.WriteLine($"  [stdin] {command}");
            Process.StandardInput.WriteLine(command);
            Process.StandardInput.Flush();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                while (Output.TryDequeue(out var line))
                {
                    if (line.Trim() == "[ACK]") return;
                }
                if (Process.HasExited) return;
                Thread.Sleep(50);
            }
            throw new TimeoutException($"Timed out waiting for [ACK] after '{command}'");
        }
    }

    private static EmulateProcess StartEmulate(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "emulate " + string.Join(" ", args),
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var output = new ConcurrentQueue<string>();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue("[stderr] " + e.Data); };
        if (!proc.Start()) throw new InvalidOperationException("failed to start HIDMaestroTest");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        Console.WriteLine($"  started pid={proc.Id}: {psi.FileName} {psi.Arguments}");
        return new EmulateProcess { Process = proc, Output = output };
    }

    private static string ResolveTestExe(string[] args)
    {
        if (args.Length > 0) return Path.GetFullPath(args[0]);

        string baseDir = AppContext.BaseDirectory;
        string tfm = "net10.0-windows10.0.26100.0";
        return Path.GetFullPath(Path.Combine(baseDir,
            "..", "..", "..", "..", "..", "..",
            "bin", "Release", tfm, "win-x64", "HIDMaestroTest.exe"));
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro neutral-input CLI regression ===");
        string testExe = ResolveTestExe(args);
        Console.WriteLine($"  HIDMaestroTest: {testExe}");
        if (!File.Exists(testExe))
        {
            Console.WriteLine("  FAIL: HIDMaestroTest.exe not found. Build test/HIDMaestroTest.csproj first.");
            return 2;
        }

        RunProcess(testExe, "cleanup");
        Thread.Sleep(500);
        var baseline = ConnectedSlots();
        Console.WriteLine($"  baseline XInput slots: {(baseline.Count == 0 ? "(none)" : string.Join(",", baseline))}");

        using (var emu = StartEmulate(testExe, "--rate-hz", "100", "xbox-360-wired"))
        {
            int slot = WaitForNewSlot(baseline, timeoutMs: 20_000);
            if (slot < 0)
            {
                Check("virtual claims a new XInput slot", false);
                return 1;
            }
            Check("virtual claims a new XInput slot", true, $"slot={slot}");

            WaitForActive(slot, "startup pattern is active", timeoutMs: 6_000);

            emu.Send("neutral on all", 20_000);
            AssertNeutralStable(slot, "CLI neutral on keeps slot connected and idle", durationMs: 1_800);

            emu.Send("neutral off all", 20_000);
            WaitForActive(slot, "CLI neutral off restores active input", timeoutMs: 6_000);

            emu.Send("neutral toggle all", 20_000);
            AssertNeutralStable(slot, "CLI neutral toggle enables neutral", durationMs: 1_200);

            emu.Send("neutral toggle all", 20_000);
            WaitForActive(slot, "CLI neutral toggle disables neutral", timeoutMs: 6_000);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} PASS ===");
        return s_failures == 0 ? 0 : 1;
    }

    private static void RunProcess(string exe, string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"failed to start {exe}");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} {arguments} exited {proc.ExitCode}");
    }
}
