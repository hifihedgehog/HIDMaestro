// SWD create bound check. No elevation, no device.
//
// SwdDeviceFactory.Create runs hmswd.exe once and waits for its answer up to
// the SDK's bound: the helper's own 60 s wait and 30 s more, scaled by
// HIDMAESTRO_TIMEOUT_SCALE. Until v2.1.0 it stopped the helper at 15 s and
// ran it twice more. Here this probe, run with hmswd.exe's arguments, takes
// the helper's place (SwdDeviceFactory.HelperPathForTest) and answers as
// hmswd.exe does, after a delay. The SDK reads the scale once, so each case
// runs in a child process of its own:
//
//   A. A helper that answers after 20 s times the scale, past the old
//      attempt's 15 s and inside the bound: the device is created, and the
//      helper ran once.
//   B. A helper still running at the bound, at scale 0.1 (9 s): stopped and
//      gone, the create failed no sooner than the bound, with WAIT_TIMEOUT
//      and a cause that names the SDK's bound. The companion's exception
//      (DeviceOrchestrator.CompanionNotCreated) is an
//      InvalidOperationException with that cause and HRESULT, and the
//      registration gate's refusal keeps its own message and HRESULT.
//   C. hmswd.exe's exit codes as driver\hmswd\hmswd.c defines them: 0 with
//      the instance id, 1 for arguments it refused, 2 with SwDeviceCreate's
//      HRESULT, 3 for the helper's own wait, 4 with the callback's HRESULT,
//      and any other code a failure that names it.
//
// Usage: SwdBoundCheck [scale]
//   scale  case A's timeout scale, 1 when not given (a 20 s helper)
// Exit 0 when every check passes, 1 when one fails.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using HIDMaestro.Internal;

internal static class Program
{
    const string Enumerator = "SWDBOUNDCHECK";
    const int WaitTimeoutHr = unchecked((int)0x80070102);
    const int E_FAIL = unchecked((int)0x80004005);
    const int E_INVALIDARG = unchecked((int)0x80070057);

    // What the stand-in does, read from its environment, which it inherits
    // from the case that runs the create.
    const string DelayVar = "SWDBOUND_DELAY_MS", ExitVar = "SWDBOUND_EXIT",
                 OutVar = "SWDBOUND_OUT", ErrVar = "SWDBOUND_ERR", RunsVar = "SWDBOUND_RUNS";

