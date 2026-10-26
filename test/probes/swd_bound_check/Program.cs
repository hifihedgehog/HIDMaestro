// SWD helper bound check. No elevation, no device.
//
// SwdDeviceFactory runs hmswd.exe once per create or remove and waits for it
// by the steps it prints (driver\hmswd\hmswd.c, "Steps"): up to the SDK's
// bound, the helper's own 60 s wait and 30 s more scaled by
// HIDMAESTRO_TIMEOUT_SCALE, until the helper begins its close (STEP
// CLOSING), then up to 8 s, scaled, for the close. Until v2.1.0 a create
// stopped the helper at 15 s and ran it twice more, and a remove stopped it
// at 8 s wherever it stood. Here this probe, run with hmswd.exe's arguments,
// takes the helper's place (SwdDeviceFactory.HelperPathForTest) and plays a
// script of hmswd.exe's lines, exit code and pauses. The SDK reads the scale
// once, so each case runs in a child process of its own, all at once:
//
//   A. A create and a remove whose helper waits 20 s times the scale for
//      Plug and Play, past the old 15 s and 8 s and inside the bound: the
//      device is created, the remove counts as done, and each helper ran
//      once.
//   B1. At scale 0.1 (a 9 s bound, a 0.8 s close): a create whose helper
//      never reaches its close is stopped no sooner than the bound and
//      fails with WAIT_TIMEOUT and a cause that names the bound and the
//      step it stood at. The companion's exception
//      (DeviceOrchestrator.CompanionNotCreated) is an
//      InvalidOperationException with that cause and HRESULT, and the
//      registration gate's refusal keeps its own message and HRESULT. A
//      create whose helper printed OK and then hangs in its close succeeds
//      once the close's bound has passed.
//   B2. At scale 0.1: a remove whose helper never reaches its close is
//      stopped at the old 8 s (0.8 s), not the create's 90 s, fails with
//      WAIT_TIMEOUT and says the device stays. A remove whose helper
//      set the Handle lifetime and then hangs in its close, issue #18's
//      case, counts as done once the close's bound has passed, and says so.
//   C. hmswd.exe's exit codes as hmswd.c defines them, for a create (0 with
//      the instance id, also on the reuse fast path, 1, 2, 3, 4, 5 and a
//      crash) and a remove (0, 3, 5).
//   D. The deploy's pnputil adds (DriverBuilder): each gets as long as
//      Windows gives a device installation task, 240 s unless the policy
//      sets more. A tool still running at its bound is stopped and the run
//      says so, through DriverBuilder's runner and through PnputilHelper's,
//      which the WinUSB package's add uses, and a stopped add is a failure
//      that names the stop, the driver store's state and the next deploy.
//   E. The three call sites of a bounded pnputil add (the deploy's add with
//      /install, its add of a missing INF, the WinUSB package's add), each
//      driven with a stand-in pnputil: one that outlasts the old 30 s and
//      ends inside the bound is let finish, one that outruns the bound is
//      stopped and the site throws.
//
// Usage: SwdBoundCheck [scale]
//   scale  case A's timeout scale, 1 when not given (a 20 s helper)
// Exit 0 when every check passes, 1 when one fails.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using HIDMaestro.Internal;

internal static class Program
{
    const string Enumerator = "SWDBOUNDCHECK";
    const int WaitTimeoutHr = unchecked((int)0x80070102);
    const int E_FAIL = unchecked((int)0x80004005);
    const int E_INVALIDARG = unchecked((int)0x80070057);

    // The stand-in's script and the file it notes its runs in, read from its
    // environment, which it inherits from the case that runs it.
    const string ScriptVar = "SWDBOUND_SCRIPT", RunsVar = "SWDBOUND_RUNS";

