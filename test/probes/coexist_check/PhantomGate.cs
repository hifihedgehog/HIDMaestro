// `CoexistCheck.exe phantomgate`: the phantom cleanup's hold of
// OwnerSweep.CreateGate, its waits and the exit handler's join of it, with no
// device and no elevation. The cleanup runs its real path,
// OwnerSweep.RunPhantomCleanup into DeviceManager.RemoveAccumulatedHmPhantoms
// and the uninstall wait, and only the phantoms and Windows' answer about
// them stand in: DeviceManager.PhantomSource gives ids, named older than
// identity tokens so that no claim is needed, with the call that stands for
// each SetupDiRemoveDevice, and DeviceManager.RemovalLocate answers in place
// of the locates in DeviceManager.WaitRemovalComplete.
//
//   - the cleanup waits on a phantom it uninstalled until the locate stops
//     finding it, and never on one whose uninstall failed, and the gate is
//     held from the uninstall until then,
//   - the waits of one run share one budget, so three ids that stay known
//     cost the budget once,
//   - a process that exits while its cleanup runs waits for it within the
//     exit handler's one budget. The control is the same work on a plain
//     background thread, which the runtime abandons at exit. A cleanup
//     whose uninstall is longer than the budget ends the wait at the
//     budget, and the handler logs that the cleanup held the gate. Once the
//     exit starts the cleanup begins no further uninstall, and a phantom
//     that stays known costs the exit the cleanup's waiting budget, which
//     ends inside the exit's. A cleanup still waiting for the gate behind
//     a create in flight ends the wait at the budget too, and the handler
//     logs the create as the cause.
//   - a cleanup started inside the program's own ProcessExit handler,
//     whose claim registers the SDK's handler too late to run: after a call
//     into the SDK before exit began, the call that starts it runs it to its
//     end before returning (OwnerSweep.StartPhantomCleanupOnce,
//     ProcessExitSignal). As the program's first call into the SDK, which
//     only .NET leaves its module initializer to, the cleanup's uninstall
//     begins and the process ends inside it, as the SDK documents. These children call
//     StartPhantomCleanupOnce, where that choice is made: unelevated,
//     RemoveAllVirtualControllers stops at its claim.
//   - a copy of the SDK loaded into a collectible load context and called
//     once, which runs its module initializer, lets the context unload:
//     the handler the initializer puts on the default context's Unloading
//     is gone after the Unload, and the context is collected.
//   - the uninstall-grade sweep (RemoveAllVirtualControllers with
//     HMCleanupScope.Everything) acts only once the cleanup has let go of
//     the gate. A hook stops it there, before it acts on anything.
//
// Every bound is read through the SDK's TimeoutScale, so the probe holds
// under HIDMAESTRO_TIMEOUT_SCALE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;

internal static partial class Program
{
    const BindingFlags AnyStatic = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    // Names older than identity tokens: the cleanup takes no claim for them.
    const string FakeId = @"ROOT\HIDMAESTRO\7E57", FailingId = @"ROOT\HIDMAESTRO\7E58";

    static Type SdkType(string name) => typeof(HMContext).Assembly.GetType("HIDMaestro.Internal." + name)!;

    /// <summary>A wall-clock bound as the SDK scales it.</summary>
    static int Scaled(int ms) => (int)SdkType("TimeoutScale").GetMethod("Apply", AnyStatic, new[] { typeof(int) })!.Invoke(null, new object[] { ms })!;

    static int SdkConst(string type, string name) => (int)SdkType(type).GetField(name, AnyStatic)!.GetRawConstantValue()!;

    static void SetSource(IEnumerable<(string, Func<bool>)>? phantoms)
    {
        SdkType("DeviceManager").GetField("PhantomSource", AnyStatic)!.SetValue(null,
            phantoms == null ? null : (Func<IEnumerable<(string Id, Func<bool> Uninstall)>>)(() => phantoms));
        // Each uninstall holds the PnP lock, which lives in an
        // administrators-only namespace. With no device and no elevation a
        // lock of this process stands in, when the SDK has one.
        SdkType("DeviceManager").GetField("PnpLockHook", AnyStatic)?.SetValue(null,
            phantoms == null ? null : (Func<IDisposable>)(() => new StandInLock()));
    }