    static int s_failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    }

    static int Main(string[] args)
    {
        // The stand-in, run by SwdDeviceFactory.Create in hmswd.exe's place.
        if (args.Length > 0 && args[0] == "create") return StandIn(args);
        if (args.Length == 2 && args[0] == "case") return Case(args[1]);

        double scale = 1;
        if (args.Length > 1
            || (args.Length == 1 && !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            || scale < 0.1 || scale > 100)
        {
            Console.WriteLine("usage: SwdBoundCheck [scale], a scale from 0.1 to 100");
            return 1;
        }
        int failed = RunCase("A", scale) + RunCase("B", 0.1) + RunCase("C", 1);
        Console.WriteLine($"swd_bound_check: {(failed == 0 ? "PASS" : $"{failed} case(s) FAIL")}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>One case in a child process with its own timeout scale. Its
    /// check lines are written to this process's output once it ends.</summary>
    static int RunCase(string name, double scale)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("case");
        psi.ArgumentList.Add(name);
        psi.Environment["HIDMAESTRO_TIMEOUT_SCALE"] = scale.ToString(CultureInfo.InvariantCulture);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        bool ended = p.WaitForExit(600_000);
        if (!ended)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.WaitForExit(10_000);
        }
        // Each case's helpers have ended by then, so nothing else holds the
        // pipes.
        System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 10_000);
        if (stdout.IsCompletedSuccessfully) Console.Write(stdout.Result);
        if (stderr.IsCompletedSuccessfully && stderr.Result.Length > 0) Console.Write(stderr.Result);
        if (!ended)
        {
            Console.WriteLine($"  [FAIL] case {name} had not ended after 600 s and was stopped");
            return 1;
        }
        if (p.ExitCode != 0 && !(stdout.IsCompletedSuccessfully && stdout.Result.Contains("[FAIL]", StringComparison.Ordinal)))
            Console.WriteLine($"  [FAIL] case {name} exited with 0x{p.ExitCode:X8} before it reported a failed check");
        return p.ExitCode == 0 ? 0 : 1;
    }

    static int Case(string name)
    {
        Console.WriteLine($"case {name}, timeout scale {TimeoutScale.Factor.ToString(CultureInfo.InvariantCulture)}");
        SwdDeviceFactory.HelperPathForTest = Environment.ProcessPath;
        switch (name)
        {
            case "A": CaseA(); break;
            case "B": CaseB(); break;
            case "C": CaseC(); break;
            default: Console.WriteLine($"  no case {name}"); return 1;
        }
        Console.Out.Flush();
        return s_failures == 0 ? 0 : 1;
    }

    static string S(int ms) => SwdDeviceFactory.Seconds(ms);

    static void CaseA()
    {
        int oldAttempt = TimeoutScale.Apply(15_000);
        int bound = TimeoutScale.Apply(SwdDeviceFactory.HelperBoundMs);
        int delay = TimeoutScale.Apply(20_000);
        Check("A: the helper's delay is past the old attempt's bound and inside the create's",
              delay > oldAttempt && delay < bound,
              $"old attempt {S(oldAttempt)}, helper {S(delay)}, bound {S(bound)}");
        var (r, ms, runs) = CreateWith("SWDBOUND_A", delay, 0, null, null);
        Check($"A: a helper that answers after {S(delay)} -> created, the helper run once, the create no sooner than its answer",
              r.Success && r.InstanceId == $@"SWD\{Enumerator}\SWDBOUND_A" && r.Cause == null && runs.Count == 1 && ms >= delay,
              $"success {r.Success}, id {r.InstanceId ?? "none"}, 0x{r.HResult:X8}, {runs.Count} helper run(s), {ms} ms, cause \"{r.Cause}\"");
    }

    static void CaseB()
    {
        int bound = TimeoutScale.Apply(SwdDeviceFactory.HelperBoundMs);
        var (r, ms, runs) = CreateWith("SWDBOUND_B", 120_000, 0, null, null);
        bool gone = runs.Count == 1 && Gone(runs[0]);
        string expected = $"hmswd.exe was still running when the SDK's bound of {S(bound)} ran out, and was stopped " +
                          $"(its own wait for Plug and Play is {S(SwdDeviceFactory.HelperWaitMs)}).";
        Check($"B: a helper still running at the {S(bound)} bound -> stopped and gone, the create failed no sooner than the bound, WAIT_TIMEOUT, the cause names the bound",
              !r.Success && r.HResult == WaitTimeoutHr && r.Cause == expected && ms >= bound && runs.Count == 1 && gone,
              $"0x{r.HResult:X8}, {ms} ms, {runs.Count} helper run(s), gone {gone}, cause \"{r.Cause}\"");

        const string Id = @"SWD\HIDMAESTRO\SWDBOUND_B";
        var e = DeviceOrchestrator.CompanionNotCreated("XUSB companion", Id, "xbox-360-wired", 0, r);
        Check("B: the companion's exception -> InvalidOperationException, the create's cause in its message, its HRESULT",
              e.GetType() == typeof(InvalidOperationException) && e.HResult == WaitTimeoutHr
              && e.Message == $"The XUSB companion {Id} of profile 'xbox-360-wired' at index 0 was not created: {expected}",
              $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}");
        var g = DeviceOrchestrator.CompanionNotCreated("XUSB companion", Id, "xbox-360-wired", 0, null);
        Check("B: the registration gate's refusal -> its own message, InvalidOperationException's own HRESULT",
              g.GetType() == typeof(InvalidOperationException) && g.HResult == new InvalidOperationException().HResult
              && g.Message == $"The XUSB companion {Id} of profile 'xbox-360-wired' at index 0 was not created: " +
                              "a prior life of it or a device with its ParentIdPrefix was still there after its removal.",
              $"0x{g.HResult:X8}: {g.Message}");
    }

    static void CaseC()
    {
        var cases = new (string Name, int Exit, string? Out, string? Err, int Hr, string? Cause)[]
        {
            ("exit 0 with the instance id", 0, null, null, 0, null),
            ("exit 0 with no instance id", 0, "READY", null, E_FAIL,
             "hmswd.exe exited with code 0 but printed no instance id (\"READY\")."),
            ("exit 1, arguments refused", 1, null, "ERR BAD_GUID: x", E_INVALIDARG,
             "hmswd.exe refused its arguments, exit code 1 (\"ERR BAD_GUID: x\")."),
            ("exit 2, SwDeviceCreate's HRESULT", 2, null, "ERR SwDeviceCreate: 0x80070005", unchecked((int)0x80070005),
             "SwDeviceCreate failed with 0x80070005 (hmswd.exe exit code 2)."),
            ("exit 3, the helper's own wait", 3, null, "ERR callback timeout", WaitTimeoutHr,
             "Plug and Play did not report the device enumerated within hmswd.exe's own wait of 60 s, and hmswd.exe closed it (exit code 3)."),
            ("exit 4, the callback's HRESULT", 4, null, "ERR callback hr=0x8007001F", unchecked((int)0x8007001F),
             "Plug and Play reported the device's creation failed with 0x8007001F (hmswd.exe exit code 4)."),
            ("exit 5, a code no create returns", 5, null, "ERR SwDeviceSetLifetime: 0x80070032", E_FAIL,
             "hmswd.exe exited with code 0x00000005 (\"ERR SwDeviceSetLifetime: 0x80070032\")."),
            ("a crash, 0xC0000005", unchecked((int)0xC0000005), null, null, E_FAIL,
             "hmswd.exe exited with code 0xC0000005 (no output)."),
        };
        for (int n = 0; n < cases.Length; n++)
        {
            var c = cases[n];
            string suffix = $"SWDBOUND_C{n}";
            var (r, _, runs) = CreateWith(suffix, 0, c.Exit, c.Out, c.Err);
            bool ok = c.Cause == null
                ? r.Success && r.InstanceId == $@"SWD\{Enumerator}\{suffix}" && r.HResult == 0 && r.Cause == null
                : !r.Success && r.InstanceId == null && r.HResult == c.Hr && r.Cause == c.Cause;
            Check($"C: {c.Name} -> {(c.Cause == null ? "created" : $"0x{c.Hr:X8} and its cause")}", ok && runs.Count == 1,
                  $"success {r.Success}, id {r.InstanceId ?? "none"}, 0x{r.HResult:X8}, {runs.Count} helper run(s), cause \"{r.Cause}\"");
        }
    }

    /// <summary>One create through the stand-in, which waits
    /// <paramref name="delayMs"/>, prints <paramref name="stdout"/> (the
    /// instance id when null and the exit code is 0) and
    /// <paramref name="stderr"/>, and exits with <paramref name="exitCode"/>.
    /// Returns the result, the create's time and the process id of each
    /// stand-in run.</summary>
    static (SwdDeviceFactory.Result Result, long Ms, List<int> Runs) CreateWith(string suffix, int delayMs, int exitCode,
                                                                               string? stdout, string? stderr)
    {
        string runs = Path.Combine(Path.GetTempPath(), $"swd_bound_check-{Environment.ProcessId}-{suffix}.txt");
        File.Delete(runs);
        Environment.SetEnvironmentVariable(DelayVar, delayMs.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(ExitVar, exitCode.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(OutVar, stdout);
        Environment.SetEnvironmentVariable(ErrVar, stderr);
        Environment.SetEnvironmentVariable(RunsVar, runs);
        var sw = Stopwatch.StartNew();
        var r = SwdDeviceFactory.Create(suffix, new[] { @"SWDBOUND\HW" }, Array.Empty<string>(),
                                        SwdDeviceFactory.ContainerIdFor(0), "SWD bound check", Enumerator);
        long ms = sw.ElapsedMilliseconds;
        var pids = new List<int>();
        if (File.Exists(runs))
        {
            foreach (string line in File.ReadAllLines(runs))
                if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) pids.Add(pid);
            File.Delete(runs);
        }
        return (r, ms, pids);
    }

    /// <summary>Whether a stand-in run has ended: no process of its id, or
    /// one that has exited, or the id now another program's.</summary>
    static bool Gone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited || !string.Equals(p.ProcessName, "SwdBoundCheck", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>hmswd.exe's part: <c>create &lt;enumerator&gt; &lt;suffix&gt;
    /// &lt;container&gt; &lt;hardware ids&gt; &lt;compatible ids&gt;
    /// &lt;description&gt;</c>, answered as the environment says. It notes
    /// its process id first, so a run that is stopped is counted.</summary>
    static int StandIn(string[] args)
    {
        if (Environment.GetEnvironmentVariable(RunsVar) is string runs)
            File.AppendAllText(runs, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        int delay = int.Parse(Environment.GetEnvironmentVariable(DelayVar) ?? "0", CultureInfo.InvariantCulture);
        if (delay > 0) Thread.Sleep(delay);
        int exit = int.Parse(Environment.GetEnvironmentVariable(ExitVar) ?? "0", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (Environment.GetEnvironmentVariable(OutVar) is string stdout) Console.Out.WriteLine(stdout);
        else if (exit == 0 && args.Length >= 3) Console.Out.WriteLine($@"OK SWD\{args[1]}\{args[2]}");
        if (Environment.GetEnvironmentVariable(ErrVar) is string stderr) Console.Error.WriteLine(stderr);
        return exit;
    }
}