    static int s_failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    }

    static int Main(string[] args)
    {
        // The stand-in, run by SwdDeviceFactory in hmswd.exe's place.
        if (args.Length > 0 && (args[0] == "create" || args[0] == "remove")) return StandIn(args);
        if (args.Length > 0 && args[0] == "/add-driver") return PnputilStandIn();
        if (args.Length == 2 && args[0] == "case") return Case(args[1]);

        double scale = 1;
        if (args.Length > 1
            || (args.Length == 1 && !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
            || scale < 0.1 || scale > 100)
        {
            Console.WriteLine("usage: SwdBoundCheck [scale], a scale from 0.1 to 100");
            return 1;
        }
        var cases = new (string Name, double Scale)[] { ("A", scale), ("B1", 0.1), ("B2", 0.1), ("C", 1), ("D", 1), ("E", 0.1) };
        var running = new List<(string Name, Process P, System.Threading.Tasks.Task<string> Out, System.Threading.Tasks.Task<string> Err)>();
        foreach (var (name, s) in cases)
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
            psi.Environment["HIDMAESTRO_TIMEOUT_SCALE"] = s.ToString(CultureInfo.InvariantCulture);
            var p = Process.Start(psi)!;
            running.Add((name, p, p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync()));
        }
        int failed = 0;
        foreach (var (name, p, stdout, stderr) in running)
        {
            bool ended = p.WaitForExit(600_000);
            if (!ended)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(10_000);
            }
            // Each case's helpers have ended by then, so nothing else holds
            // the pipes.
            System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 10_000);
            string text = stdout.IsCompletedSuccessfully ? stdout.Result : "";
            Console.Write(text);
            if (stderr.IsCompletedSuccessfully && stderr.Result.Length > 0) Console.Write(stderr.Result);
            if (!ended) Console.WriteLine($"  [FAIL] case {name} had not ended after 600 s and was stopped");
            else if (p.ExitCode != 0 && !text.Contains("[FAIL]", StringComparison.Ordinal))
                Console.WriteLine($"  [FAIL] case {name} exited with 0x{p.ExitCode:X8} before it reported a failed check");
            if (!ended || p.ExitCode != 0) failed++;
            p.Dispose();
        }
        Console.WriteLine($"swd_bound_check: {(failed == 0 ? "PASS" : $"{failed} case(s) FAIL")}");
        return failed == 0 ? 0 : 1;
    }

    static int Case(string name)
    {
        Console.WriteLine($"case {name}, timeout scale {TimeoutScale.Factor.ToString(CultureInfo.InvariantCulture)}");
        SwdDeviceFactory.HelperPathForTest = Environment.ProcessPath;
        switch (name)
        {
            case "A": CaseA(); break;
            case "B1": CaseB1(); break;
            case "B2": CaseB2(); break;
            case "C": CaseC(); break;
            case "D": CaseD(); break;
            case "E": CaseE(); break;
            default: Console.WriteLine($"  no case {name}"); return 1;
        }
        Console.Out.Flush();
        return s_failures == 0 ? 0 : 1;
    }

    static string S(int ms) => SwdDeviceFactory.Seconds(ms);

    static int Bound => TimeoutScale.Apply(SwdDeviceFactory.HelperBoundMs);
    static int Close => TimeoutScale.Apply(SwdDeviceFactory.HelperCloseMs);

    // hmswd.exe's lines as hmswd.c prints them, each case's script built
    // from them.
    const string Started = "out:STEP STARTED", Created = "out:STEP CREATED", Enumerated = "out:STEP ENUMERATED",
                 Ok = "out:OK {id}", Handle = "out:STEP LIFETIME HANDLE", Removed = "out:OK REMOVED";
    static string Closing(int code) => $"out:STEP CLOSING {code}";
    static string Sleep(int ms) => $"sleep:{ms}";
    static string Exit(int code) => $"exit:{code}";

    static string Script(params string[] actions) => string.Join(";", actions);

    static void CaseA()
    {
        int delay = TimeoutScale.Apply(20_000);
        int oldCreate = TimeoutScale.Apply(15_000), oldRemove = TimeoutScale.Apply(8_000);
        Check("A: the helper's wait is past the old create's 15 s and the old remove's 8 s, and inside the bound",
              delay > oldCreate && delay > oldRemove && delay < Bound,
              $"old create {S(oldCreate)}, old remove {S(oldRemove)}, helper {S(delay)}, bound {S(Bound)}");

        var (c, ms, runs) = CreateWith("SWDBOUND_A",
            Script(Started, Created, Sleep(delay), Enumerated, Ok, Closing(0), Exit(0)));
        Check($"A: a create whose helper waits {S(delay)} for Plug and Play -> created, the helper run once, no sooner than its answer",
              c.Success && c.InstanceId == $@"SWD\{Enumerator}\SWDBOUND_A" && c.Cause == null && runs.Count == 1 && ms >= delay,
              $"success {c.Success}, id {c.InstanceId ?? "none"}, 0x{c.HResult:X8}, {runs.Count} helper run(s), {ms} ms, cause \"{c.Cause}\"");

        // A remove is stopped at the old 8 s if its helper has not reached
        // STEP CLOSING (issue #18), so its wait for the reconnect stays under it.
        int rdelay = TimeoutScale.Apply(4_000);
        var (hr, cause, rms, rruns) = RemoveWith("SWDBOUND_AR",
            Script(Started, Created, Sleep(rdelay), Enumerated, Handle, Closing(0), Removed, Exit(0)));
        Check($"A: a remove whose helper waits {S(rdelay)} for the reconnect, inside the old {S(oldRemove)} -> done, the helper run once, no sooner than its answer",
              hr == 0 && cause.Length == 0 && rruns.Count == 1 && rms >= rdelay,
              $"0x{hr:X8}, {rruns.Count} helper run(s), {rms} ms, cause \"{cause}\"");
    }

    static void CaseB1()
    {
        var (r, ms, runs) = CreateWith("SWDBOUND_B", Script(Started, Created, Sleep(120_000), Exit(0)));
        bool gone = runs.Count == 1 && Gone(runs[0]);
        string expected = $"hmswd.exe was still running, waiting for Plug and Play to report the device enumerated, when the SDK's bound of {S(Bound)} ran out, " +
                          $"and was stopped (its own wait for Plug and Play is {S(SwdDeviceFactory.HelperWaitMs)}).";
        Check($"B1: a create whose helper never begins its close -> stopped and gone no sooner than the {S(Bound)} bound, WAIT_TIMEOUT, the cause names the bound and the step",
              !r.Success && r.HResult == WaitTimeoutHr && r.Cause == expected && ms >= Bound && runs.Count == 1 && gone,
              $"0x{r.HResult:X8}, {ms} ms, {runs.Count} helper run(s), gone {gone}, cause \"{r.Cause}\"");

        const string Id = @"SWD\HIDMAESTRO\SWDBOUND_B";
        var e = DeviceOrchestrator.CompanionNotCreated("XUSB companion", Id, "xbox-360-wired", 0, r);
        Check("B1: the companion's exception -> InvalidOperationException, the create's cause in its message, its HRESULT",
              e.GetType() == typeof(InvalidOperationException) && e.HResult == WaitTimeoutHr
              && e.Message == $"The XUSB companion {Id} of profile 'xbox-360-wired' at index 0 was not created: {expected}",
              $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}");
        var g = DeviceOrchestrator.CompanionNotCreated("XUSB companion", Id, "xbox-360-wired", 0, null);
        Check("B1: the registration gate's refusal -> its own message, InvalidOperationException's own HRESULT",
              g.GetType() == typeof(InvalidOperationException) && g.HResult == new InvalidOperationException().HResult
              && g.Message == $"The XUSB companion {Id} of profile 'xbox-360-wired' at index 0 was not created: " +
                              "a prior life of it or a device with its ParentIdPrefix was still there after its removal.",
              $"0x{g.HResult:X8}: {g.Message}");

        var (k, kms, kruns) = CreateWith("SWDBOUND_K",
            Script(Started, Created, Enumerated, Ok, Closing(0), Sleep(120_000), Exit(0)));
        bool kgone = kruns.Count == 1 && Gone(kruns[0]);
        Check($"B1: a create whose helper printed OK and hangs in its close -> created once the {S(Close)} close bound passed, the helper stopped",
              k.Success && k.InstanceId == $@"SWD\{Enumerator}\SWDBOUND_K" && kms >= Close && kruns.Count == 1 && kgone,
              $"success {k.Success}, id {k.InstanceId ?? "none"}, {kms} ms, {kruns.Count} helper run(s), gone {kgone}");
    }

    static void CaseB2()
    {
        var (hr, cause, ms, runs) = RemoveWith("SWDBOUND_BR", Script(Started, Created, Sleep(120_000), Exit(0)));
        bool gone = runs.Count == 1 && Gone(runs[0]);
        int oldRemoveBound = TimeoutScale.Apply(8_000);
        string expected = $"hmswd.exe was still running, waiting for Plug and Play to report the device enumerated, when the SDK's bound of {S(oldRemoveBound)} ran out, " +
                          "and was stopped. The device's lifetime is still ParentPresent, so it stays.";
        // Issue #18: a helper stuck before STEP CLOSING ends the remove at the
        // old 8 s (scaled), not at the create's 90 s.
        Check($"B2: a remove whose helper never reaches STEP CLOSING -> stopped and gone within the old {S(oldRemoveBound)} bound (not the create's {S(Bound)}), WAIT_TIMEOUT, the device stays",
              hr == WaitTimeoutHr && cause == expected && ms >= oldRemoveBound && ms < oldRemoveBound + TimeoutScale.Apply(5_000) + 3_000
              && ms < Bound && runs.Count == 1 && gone,
              $"0x{hr:X8}, {ms} ms, {runs.Count} helper run(s), gone {gone}, cause \"{cause}\"");

        var (khr, kcause, kms, kruns) = RemoveWith("SWDBOUND_KR",
            Script(Started, Created, Enumerated, Handle, Closing(0), Sleep(120_000), Removed, Exit(0)));
        bool kgone = kruns.Count == 1 && Gone(kruns[0]);
        string kexpected = $"Its SwDeviceClose had not returned after {S(Close)}, and it was stopped. " +
                           "The device's lifetime is Handle, so the end of the helper's process closes the handle, which removes the device.";
        Check($"B2: a remove whose helper set the Handle lifetime and hangs in its close (issue #18) -> done once the {S(Close)} close bound passed, the helper stopped",
              khr == 0 && kcause == kexpected && kms >= Close && kruns.Count == 1 && kgone,
              $"0x{khr:X8}, {kms} ms, {kruns.Count} helper run(s), gone {kgone}, cause \"{kcause}\"");
    }

    static void CaseC()
    {
        var creates = new (string Name, string Script, int Hr, string? Cause)[]
        {
            ("exit 0 with the instance id", Script(Started, Created, Enumerated, Ok, Closing(0), Exit(0)), 0, null),
            ("exit 0 on the reuse fast path, its lifetime left as it was",
             Script(Started, Created, "out:STEP LIFETIME UNCHANGED 0x80070032", Ok, Closing(0), Exit(0)), 0, null),
            ("exit 0 with no instance id", Script(Started, Created, Enumerated, "out:READY", Closing(0), Exit(0)), E_FAIL,
             "hmswd.exe exited with code 0 but printed no instance id (\"STEP STARTED | STEP CREATED | STEP ENUMERATED | READY | STEP CLOSING 0\")."),
            ("exit 1, arguments refused", Script("err:ERR BAD_GUID: x", Exit(1)), E_INVALIDARG,
             "hmswd.exe refused its arguments, exit code 1 (\"ERR BAD_GUID: x\")."),
            ("exit 2, SwDeviceCreate's HRESULT", Script(Started, "err:ERR SwDeviceCreate: 0x80070005", Exit(2)), unchecked((int)0x80070005),
             "SwDeviceCreate failed with 0x80070005 (hmswd.exe exit code 2)."),
            ("exit 3, the helper's own wait", Script(Started, Created, "err:ERR callback timeout", Closing(3), Exit(3)), WaitTimeoutHr,
             "Plug and Play did not report the device enumerated within hmswd.exe's own wait of 60 s, and hmswd.exe closed it (exit code 3)."),
            ("exit 4, the callback's HRESULT", Script(Started, Created, Enumerated, "err:ERR callback hr=0x8007001F", Closing(4), Exit(4)), unchecked((int)0x8007001F),
             "Plug and Play reported the device's creation failed with 0x8007001F (hmswd.exe exit code 4)."),
            ("exit 5, SwDeviceSetLifetime's HRESULT", Script(Started, Created, Enumerated, "err:ERR SwDeviceSetLifetime: 0x80070032", Closing(5), Exit(5)),
             unchecked((int)0x80070032), "SwDeviceSetLifetime failed with 0x80070032, and hmswd.exe closed the device (exit code 5)."),
            ("a crash, 0xC0000005", Script(Started, Created, Exit(unchecked((int)0xC0000005))), E_FAIL,
             "hmswd.exe exited with code 0xC0000005 (no output)."),
        };
        for (int n = 0; n < creates.Length; n++)
        {
            var c = creates[n];
            string suffix = $"SWDBOUND_C{n}";
            var (r, _, runs) = CreateWith(suffix, c.Script);
            bool ok = c.Cause == null
                ? r.Success && r.InstanceId == $@"SWD\{Enumerator}\{suffix}" && r.HResult == 0 && r.Cause == null
                : !r.Success && r.InstanceId == null && r.HResult == c.Hr && r.Cause == c.Cause;
            Check($"C: create, {c.Name} -> {(c.Cause == null ? "created" : $"0x{c.Hr:X8} and its cause")}", ok && runs.Count == 1,
                  $"success {r.Success}, id {r.InstanceId ?? "none"}, 0x{r.HResult:X8}, {runs.Count} helper run(s), cause \"{r.Cause}\"");
        }

        var removes = new (string Name, string Script, int Hr, string Cause)[]
        {
            ("exit 0, removed", Script(Started, Created, Enumerated, Handle, Closing(0), Removed, Exit(0)), 0, ""),
            ("exit 3, the reconnect's wait", Script(Started, Created, "err:ERR callback timeout", Closing(3), Exit(3)), WaitTimeoutHr,
             "Plug and Play did not answer the reconnect within hmswd.exe's own wait of 60 s (exit code 3). The device's lifetime is still ParentPresent, so it stays."),
            ("exit 5, SwDeviceSetLifetime's HRESULT", Script(Started, Created, Enumerated, "err:ERR SwDeviceSetLifetime: 0x80070032", Closing(5), Exit(5)),
             unchecked((int)0x80070032),
             "SwDeviceSetLifetime failed (hmswd.exe exit code 5, \"ERR SwDeviceSetLifetime: 0x80070032\"). The device's lifetime is still ParentPresent, so it stays."),
        };
        for (int n = 0; n < removes.Length; n++)
        {
            var c = removes[n];
            var (hr, cause, _, runs) = RemoveWith($"SWDBOUND_CR{n}", c.Script);
            Check($"C: remove, {c.Name} -> 0x{c.Hr:X8}{(c.Cause.Length > 0 ? " and its cause" : "")}",
                  hr == c.Hr && cause == c.Cause && runs.Count == 1,
                  $"0x{hr:X8}, {runs.Count} helper run(s), cause \"{cause}\"");
        }
    }

    static void CaseD()
    {
        int policy = 0;
        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Policies\Microsoft\Windows\DeviceInstall\Settings"))
            if (key?.GetValue("InstallTimeout") is int set) policy = set;
        int expected = Math.Max(240, policy) * 1000;
        int addMs = DriverBuilder.PnputilAddMs();
        Check("D: a pnputil add gets as long as Windows gives a device installation task",
              addMs == expected, $"{S(addMs)}, the policy {(policy == 0 ? "not set" : $"{policy} s")}");

        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
        var runBounded = typeof(DriverBuilder).GetMethod("RunBounded", Private);
        var addStopped = typeof(DriverBuilder).GetMethod("AddStopped", Private);
        if (runBounded == null || addStopped == null)
        {
            Check("D: DriverBuilder.RunBounded and AddStopped exist", false, "");
            return;
        }
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        int bound = TimeoutScale.Apply(1_000);
        var sw = Stopwatch.StartNew();
        var run = (ValueTuple<int, string, bool>)runBounded.Invoke(null, new object?[] { cmd, "/d /c ping -n 30 127.0.0.1 >nul", null, 1_000 })!;
        long ms = sw.ElapsedMilliseconds;
        Check($"D: a tool still running at its {S(bound)} bound -> stopped no sooner than the bound, and the run says so",
              run.Item3 && ms >= bound, $"stopped {run.Item3}, exit {run.Item1}, {ms} ms");
        var quick = (ValueTuple<int, string, bool>)runBounded.Invoke(null, new object?[] { cmd, "/d /c exit 3", null, 60_000 })!;
        Check("D: a tool that exits on its own -> not stopped, its exit code kept",
              !quick.Item3 && quick.Item1 == 3, $"stopped {quick.Item3}, exit {quick.Item1}");
        // The WinUSB package's add runs through PnputilHelper's runner.
        sw.Restart();
        var tool = PnputilHelper.RunToolBounded(cmd, "/d /c ping -n 30 127.0.0.1 >nul", 1_000);
        long toolMs = sw.ElapsedMilliseconds;
        Check($"D: PnputilHelper's runner, a tool still running at its {S(bound)} bound -> stopped no sooner than the bound, and the run says so",
              tool.stopped && toolMs >= bound, $"stopped {tool.stopped}, exit {tool.exitCode}, {toolMs} ms");
        var toolQuick = PnputilHelper.RunToolBounded(cmd, "/d /c exit 3", 60_000);
        Check("D: PnputilHelper's runner, a tool that exits on its own -> not stopped, its exit code kept",
              !toolQuick.stopped && toolQuick.exitCode == 3, $"stopped {toolQuick.stopped}, exit {toolQuick.exitCode}");

        var e = (InvalidOperationException)addStopped.Invoke(null, new object[] { "/add-driver hidmaestro.inf /install", "hidmaestro.inf", true })!;
        string m = e.Message;
        Check("D: a stopped add -> a failure naming the stop, the driver store's state, the earlier packages' removal and the next deploy",
              m.StartsWith($"pnputil /add-driver hidmaestro.inf /install was stopped after {expected / 1000} s, the time Windows gives a device installation task, before it finished. ", StringComparison.Ordinal)
              && (m.Contains("The driver store lists hidmaestro.inf ", StringComparison.Ordinal)
                  || m.Contains("The driver store does not list hidmaestro.inf ", StringComparison.Ordinal))
              && m.Contains("This deploy removed HIDMaestro's earlier packages before its adds. ", StringComparison.Ordinal)
              && m.EndsWith("The install record still names the payload installed before, so the next InstallDriver deploys again.", StringComparison.Ordinal),
              m);
    }

    const string PnpSleepVar = "SWDBOUND_PNP_SLEEP";

    /// <summary>pnputil's part for case E: sleeps the milliseconds in its
    /// environment, then exits 0.</summary>
    static int PnputilStandIn()
    {
        Thread.Sleep(int.Parse(Environment.GetEnvironmentVariable(PnpSleepVar) ?? "0", CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>The three call sites of a bounded pnputil add, each driven
    /// with a stand-in pnputil: the deploy's add with /install
    /// (AddWithInstall, which InstallDriversFrom calls under the PnP lock), the deploy's add of a missing INF
    /// (AddMissingInf) and the WinUSB package's add (AddOrThrow). A stand-in
    /// that runs past the 30 s the adds had until v2.1.0 and ends inside the
    /// bound must not be stopped (a call site that bounds at 30 s stops it).
    /// A stand-in that outruns the bound must be stopped and the site must
    /// throw (a call site that drops the throw returns).</summary>
    static void CaseE()
    {
        DriverBuilder.PnputilExeForTest = Environment.ProcessPath;
        int bound = TimeoutScale.Apply(DriverBuilder.PnputilAddMs());
        int old = TimeoutScale.Apply(30_000);
        int slow = old + 3_000, hang = bound + 20_000;
        Check("E: the stand-in's slow run is past the old 30 s bound and inside the new one",
              slow > old && slow < bound, $"old {S(old)}, slow {S(slow)}, bound {S(bound)}");

        string dir = Path.Combine(Path.GetTempPath(), $"swd_bound_check-E-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        foreach (string inf in PnputilHelper.HidMaestroInfNames) File.WriteAllText(Path.Combine(dir, inf), "; stand-in");
        string winusb = Path.Combine(dir, "winusb.inf");
        File.WriteAllText(winusb, "; stand-in");
        var sites = new (string Name, Action Call)[]
        {
            ("the deploy's add with /install", () => DriverBuilder.AddWithInstall(Path.Combine(dir, "hidmaestro.inf"), "hidmaestro.inf")),
            ("the deploy's add of a missing INF", () => DriverBuilder.AddMissingInf(dir, "hidmaestro.inf")),
            ("the WinUSB package's add", () => WinUsbBinding.AddOrThrow(winusb, "winusb.inf")),
        };
        foreach (var (name, call) in sites)
        {
            Environment.SetEnvironmentVariable(PnpSleepVar, slow.ToString(CultureInfo.InvariantCulture));
            var sw = Stopwatch.StartNew();
            string? slowEx = Thrown(call);
            long slowMs = sw.ElapsedMilliseconds;
            Check($"E: {name}, a pnputil that runs {S(slow)} -> let finish (not stopped)",
                  slowMs >= slow && (slowEx == null || !slowEx.Contains("was stopped", StringComparison.Ordinal)),
                  $"{slowMs} ms, thrown \"{slowEx}\"");

            Environment.SetEnvironmentVariable(PnpSleepVar, hang.ToString(CultureInfo.InvariantCulture));
            sw.Restart();
            string? hangEx = Thrown(call);
            long hangMs = sw.ElapsedMilliseconds;
            Check($"E: {name}, a pnputil that outruns the {S(bound)} bound -> stopped no sooner than the bound, and the site throws \"was stopped\"",
                  hangMs >= bound && hangMs < hang && hangEx != null && hangEx.Contains("was stopped after", StringComparison.Ordinal),
                  $"{hangMs} ms, thrown \"{hangEx}\"");
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    static string? Thrown(Action call)
    {
        try { call(); return null; }
        catch (Exception e) { return (e.InnerException ?? e).Message; }
    }

    /// <summary>One create through the stand-in playing
    /// <paramref name="script"/>. Returns the result, the create's time and
    /// the process id of each stand-in run.</summary>
    static (SwdDeviceFactory.Result Result, long Ms, List<int> Runs) CreateWith(string suffix, string script)
    {
        string runs = Prepare(suffix, script);
        var sw = Stopwatch.StartNew();
        var r = SwdDeviceFactory.Create(suffix, new[] { @"SWDBOUND\HW" }, Array.Empty<string>(),
                                        SwdDeviceFactory.ContainerIdFor(0), "SWD bound check", Enumerator);
        return (r, sw.ElapsedMilliseconds, Runs(runs));
    }

    /// <summary>One remove through the stand-in playing
    /// <paramref name="script"/>.</summary>
    static (int Hr, string Cause, long Ms, List<int> Runs) RemoveWith(string suffix, string script)
    {
        string runs = Prepare(suffix, script);
        var sw = Stopwatch.StartNew();
        int hr = SwdDeviceFactory.RemoveWith(Enumerator, suffix, SwdDeviceFactory.ContainerIdFor(0), new[] { @"SWDBOUND\HW" },
                                             Array.Empty<string>(), "SWD bound check", out string cause);
        return (hr, cause, sw.ElapsedMilliseconds, Runs(runs));
    }

    static string Prepare(string suffix, string script)
    {
        string runs = Path.Combine(Path.GetTempPath(), $"swd_bound_check-{Environment.ProcessId}-{suffix}.txt");
        File.Delete(runs);
        Environment.SetEnvironmentVariable(ScriptVar, script);
        Environment.SetEnvironmentVariable(RunsVar, runs);
        return runs;
    }

    static List<int> Runs(string runs)
    {
        var pids = new List<int>();
        if (!File.Exists(runs)) return pids;
        foreach (string line in File.ReadAllLines(runs))
            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) pids.Add(pid);
        File.Delete(runs);
        return pids;
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

    /// <summary>hmswd.exe's part: <c>create|remove &lt;enumerator&gt;
    /// &lt;suffix&gt; &lt;container&gt; &lt;hardware ids&gt; &lt;compatible
    /// ids&gt; &lt;description&gt;</c>, answered by the script in its
    /// environment: <c>out:</c> and <c>err:</c> lines, flushed at once as
    /// hmswd.exe flushes its steps, with <c>{id}</c> standing for the
    /// instance id, <c>sleep:</c> pauses in milliseconds and the
    /// <c>exit:</c> code. It notes its process id first, so a run that is
    /// stopped is counted.</summary>
    static int StandIn(string[] args)
    {
        if (Environment.GetEnvironmentVariable(RunsVar) is string runs)
            File.AppendAllText(runs, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        string id = args.Length >= 3 ? $@"SWD\{args[1]}\{args[2]}" : "";
        foreach (string action in (Environment.GetEnvironmentVariable(ScriptVar) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = action.IndexOf(':');
            string verb = action[..colon], arg = action[(colon + 1)..];
            switch (verb)
            {
                case "out": Console.Out.WriteLine(arg.Replace("{id}", id)); Console.Out.Flush(); break;
                case "err": Console.Error.WriteLine(arg); Console.Error.Flush(); break;
                case "sleep": Thread.Sleep(int.Parse(arg, CultureInfo.InvariantCulture)); break;
                case "exit": return int.Parse(arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
        }
        return 0;
    }
}