    /// <summary>The stand-in for the PnP lock: one process-wide monitor.</summary>
    sealed class StandInLock : IDisposable
    {
        static readonly object s_gate = new();
        public StandInLock() => Monitor.Enter(s_gate);
        public void Dispose() => Monitor.Exit(s_gate);
    }

    static void SetLocate(Func<string, bool>? locate) =>
        SdkType("DeviceManager").GetField("RemovalLocate", AnyStatic)!.SetValue(null, locate);

    static int PhantomGate()
    {
        Console.WriteLine("=== The phantom cleanup's gate, waits and exit join ===");
        Type sweep = SdkType("OwnerSweep");
        if (SdkType("DeviceManager").GetField("PhantomSource", AnyStatic) == null || SdkType("DeviceManager").GetField("RemovalLocate", AnyStatic) == null
            || sweep.GetMethod("RunPhantomCleanup", AnyStatic) == null)
        {
            Check("the SDK has the phantom cleanup seams", false, "this build has none");
            return 1;
        }
        var gate = (ReaderWriterLockSlim)sweep.GetField("CreateGate", AnyStatic)!.GetValue(null)!;
        var run = (Action)Delegate.CreateDelegate(typeof(Action), sweep.GetMethod("RunPhantomCleanup", AnyStatic)!);
        int budget = Scaled(SdkConst("OwnerSweep", "UninstallWaitBudgetMs"));
        int exitBudget = Scaled(SdkConst("OwnerSweep", "ExitWaitMs"));
        Console.WriteLine($"  waiting budget {budget} ms, exit budget {exitBudget} ms");

        // One phantom whose uninstall succeeds and stays known for three
        // locates, and one whose uninstall fails. The probe takes the read
        // lock every few ms and keeps the times it got it.
        var clock = Stopwatch.StartNew();
        long uninstallAt = -1, goneAt = -1;
        int fakeLocates = 0, failingLocates = 0;
        SetSource(new (string, Func<bool>)[]
        {
            (FakeId, () => { Interlocked.Exchange(ref uninstallAt, clock.ElapsedTicks); return true; }),
            (FailingId, () => false),
        });
        SetLocate(id =>
        {
            if (id == FailingId) { Interlocked.Increment(ref failingLocates); return false; }
            bool known = Interlocked.Increment(ref fakeLocates) <= 3;
            if (!known) Interlocked.Exchange(ref goneAt, clock.ElapsedTicks);
            return known;
        });
        var acquired = new List<long>();
        var cleanup = new Thread(() => run());
        cleanup.Start();
        while (cleanup.IsAlive)
        {
            if (gate.TryEnterReadLock(0))
            {
                // Taken while the read lock is held: no writer holds the gate.
                acquired.Add(clock.ElapsedTicks);
                gate.ExitReadLock();
            }
            Thread.Sleep(2);
        }
        cleanup.Join();
        bool after = gate.TryEnterReadLock(0);
        if (after) gate.ExitReadLock();
        object? report = sweep.GetProperty("LastPhantomCleanup", AnyStatic)!.GetValue(null);
        Check("the cleanup waited on the phantom it uninstalled until the locate stopped finding it",
              fakeLocates == 4 && goneAt > uninstallAt && uninstallAt > 0, $"{fakeLocates} locates");
        Check("and never on the one whose uninstall failed", failingLocates == 0, $"{failingLocates} locates");
        int inside = acquired.FindIndex(t => t > uninstallAt && t < goneAt);
        Check("the gate was held from the uninstall until the locate stopped finding the id",
              uninstallAt > 0 && goneAt > 0 && inside < 0,
              inside < 0 ? $"{acquired.Count} read locks taken outside it" : $"read lock taken at {acquired[inside] * 1000 / Stopwatch.Frequency} ms");
        Check("control: the probe takes the read lock once the run ends", after);
        Check("the run reports one removal, waited for and settled",
              report != null && Prop(report, "Removed") == "1" && Prop(report, "Ids") == "1" && Prop(report, "Unsettled") == "0",
              report?.ToString() ?? "no report");

        // Three phantoms that stay known: the run's waits end when the one
        // budget is spent, not after three.
        var stayKnown = new (string, Func<bool>)[]
        {
            (@"ROOT\HIDMAESTRO\7E59", () => true), (@"ROOT\HIDMAESTRO\7E5A", () => true), (@"ROOT\HIDMAESTRO\7E5B", () => true),
        };
        SetSource(stayKnown);
        SetLocate(_ => true);
        var sw = Stopwatch.StartNew();
        run();
        long ms = sw.ElapsedMilliseconds;
        report = sweep.GetProperty("LastPhantomCleanup", AnyStatic)!.GetValue(null);
        Check("three ids that stay known spend one waiting budget in all, reported unsettled",
              ms >= budget && ms < 2L * budget && report != null && Prop(report, "Unsettled") == "3",
              $"{ms} ms, budget {budget} ms, {report}");
        Check("the cleanup's waiting budget ends inside the exit's", budget < exitBudget, $"{budget} ms against {exitBudget} ms");
        SetSource(null);
        SetLocate(null);

        // The uninstall-grade sweep behind a cleanup in an uninstall.
        var acts = SdkType("DeviceOrchestrator").GetField("BeforeEverythingActs", AnyStatic);
        var everything = SdkType("DeviceOrchestrator").GetMethod("RemoveEverything", AnyStatic);
        if (acts == null || everything == null) Check("the SDK has the uninstall-grade sweep's hook", false, "this build has none");
        else
        {
            long uninstallEnd = -1, actsAt = -1;
            SetSource(new (string, Func<bool>)[]
            {
                (FakeId, () => { Thread.Sleep(1_500); Interlocked.Exchange(ref uninstallEnd, clock.ElapsedTicks); return true; }),
            });
            SetLocate(_ => false);
            acts.SetValue(null, (Action)(() =>
            {
                Interlocked.Exchange(ref actsAt, clock.ElapsedTicks);
                throw new OperationCanceledException("phantomgate stops the sweep before it acts");
            }));
            var behind = new Thread(() => run());
            behind.Start();
            // The cleanup holds the gate once a read lock can no longer be had.
            var held = Stopwatch.StartNew();
            while (held.ElapsedMilliseconds < 5_000)
            {
                if (!gate.TryEnterReadLock(0)) break;
                gate.ExitReadLock();
                Thread.Sleep(1);
            }
            bool stopped = false;
            try { everything.Invoke(null, new object[] { true }); }
            catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException) { stopped = true; }
            finally { acts.SetValue(null, null); }
            behind.Join();
            SetSource(null);
            SetLocate(null);
            Check("the uninstall-grade sweep acts only once the cleanup's uninstall is done",
                  stopped && uninstallEnd > 0 && actsAt > uninstallEnd,
                  $"uninstall done at {uninstallEnd * 1000 / Stopwatch.Frequency} ms, sweep would act at {actsAt * 1000 / Stopwatch.Frequency} ms");
        }

        // The exit join, in children that exit while their cleanup runs.
        string dir = Path.Combine(Path.GetTempPath(), "hm-phantomgate-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            int childBound = 2 * exitBudget + 20_000;
            string joined = Path.Combine(dir, "cleanup.done"), abandoned = Path.Combine(dir, "thread.done"), cut = Path.Combine(dir, "long.done");
            string many = Path.Combine(dir, "many.count"), known = Path.Combine(dir, "known.done"), queued = Path.Combine(dir, "queued.done");
            int rcJoin = RunGateChild("cleanup", joined, childBound, out long msJoin, out _);
            int rcCtl = RunGateChild("thread", abandoned, childBound, out long msCtl, out _);
            int rcLong = RunGateChild("long", cut, childBound, out long msLong, out string logLong);
            int rcMany = RunGateChild("many", many, childBound, out long msMany, out string logMany);
            int rcKnown = RunGateChild("known", known, childBound, out long msKnown, out string logKnown);
            int rcQueued = RunGateChild("queued", queued, childBound, out long msQueued, out string logQueued);
            string inExit = Path.Combine(dir, "inexit.done"), inExitLate = Path.Combine(dir, "inexitlate.done");
            int rcInExit = RunGateChild("inexit", inExit, childBound, out long msInExit, out _);
            int rcInExitLate = RunGateChild("inexitlate", inExitLate, childBound, out long msInExitLate, out _);
            string alc = Path.Combine(dir, "alc.txt");
            int rcAlc = RunGateChild("alc", alc, childBound, out _, out _);
            Check("a process that exits while its cleanup runs waits for it", rcJoin == 0 && File.Exists(joined) && msJoin >= 1_500,
                  $"exit {rcJoin} after {msJoin} ms, marker {(File.Exists(joined) ? "written" : "missing")}");
            Check("control: the same work on a plain background thread is abandoned at exit", rcCtl == 0 && !File.Exists(abandoned),
                  $"exit {rcCtl} after {msCtl} ms, marker {(File.Exists(abandoned) ? "written" : "missing")}");
            Check("a cleanup whose uninstall outlasts the exit's budget ends the wait at the budget",
                  rcLong == 0 && !File.Exists(cut) && msLong >= exitBudget && msLong < exitBudget + 10_000,
                  $"exit {rcLong} after {msLong} ms, budget {exitBudget} ms, marker {(File.Exists(cut) ? "written" : "missing")}");
            Check("and the exit names the cleanup as what held the gate, not a create",
                  logLong.Contains("phantom cleanup still holds the gate", StringComparison.Ordinal)
                  && !logLong.Contains("a create is still in flight", StringComparison.Ordinal),
                  LastExitLine(logLong));
            // An exit whose join outlasts the cleanup logs that it reached the
            // end of the exit's budget. That line, not the child's wall
            // time, which counts its start and teardown on a machine that
            // may be busy, says whether the cleanup ended inside the budget.
            // The cleanup's own summary line, written as its run ends, shows
            // the child's log was written.
            const string budgetSpent = "at the end of the exit's budget", cleanupEnded = "phantom cleanup:";
            string count = File.Exists(many) ? File.ReadAllText(many) : "0";
            Check("once the exit starts, the cleanup begins no further uninstall",
                  rcMany == 0 && count == "1" && logMany.Contains(cleanupEnded, StringComparison.Ordinal)
                  && !logMany.Contains(budgetSpent, StringComparison.Ordinal),
                  $"exit {rcMany} after {msMany} ms, {count} of 5 uninstalls ran. {LastExitLine(logMany)}");
            Check("a phantom that stays known costs the exit the cleanup's budget, which ends inside the exit's",
                  rcKnown == 0 && msKnown >= budget && logKnown.Contains(cleanupEnded, StringComparison.Ordinal)
                  && !logKnown.Contains(budgetSpent, StringComparison.Ordinal) && File.Exists(known),
                  $"exit {rcKnown} after {msKnown} ms, cleanup budget {budget} ms, exit budget {exitBudget} ms, " +
                  $"handler {(File.Exists(known) ? "ran" : "did not run")} past the join. {LastExitLine(logKnown)}");
            Check("a cleanup queued behind a create in flight ends the exit's wait at the budget",
                  rcQueued == 0 && msQueued >= exitBudget && msQueued < exitBudget + 10_000 && !File.Exists(queued),
                  $"exit {rcQueued} after {msQueued} ms, budget {exitBudget} ms");
            Check("and the exit names the create as what held the cleanup, not an uninstall",
                  logQueued.Contains("still waiting for a create", StringComparison.Ordinal)
                  && !logQueued.Contains("in an uninstall", StringComparison.Ordinal),
                  LastExitLine(logQueued));
            Check("a cleanup started in the program's own exit handler, after a call into the SDK before exit, runs to its end before the call returns",
                  rcInExit == 0 && Marker(inExit, ".loaded") == "1" && File.Exists(inExit + ".began") && File.Exists(inExit) && msInExit >= 1_500,
                  $"exit {rcInExit} after {msInExit} ms, {InExitShow(inExit)}");
            Check("as the program's first call into the SDK, its uninstall begins and the process ends inside it",
                  rcInExitLate == 0 && Marker(inExitLate, ".loaded") == "0" && File.Exists(inExitLate + ".began") && !File.Exists(inExitLate),
                  $"exit {rcInExitLate} after {msInExitLate} ms, {InExitShow(inExitLate)}");
            string alcResult = File.Exists(alc) ? File.ReadAllText(alc) : "no result";
            Check("a collectible load context that loaded the SDK unloads: the module initializer's handler on the default context goes with it, "
                  + "and the default context's own copy keeps its handler and its flag clear",
                  rcAlc == 0 && alcResult == "subscribed=1 removed=1 unloaded=1 defaultkept=1 defaultraised=0", $"exit {rcAlc}, {alcResult}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>`CoexistCheck.exe firstcall &lt;runs&gt; [phantoms] [inexit]`:
    /// what a process's first call spends on the phantom cleanup, each run
    /// in a new process, with the cleanup's real path and no seam. The child
    /// makes the call through OwnerSweep.StartPhantomCleanupOnce, where every
    /// first InstallDriver, create and RemoveAllVirtualControllers starts the
    /// cleanup, or through OwnerSweep.PhantomCleanupFor on a build that has
    /// it. It then joins the cleanup, so every run times two spans, the call
    /// and the call to the cleanup's end, and no run cuts a cleanup off. It
    /// reports how many phantoms the cleanup removed. With "inexit" the child
    /// calls into the SDK at startup and makes the call inside a ProcessExit
    /// handler of its own, where a build with ProcessExitSignal runs the
    /// cleanup on the calling thread and one without starts a thread that the
    /// join waits for. Each run reports where the cleanup ran, seen as the
    /// call returns: no thread started and its report written is the
    /// calling thread. On a build with ProcessExitSignal every run must show
    /// the calling thread inside the handler and a thread of its own at
    /// startup. Unelevated, no uninstall or registry write can succeed, and
    /// on a machine with no HIDMaestro phantom the cleanup has none to make. With "phantoms", elevated, a peer creates two Xbox 360
    /// controllers and a pad and disposes them before every run, which
    /// leaves phantoms, and every run must find some and remove them all.
    /// Prints p50 and p95 of both spans.</summary>
    static int FirstCallTimes(int runs, bool makePhantoms, bool inExit)
    {
        string dir = Path.Combine(Path.GetTempPath(), "hm-firstcall-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        var call = new List<double>();
        var total = new List<double>();
        var ran = new Dictionary<string, int>();
        try
        {
            for (int i = 1; i <= runs; i++)
            {
                int before = 0;
                if (makePhantoms)
                {
                    using (var maker = PeerProcess.Start($"F{i}-maker"))
                    {
                        bool ok = Ok(maker.Send("create xbox - -")) & Ok(maker.Send("create xbox - -")) & Ok(maker.Send("create pad F215 -"));
                        maker.Send("dispose");
                        if (!ok) { Check($"firstcall: run {i}'s maker creates", false); return 1; }
                    }
                    before = Native.HidMaestroPhantoms().Count;
                    if (before == 0) { Check($"firstcall: run {i} has phantoms to remove", false); return 1; }
                }
                string result = Path.Combine(dir, $"run{i}.txt");
                string args = $"firstcall-child \"{result}\"" + (inExit ? " inexit" : "");
                var psi = new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi)!)
                    if (!p.WaitForExit(600_000)) { try { p.Kill(); } catch { } Check($"firstcall: run {i} ends", false); return 1; }
                if (!File.Exists(result)) { Check($"firstcall: run {i} reports its time", false); return 1; }
                string[] parts = File.ReadAllText(result).Split(' ');
                call.Add(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture));
                total.Add(double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                string where1 = parts.Length > 3 ? parts[3] : "unseen";
                ran[where1] = ran.GetValueOrDefault(where1) + 1;
                if (makePhantoms)
                {
                    int after = Native.HidMaestroPhantoms().Count;
                    Console.WriteLine($"    run {i}: {before} phantoms, call {parts[0]} ms, to the cleanup's end {parts[1]} ms, " +
                                      $"cleanup removed {parts[2]}, {after} left");
                    if (after != 0) { Check($"firstcall: run {i}'s cleanup removes every phantom", false, $"{after} left"); return 1; }
                }
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        string where = inExit ? "inside an exit handler" : "at startup";
        string seen = string.Join(", ", ran.OrderBy(r => r.Key).Select(r => r.Key switch
        {
            "inline" => $"on the calling thread, ended when the call returned: {r.Value} runs",
            "thread" => $"on a thread the call started: {r.Value} runs",
            _ => $"not seen: {r.Value} runs",
        }));
        Console.WriteLine($"first call {where}, the cleanup ran {seen}");
        Console.WriteLine($"first call {where}: {RankLine(call)}");
        Console.WriteLine($"first call {where} to the cleanup's end: {RankLine(total)}");
        // On a build with the exit signal, where the cleanup ran is checked,
        // not assumed: inside the handler on the calling thread, at startup
        // on a thread of its own.
        if (typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.ProcessExitSignal") != null)
        {
            string expected = inExit ? "inline" : "thread";
            Check($"firstcall: every run's cleanup ran {(inExit ? "on the calling thread" : "on a thread the call started")}",
                  ran.GetValueOrDefault(expected) == runs, seen);
            return s_failures == 0 ? 0 : 1;
        }
        return 0;
    }

    /// <summary>The run count of "firstcall", wherever it stands among the
    /// words after the mode: "firstcall 40 inexit" and "firstcall inexit 40"
    /// both give 40.</summary>
    static int RunCount(string[] args)
    {
        foreach (string a in args.Skip(1))
            if (int.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
                return n;
        throw new ArgumentException("firstcall needs a run count");
    }

    static string RankLine(List<double> v)
    {
        v.Sort();
        double Rank(double q) => v[Math.Max(0, (int)Math.Ceiling(q * v.Count) - 1)];
        return FormattableString.Invariant($"p50 {Rank(0.50):F3} ms, p95 {Rank(0.95):F3} ms, min {v[0]:F3}, max {v[^1]:F3}, n={v.Count}");
    }

    static int FirstCallChild(string result, bool inExit)
    {
        if (!inExit)
        {
            FirstCall(result);
            return 0;
        }
        // A call into the SDK now, which runs its module initializer on a
        // build that has one. The timed call comes in the handler.
        _ = Scaled(0);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FirstCall(result);
        return 0;
    }

    static void FirstCall(string result)
    {
        Type sweep = SdkType("OwnerSweep");
        MethodInfo? waits = sweep.GetMethod("PhantomCleanupFor", AnyStatic);
        var sw = Stopwatch.StartNew();
        if (waits != null) waits.Invoke(null, new object[] { true });
        else sweep.GetMethod("StartPhantomCleanupOnce", AnyStatic)!.Invoke(null, null);
        double ms = sw.Elapsed.TotalMilliseconds;
        // Where it ran, seen as the call returns: no thread started and a
        // report already written is the calling thread, a started thread is
        // a thread of its own.
        object? thread = sweep.GetField("s_phantomCleanup", AnyStatic)?.GetValue(null);
        bool reported = sweep.GetProperty("LastPhantomCleanup", AnyStatic)?.GetValue(null) != null;
        string ran = thread != null ? "thread" : reported ? "inline" : "unseen";
        // The cleanup ends before the process does, on every build.
        sweep.GetMethod("JoinPhantomCleanup", AnyStatic)?.Invoke(null, new object[] { 600_000 });
        double toEnd = sw.Elapsed.TotalMilliseconds;
        object? report = sweep.GetProperty("LastPhantomCleanup", AnyStatic)?.GetValue(null);
        string removed = report == null ? "unknown" : Prop(report, "Removed") ?? "unknown";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        File.WriteAllText(result, ms.ToString("F3", inv) + " " + toEnd.ToString("F3", inv) + " " + removed + " " + ran);
    }

    static string? Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o)?.ToString();

    static string Marker(string marker, string suffix) => File.Exists(marker + suffix) ? File.ReadAllText(marker + suffix) : "none";

    static string InExitShow(string marker) =>
        $"SDK loaded before the handler {Marker(marker, ".loaded")}, uninstall {(File.Exists(marker + ".began") ? "began" : "never began")}, " +
        $"marker {(File.Exists(marker) ? "written" : "missing")}";

    static string LastExitLine(string log)
    {
        string last = "(no exit line)";
        foreach (string line in log.Split('\n'))
            if (line.Contains("exit:", StringComparison.Ordinal)) last = line.Trim();
        return last;
    }

    /// <summary>Run a child with its own TEMP and the SDK's diagnostic log
    /// on, and hand back that log.</summary>
    static int RunGateChild(string mode, string marker, int boundMs, out long ms, out string log)
    {
        string temp = Path.Combine(Path.GetDirectoryName(marker)!, mode + ".temp");
        Directory.CreateDirectory(temp);
        var psi = new ProcessStartInfo(Environment.ProcessPath!, $"phantomgate-child {mode} \"{marker}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["TMP"] = temp;
        psi.Environment["TEMP"] = temp;
        psi.Environment["HIDMAESTRO_DIAG"] = "1";
        var sw = Stopwatch.StartNew();
        int rc;
        using (var p = Process.Start(psi)!)
        {
            if (!p.WaitForExit(boundMs)) { try { p.Kill(); } catch { } rc = -1; }
            else rc = p.ExitCode;
        }
        ms = sw.ElapsedMilliseconds;
        string path = Path.Combine(temp, "HIDMaestro", "teardown_diag.log");
        log = "";
        try { if (File.Exists(path)) log = File.ReadAllText(path); } catch { }
        return rc;
    }

    /// <summary>The child: register the SDK's exit handler, as the owner
    /// claim does, start work through the cleanup's uninstall of one
    /// phantom or on a plain background thread, and return from Main at
    /// once. "long" works past the exit's budget. "many" has five phantoms
    /// whose uninstalls each count themselves. "known" uninstalls one at
    /// once that stays known, and writes the marker from a handler of its
    /// own, which runs after the SDK's. "queued" holds the gate's read side
    /// on a thread of its own, as a create in flight does, so the cleanup
    /// never gets the gate and its uninstall would write the marker.
    /// "inexit" calls into the SDK and registers a handler of its own
    /// before the SDK's, as a program does before its first SDK claim.
    /// Inside it, it registers the SDK's handler, as a first owner claim
    /// taken there does, and starts the cleanup of one phantom whose
    /// uninstall writes ".began" and then, 1.5 s later, the marker.
    /// "inexitlate" does the same without touching the SDK before the
    /// handler, and waits there until the uninstall began, so the process
    /// ends inside it. Both write ".loaded": whether the SDK's assembly was
    /// loaded when the handler started.</summary>
    /// <summary>The "inexit" children's handler. It reads whether the SDK's
    /// assembly is loaded before anything here can load it: the SDK is
    /// touched only in <see cref="InExitCleanup"/>, compiled when called.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void InExitHandler(string marker, bool late)
    {
        bool loaded = Array.Exists(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "HIDMaestro.Core");
        File.WriteAllText(marker + ".loaded", loaded ? "1" : "0");
        InExitCleanup(marker, late);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void InExitCleanup(string marker, bool late)
    {
        Type sweep = SdkType("OwnerSweep");
        sweep.GetMethod("RegisterExitCleanup", AnyStatic)!.Invoke(null, null);
        SetSource(new (string, Func<bool>)[]
        {
            (FakeId, () =>
            {
                File.WriteAllText(marker + ".began", "1");
                Thread.Sleep(1_500);
                File.WriteAllText(marker, "done");
                return true;
            }),
        });
        SetLocate(_ => false);
        sweep.GetMethod("StartPhantomCleanupOnce", AnyStatic)!.Invoke(null, null);
        // A thread the call started ends with the process: wait here until
        // its uninstall has begun, so the process ends inside it.
        if (late)
        {
            var sw = Stopwatch.StartNew();
            while (!File.Exists(marker + ".began") && sw.ElapsedMilliseconds < Scaled(5_000)) Thread.Sleep(10);
        }
    }

    /// <summary>The "alc" child's load: a call into the default context's
    /// copy of the SDK first, then a copy in a collectible load context, one
    /// call into it, which runs its module initializer, and the context's
    /// Unload. Whether a handler of the copy's sat on the
    /// default context's Unloading before the Unload, and whether it was
    /// gone after it, read from the event's delegate field. Only a weak
    /// reference to the context leaves this method.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (WeakReference Context, bool Subscribed, bool Removed) LoadSdkCollectible()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("hm-phantomgate-collectible", isCollectible: true);
        Assembly sdk = context.LoadFromAssemblyPath(typeof(HMContext).Assembly.Location);
        sdk.GetType("HIDMaestro.HMBuildInfo")!.GetProperty("HasOpenVrDriver")!.GetValue(null);
        bool subscribed = DefaultUnloadingHas(sdk);
        context.Unload();
        bool removed = !DefaultUnloadingHas(sdk);
        return (new WeakReference(context), subscribed, removed);
    }

    /// <summary>The default context's copy of the SDK: whether its handler
    /// still sits on the default context's Unloading, and its flag. A method
    /// of its own, since PhantomGateChild must name no SDK type: compiling it
    /// would run the module initializer before the "inexitlate" handler.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (bool Kept, bool Raised) DefaultCopyState() =>
        (DefaultUnloadingHas(typeof(HMContext).Assembly),
         (bool)SdkType("ProcessExitSignal").GetProperty("Raised", AnyStatic)!.GetValue(null)!);

    static bool DefaultUnloadingHas(Assembly sdk)
    {
        var field = typeof(System.Runtime.Loader.AssemblyLoadContext).GetField("_unloading", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(System.Runtime.Loader.AssemblyLoadContext.Default) is not Delegate handlers) return false;
        return Array.Exists(handlers.GetInvocationList(), d => d.Method.DeclaringType?.Assembly == sdk);
    }

    static int PhantomGateChild(string mode, string marker)
    {
        if (mode == "alc")
        {
            // A call into the default context's copy, which runs its module
            // initializer before the collectible copy loads.
            _ = Scaled(0);
            var (context, subscribed, removed) = LoadSdkCollectible();
            for (int i = 0; i < 20 && context.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            var (defaultKept, defaultRaised) = DefaultCopyState();
            File.WriteAllText(marker, $"subscribed={(subscribed ? 1 : 0)} removed={(removed ? 1 : 0)} unloaded={(context.IsAlive ? 0 : 1)} "
                                      + $"defaultkept={(defaultKept ? 1 : 0)} defaultraised={(defaultRaised ? 1 : 0)}");
            return 0;
        }
        void Work(int ms) { Thread.Sleep(ms); File.WriteAllText(marker, "done"); }
        if (mode == "thread")
        {
            new Thread(() => Work(1_500)) { IsBackground = true }.Start();
        }
        else if (mode is "inexit" or "inexitlate")
        {
            bool late = mode == "inexitlate";
            // A call into the SDK, which runs its module initializer.
            if (!late) _ = Scaled(0);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => InExitHandler(marker, late);
        }
        else
        {
            Type sweep = SdkType("OwnerSweep");
            sweep.GetMethod("RegisterExitCleanup", AnyStatic)!.Invoke(null, null);
            if (mode == "many")
            {
                int done = 0;
                var five = new List<(string, Func<bool>)>();
                for (int i = 0; i < 5; i++)
                    five.Add(($@"ROOT\HIDMAESTRO\7E6{i}", () =>
                    {
                        Thread.Sleep(1_000);
                        File.WriteAllText(marker, Interlocked.Increment(ref done).ToString());
                        return true;
                    }));
                SetSource(five);
                SetLocate(_ => false);
            }
            else if (mode == "queued")
            {
                var gate = (ReaderWriterLockSlim)sweep.GetField("CreateGate", AnyStatic)!.GetValue(null)!;
                var held = new ManualResetEventSlim();
                new Thread(() => { gate.EnterReadLock(); held.Set(); Thread.Sleep(Timeout.Infinite); }) { IsBackground = true }.Start();
                held.Wait();
                SetSource(new (string, Func<bool>)[] { (FakeId, () => { Work(0); return true; }) });
                SetLocate(_ => false);
            }
            else if (mode == "known")
            {
                SetSource(new (string, Func<bool>)[] { (FakeId, () => true) });
                SetLocate(_ => true);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => File.WriteAllText(marker, "done");
            }
            else
            {
                int ms = mode == "long" ? 2 * Scaled(SdkConst("OwnerSweep", "ExitWaitMs")) + 20_000 : 1_500;
                SetSource(new (string, Func<bool>)[] { (FakeId, () => { Work(ms); return true; }) });
                SetLocate(_ => false);
            }
            sweep.GetMethod("StartPhantomCleanupOnce", AnyStatic)!.Invoke(null, null);
        }
        Thread.Sleep(100);
        return 0;
    }
}
