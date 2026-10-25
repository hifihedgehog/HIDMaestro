// signal_gate_model: an exhaustive check of the input event's waiter gate
// (docs/design/signal-gate.md). It explores every interleaving of the SDK's
// writer and one or two driver workers over a few frames, with each thread's
// stores held in a store buffer that drains to memory at any later step (the
// store-load reordering x64 allows, and ARM64 allows too). A fence or an
// interlocked operation runs only with the thread's buffer empty. The wait
// and SetEvent are system calls on an auto-reset event and also run with the
// buffer empty.
//
// A state where no step is possible is final. In a final state with the
// worker's 500 ms timeout left out, the frames the SDK wrote must have been
// read: by the worker with one worker, and by at least one of them with two,
// which is what today's protocol (a SetEvent on every frame) guarantees with
// two workers on one named event. The count of waiters in memory must always
// equal the workers inside their counted window.
//
// The variants without a fence on one side must fail. That they do shows the
// check finds a lost wakeup when there is one.
//
// Usage:
//   SignalGateModel [frames]   the fence and companion variants at frames
//     (3 unless given), then the delivery check at one frame a program:
//     this driver's rules, then each rule taken away. Exit 0 when every
//     verdict is the expected one.
//   SignalGateModel check <frames> <rule> [--shard k/n] [-j N] [--index i]
//     [--quiet] [--progress] [--max-states n] [--base c<i>] [--outcomes]
//     [--set-stats]   the delivery check alone. Rule -1 is this driver, an
//     index takes that rule away (Broken), c<i> runs a candidate change
//     (Candidates), and --base takes the rule away from a candidate.
//     Shards split the scenarios by index modulo n, so separate processes
//     can run them. --set-stats prints each scenario's set store size.
//   SignalGateModel scenarios   each scenario's index and name.
//   SignalGateModel outcomes <frames> <scenario> [v203]   one driver's
//     outcomes in a scenario, unpaired.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    enum Proto { Always, Gated }

    sealed record Variant(string Name, Proto Proto, bool SdkFence, bool WorkerAtomic, int Workers, bool Timeouts, bool ExpectLost);

    static int Main(string[] args)
    {
        // check <frames> <rule> [--shard k/n] [-j N] [--scenario name]: the
        // delivery check for one rule set (-1: this driver's own rules, an
        // index: that rule taken away). See Check.
        if (args.Length >= 3 && (args[0] == "check" || args[0] == "rule"))
            return Check(args);
        // scenarios: each scenario's index and name.
        if (args.Length >= 1 && args[0] == "scenarios")
        {
            int i = 0;
            foreach (var sc in DeliveryModel.Scenarios()) Console.WriteLine($"{i++}\t{sc.Name}");
            return 0;
        }
        // outcomes <frames> <scenario> [v203]: every outcome one driver
        // reaches in a scenario, unpaired.
        if (args.Length >= 3 && args[0] == "outcomes")
        {
            var sc = DeliveryModel.Scenarios().First(x => x.Name == args[2]);
            var drv = args.Length > 3 ? DeliveryModel.Driver.V203 : DeliveryModel.Driver.This;
            var res = DeliveryModel.Explore(sc, drv, DeliveryModel.This, int.Parse(args[1]));
            foreach (var v in res.Outcomes.OrderBy(x => x)) Console.WriteLine(DeliveryModel.Show(v));
            Console.WriteLine($"{res.States} states, {res.Finals} final{(res.ZeroSent ? ", a zeroed section sent" : "")}");
            return 0;
        }
        int frames = args.Length > 0 ? int.Parse(args[0]) : 3;
        var variants = new[]
        {
            new Variant("today: SetEvent every frame, one worker", Proto.Always, true, true, 1, false, false),
            new Variant("today: SetEvent every frame, two workers", Proto.Always, true, true, 2, false, false),
            new Variant("gate, both fences, one worker", Proto.Gated, true, true, 1, false, false),
            new Variant("gate, both fences, two workers", Proto.Gated, true, true, 2, false, false),
            new Variant("gate, both fences, two workers, timeouts", Proto.Gated, true, true, 2, true, false),
            new Variant("gate, a plain store of the handoff in the SDK, one worker", Proto.Gated, false, true, 1, false, true),
            new Variant("gate, the take before the count in the worker, one worker", Proto.Gated, true, false, 1, false, true),
        };
        int bad = 0;
        foreach (var v in variants)
        {
            var r = Explore(v, frames);
            bool lost = r.Lost != null;
            bool ok = lost == v.ExpectLost && r.InvariantBroken == null;
            if (!ok) bad++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {v.Name}: {r.States} states, {r.Finals} final, " +
                (lost ? "a lost wakeup found" : "no lost wakeup") + (v.ExpectLost ? " (expected one)" : ""));
            if (r.Lost != null) Console.WriteLine($"         {r.Lost}");
            if (r.InvariantBroken != null) Console.WriteLine($"         count invariant broken: {r.InvariantBroken}");
        }
        foreach (var v in CompanionModel.Variants)
        {
            var r = CompanionModel.Explore(v, frames);
            bool lost = r.Lost != null;
            bool ok = lost == v.ExpectLost && r.InvariantBroken == null;
            if (!ok) bad++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {v.Name}: {r.States} states, {r.Finals} final, " +
                (lost ? "the last frame left unserved" : "the last frame served") + (v.ExpectLost ? " (expected)" : ""));
            if (r.Lost != null) Console.WriteLine($"         {r.Lost}");
            if (r.InvariantBroken != null) Console.WriteLine($"         count invariant broken: {r.InvariantBroken}");
        }
        // The delivery check at one frame a program, in this process: this
        // driver's rules must pass, and each rule taken away must fail. The
        // larger runs shard it across processes (check).
        // One scenario at a time: a paired scenario can take gigabytes.
        int jobs = 1;
        if (RunCheck(1, -1, 0, 1, jobs, null, false) != 0) bad++;
        for (int i = 0; i < DeliveryModel.Broken.Length; i++)
            if (RunCheck(1, i, 0, 1, jobs, null, false) != 0) bad++;
        Console.WriteLine(bad == 0 ? "signal_gate_model: PASS" : $"signal_gate_model: FAIL ({bad})");
        return bad == 0 ? 0 : 1;
    }

    // check <frames> <rule> [--shard k/n] [-j N] [--scenario name] [--quiet]:
    // the order-aligned delivery check (DeliveryModel.ExploreAligned) over
    // the scenarios whose index is k modulo n, N at a time. Rule -1 is this
    // driver: every scenario must pass, and each failure prints its witness.
    // A rule index takes that rule away: some scenario must fail, and the
    // check stops at the first (lowest index) that does and prints its
    // witness. Exit 0 when the verdict is the expected one.
    static int Check(string[] args)
    {
        int frames = int.Parse(args[1]);
        // c<index>: a candidate change in place of this driver's rules.
        int rule = args[2].StartsWith("c") ? -100 - int.Parse(args[2].Substring(1)) : int.Parse(args[2]);
        int shard = 0, shards = 1, jobs = 1;
        string? only = null;
        bool quiet = false;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--shard") { var p = args[++i].Split('/'); shard = int.Parse(p[0]); shards = int.Parse(p[1]); }
            else if (args[i] == "-j") jobs = int.Parse(args[++i]);
            else if (args[i] == "--scenario") only = args[++i];
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] == "--progress") s_progress = true;
            else if (args[i] == "--set-stats") DeliveryModel.SetStats = true;
            else if (args[i] == "--index") s_index = int.Parse(args[++i]);
            else if (args[i] == "--outcomes") s_outcomes = true;
            else if (args[i] == "--base") s_base = args[++i];
            else if (args[i] == "--max-states") s_maxStates = long.Parse(args[++i]);
            else only = args[i];
        }
        return RunCheck(frames, rule, shard, shards, jobs, only, !quiet);
    }

    static bool s_progress;
    static int s_index = -1;
    static bool s_outcomes;
    static string? s_base;

    // The rules a run starts from: this driver, or a candidate change
    // (--base c<i>), and a rule taken away on top of them.
    static DeliveryModel.Rules Base() => s_base == null ? DeliveryModel.This : DeliveryModel.Candidates[int.Parse(s_base.Substring(1))];

    static DeliveryModel.Rules Compose(DeliveryModel.Rules removal)
    {
        var b = Base();
        if (s_base == null) return removal;
        var r = b with { Name = removal.Name + " (on " + b.Name + ")" };
        foreach (var p in typeof(DeliveryModel.Rules).GetProperties())
        {
            if (p.Name is "Name" or "EqualityContract" || !p.CanWrite) continue;
            var v = p.GetValue(removal);
            if (!Equals(v, p.GetValue(DeliveryModel.This))) p.SetValue(r, v);
        }
        return r;
    }
    static long s_maxStates = long.MaxValue;

    static int RunCheck(int frames, int rule, int shard, int shards, int jobs, string? only, bool witness)
    {
        var rules = rule <= -100 ? DeliveryModel.Candidates[-100 - rule] : rule < 0 ? Base() : Compose(DeliveryModel.Broken[rule]);
        var all = DeliveryModel.Scenarios().Select((sc, i) => (sc, i)).ToList();
        var mine = all.Where(x => x.i % shards == shard && (only == null || x.sc.Name == only) && (s_index < 0 || x.i == s_index)).ToList();
        var results = new DeliveryModel.AlignedResult?[mine.Count];
        int best = int.MaxValue;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, mine.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, k =>
        {
            if (rule >= 0 && k > Volatile.Read(ref best)) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = DeliveryModel.ExploreAligned(mine[k].sc, rules, frames, false,
                                                 rule >= 0 ? () => k > Volatile.Read(ref best) : null, s_maxStates);
            if (s_outcomes)
                lock (results) foreach (var o in a.Worse.OrderBy(x => x)) Console.WriteLine($"outcome {mine[k].i} [{DeliveryModel.Show(o)}]");
            if (s_progress)
                Console.Error.WriteLine($"{mine[k].i}	{a.States}	{a.Sets}	{a.SetItems}	{a.V203States}	{(a.TooLarge ? "TOO LARGE" : "")}	{sw.Elapsed.TotalSeconds:F1}s	{mine[k].sc.Name}	subsumed {a.Subsumed}	worse {a.Worse.Count}	extra {a.Extra.Count}");
            // A rule taken away breaks the check where it gives a failing
            // outcome this driver's own rules do not give in that scenario.
            if (rule >= 0 && !a.Canceled && (a.Worse.Count > 0 || a.ZeroSent))
            {
                var own = DeliveryModel.ExploreAligned(mine[k].sc, Base(), frames, false,
                                                       () => k > Volatile.Read(ref best), s_maxStates);
                if (own.Canceled) { a.Worse.Clear(); a.ZeroSent = false; }
                else
                {
                    a.OwnFails = own.Worse.Count > 0 || own.ZeroSent;
                    a.Worse.ExceptWith(own.Worse);
                    if (own.ZeroSent) a.ZeroSent = false;
                    if (a.Worse.Count > 0) a.Example = DeliveryModel.Show(a.Worse.First()) + " (an outcome this driver's own rules do not give)";
                }
            }
            results[k] = a;
            if (rule >= 0 && !a.Canceled && (a.Worse.Count > 0 || a.ZeroSent))
            {
                int seen;
                do { seen = Volatile.Read(ref best); } while (k < seen && Interlocked.CompareExchange(ref best, k, seen) != seen);
            }
        });
        long states = 0, finals = 0, unaligned = 0, v203 = 0, largest = 0, ownFails = 0, subsumed = 0, setItems = 0, largestSets = 0, extraScenarios = 0;
        long scFail = 0, scBetter = 0, scEqual = 0, outFail = 0, outBetter = 0, outEqual = 0;
        string largestName = "";
        var failures = new List<int>();
        for (int k = 0; k < mine.Count; k++)
        {
            var a = results[k];
            if (a == null || a.Canceled) continue;
            if (a.OwnFails) ownFails++;
            states += a.States; finals += a.Finals; unaligned += a.Unaligned; v203 += a.V203States; 
            setItems += a.SetItems; subsumed += a.Subsumed; largestSets = Math.Max(largestSets, a.SetItems);
            if (a.Extra.Count > 0) extraScenarios++;
            outFail += a.Worse.Count; outBetter += a.Extra.Count; outEqual += a.Equal.Count;
            if (a.Worse.Count > 0 || a.ZeroSent) scFail++;
            else if (a.Extra.Count > 0) scBetter++;
            else scEqual++;
            if (a.States > largest) { largest = a.States; largestName = mine[k].sc.Name; }
            if (a.Worse.Count > 0 || a.ZeroSent) failures.Add(k);
        }
        string where = $"{frames} frame(s) a program, shard {shard}/{shards}, {mine.Count} scenario(s)";
        string totals = $"scenarios: {scFail} fail, {scBetter} better, {scEqual} equal; outcomes: {outFail} fail, {outBetter} better, {outEqual} equal. " +
                        $"{extraScenarios} scenario(s) with an extra frame, {states} paired states, {finals} final, {v203} v2.0.3 states, {unaligned} shared step(s) v2.0.3 could not take, " +
                        $"{subsumed} pair(s) subsumed, {setItems} v2.0.3 set members (largest scenario {largestSets}), " +
                        $"largest scenario {largest} paired states ({largestName}), {clock.Elapsed.TotalSeconds:F0} s";
        if (rule < 0)
        {
            foreach (int k in failures)
            {
                var a = results[k]!;
                Console.WriteLine($"[FAIL] {mine[k].sc.Name} (scenario {mine[k].i}): {a.Worse.Count} outcome(s) no paired v2.0.3 run matches, such as [{a.Example}]" +
                                  (a.ZeroSent ? ", a zeroed section sent" : ""));
                if (witness) PrintWitness(mine[k].sc, rules, frames);
            }
            for (int k = 0; k < mine.Count; k++)
            {
                var a = results[k];
                if (a == null || a.Extra.Count == 0) continue;
                Console.WriteLine($"[EXTRA] {mine[k].sc.Name} (scenario {mine[k].i}): {a.Extra.Count} outcome(s) send a frame no paired v2.0.3 run sends, such as [{a.ExtraExample}]");
            }
            Console.WriteLine($"[{(failures.Count == 0 ? "PASS" : "FAIL")}] delivery with {rules.Name}, {where}: " +
                              (failures.Count == 0 ? "every outcome matched or bettered by a paired v2.0.3 run" : $"{failures.Count} scenario(s) fail") +
                              $". {totals}");
            return failures.Count == 0 ? 0 : 1;
        }
        if (failures.Count > 0)
        {
            int k = failures.Min();
            var a = results[k]!;
            Console.WriteLine($"[BREAKS] {rules.Name}, {where}: {mine[k].sc.Name} (scenario {mine[k].i}): [{a.Example}]" +
                              (a.ZeroSent ? ", a zeroed section sent" : ""));
            if (witness) PrintWitness(mine[k].sc, rules, frames, a.Worse.Count > 0 ? a.Worse.First() : null);
            return 0;
        }
        Console.WriteLine($"[NO BREAK] {rules.Name}, {where}: no scenario gives a failing outcome this driver's own rules do not" +
                          (ownFails > 0 ? $" ({ownFails} scenario(s) where this driver's own rules fail as well)" : "") + $". {totals}");
        return 1;
    }

    static void PrintExtra(DeliveryModel.AlignedResult a)
    {
        if (a.ExtraPath == null) return;
        Console.WriteLine($"    an extra frame, {a.ExtraPath.Count - 1} steps, this driver delivers [{a.ExtraExample}]:");
        for (int i = 1; i < a.ExtraPath.Count; i++)
            Console.WriteLine($"    {i,3}. {DeliveryModel.Describe(a.ExtraPath[i - 1], a.ExtraPath[i])}");
    }

    // The failing order, state by state, as the fields each step changed.
    static void PrintWitness(DeliveryModel.Scenario sc, DeliveryModel.Rules rules, int frames, ulong? want = null)
    {
        var a = DeliveryModel.ExploreAligned(sc, rules, frames, true);
        var path = want != null && a.Paths.TryGetValue(want.Value, out var p) ? p : a.Path;
        if (want != null && path != a.Path) a.Example = DeliveryModel.Show(want.Value);
        if (path == null) { Console.WriteLine("    (no witness path)"); return; }
        Console.WriteLine($"    witness, {path.Count - 1} steps, this driver delivers [{a.Example}]:");
        for (int i = 1; i < path.Count; i++)
            Console.WriteLine($"    {i,3}. {DeliveryModel.Describe(path[i - 1], path[i])}");
        if (a.PairedSide != null)
            foreach (var l in a.PairedSide) Console.WriteLine($"    {l}");
        PrintExtra(a);
        if (a.UnalignedPath != null)
        {
            var u = a.UnalignedPath;
            Console.WriteLine($"    the first shared step v2.0.3 could not take, after {u.Count - 2} steps:");
            for (int i = 1; i < u.Count; i++)
                Console.WriteLine($"    {i,3}. {DeliveryModel.Describe(u[i - 1], u[i])}");
        }
    }

    // Worker program counters.
    const int W_INC = 0, W_CHECK = 1, W_DEC_PENDING = 2, W_TAKE = 3, W_WAIT = 4, W_DEC = 5, W_PROC = 6;
    // The broken worker: the take first, then the count.
    const int W_INC_LATE = 7;
    // SDK program counters: the frame's store, the exchange into the
    // handoff, the load of the count, the compare-exchange that takes the
    // frame back, the SetEvent.
    const int S_STORE = 0, S_XCHG = 1, S_LOAD = 2, S_SIGNAL = 3, S_DONE = 4, S_RECLAIM = 5;
    // Variables.
    const int SEQ = 0, WAIT = 1, HAND = 2;

    sealed class State
    {
        public int MemSeq, MemWait, MemHand, Event;
        public int SdkPc, SdkFrame;
        public List<(int Var, int Val)> SdkBuf = new();
        public int[] Pc = Array.Empty<int>(), Seen = Array.Empty<int>(), Tmp = Array.Empty<int>();
        public bool[] Blocked = Array.Empty<bool>();
        public List<(int Var, int Val)>[] Buf = Array.Empty<List<(int, int)>>();
        public string Trace = "";

        public State Clone()
        {
            var s = (State)MemberwiseClone();
            s.SdkBuf = new(SdkBuf);
            s.Pc = (int[])Pc.Clone(); s.Seen = (int[])Seen.Clone(); s.Tmp = (int[])Tmp.Clone();
            s.Blocked = (bool[])Blocked.Clone();
            s.Buf = Buf.Select(b => new List<(int, int)>(b)).ToArray();
            return s;
        }

        public string Key()
        {
            var parts = new List<string> { $"{MemSeq},{MemWait},{MemHand},{Event}|{SdkPc},{SdkFrame}|{string.Join(";", SdkBuf)}" };
            for (int i = 0; i < Pc.Length; i++)
                parts.Add($"{Pc[i]},{Seen[i]},{Tmp[i]},{Blocked[i]}|{string.Join(";", Buf[i])}");
            return string.Join("#", parts);
        }
    }

    sealed class Result { public int States, Finals; public string? Lost, InvariantBroken; }

    static int Load(List<(int Var, int Val)> buf, int var, int mem)
    {
        for (int i = buf.Count - 1; i >= 0; i--) if (buf[i].Var == var) return buf[i].Val;
        return mem;
    }

    static Result Explore(Variant v, int frames)
    {
        var start = new State
        {
            Pc = new int[v.Workers], Seen = new int[v.Workers], Tmp = new int[v.Workers],
            Blocked = new bool[v.Workers],
            Buf = Enumerable.Range(0, v.Workers).Select(_ => new List<(int, int)>()).ToArray(),
        };
        for (int i = 0; i < v.Workers; i++) start.Pc[i] = v.Proto == Proto.Always ? W_WAIT : (v.WorkerAtomic ? W_INC : W_CHECK);
        var seen = new HashSet<string>();
        var stack = new Stack<State>();
        stack.Push(start);
        seen.Add(start.Key());
        var result = new Result();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            result.States++;
            string? inv = CheckCount(s, v);
            if (inv != null && result.InvariantBroken == null) result.InvariantBroken = inv + " after " + s.Trace;
            var next = Steps(s, v, frames).ToList();
            if (next.Count == 0)
            {
                result.Finals++;
                bool readAll = v.Workers == 1 ? s.Seen[0] == frames : s.Seen.Max() == frames;
                if (!readAll && result.Lost == null)
                    result.Lost = $"final state with frames read [{string.Join(",", s.Seen)}] of {frames}: {s.Trace}";
                continue;
            }
            foreach (var n in next)
                if (seen.Add(n.Key())) stack.Push(n);
        }
        return result;
    }

    // The waiters word counts the workers between their increment and their
    // decrement. With the plain increment the word is not exact by design,
    // so the check is only for the interlocked variants.
    static string? CheckCount(State s, Variant v)
    {
        if (v.Proto != Proto.Gated || !v.WorkerAtomic) return null;
        int inside = 0;
        for (int i = 0; i < s.Pc.Length; i++)
            if (s.Pc[i] is W_CHECK or W_DEC_PENDING or W_WAIT or W_DEC) inside++;
        return s.MemWait == inside ? null : $"waiters {s.MemWait}, workers inside {inside}";
    }

    static IEnumerable<State> Steps(State s, Variant v, int frames)
    {
        // Store buffers drain one entry at a time, oldest first.
        if (s.SdkBuf.Count > 0)
        {
            var n = s.Clone();
            var (var, val) = n.SdkBuf[0]; n.SdkBuf.RemoveAt(0);
            if (var == SEQ) n.MemSeq = val; else if (var == HAND) n.MemHand = val; else n.MemWait = val;
            n.Trace += " sdk:drain";
            yield return n;
        }
        for (int w = 0; w < s.Pc.Length; w++)
            if (s.Buf[w].Count > 0)
            {
                var n = s.Clone();
                var (var, val) = n.Buf[w][0]; n.Buf[w].RemoveAt(0);
                if (var == SEQ) n.MemSeq = val; else n.MemWait = val;
                n.Trace += $" w{w}:drain";
                yield return n;
            }

        // The SDK: frame k is a store of SEQ = k, then the gate: the frame
        // goes into the handoff with an exchange (a full fence), the count is
        // loaded, and with a waiter counted a compare-exchange takes the frame
        // back before the SetEvent.
        if (s.SdkPc != S_DONE)
        {
            switch (s.SdkPc)
            {
                case S_STORE:
                {
                    var n = s.Clone();
                    n.SdkBuf.Add((SEQ, n.SdkFrame + 1));
                    n.SdkPc = v.Proto == Proto.Always ? S_SIGNAL : S_XCHG;
                    n.Trace += $" sdk:store{n.SdkFrame + 1}";
                    yield return n;
                    break;
                }
                case S_XCHG:
                    if (!v.SdkFence)
                    {
                        // The broken SDK: a plain store, left in the buffer.
                        var n = s.Clone(); n.SdkBuf.Add((HAND, n.SdkFrame + 1)); n.SdkPc = S_LOAD;
                        n.Trace += " sdk:store-handoff"; yield return n;
                    }
                    else if (s.SdkBuf.Count == 0)
                    {
                        var n = s.Clone(); n.MemHand = n.SdkFrame + 1; n.SdkPc = S_LOAD;
                        n.Trace += " sdk:xchg-handoff"; yield return n;
                    }
                    break;
                case S_RECLAIM:
                    if (s.SdkBuf.Count == 0)
                    {
                        var n = s.Clone();
                        if (n.MemHand == n.SdkFrame + 1) { n.MemHand = 0; n.SdkPc = S_SIGNAL; n.Trace += " sdk:take-back"; }
                        else { Advance(n, frames); n.Trace += " sdk:taken"; }
                        yield return n;
                    }
                    break;
                case S_LOAD:
                {
                    var n = s.Clone();
                    int waiters = Load(n.SdkBuf, WAIT, n.MemWait);
                    n.Trace += $" sdk:load-waiters={waiters}";
                    if (waiters != 0) n.SdkPc = v.Proto == Proto.Gated ? S_RECLAIM : S_SIGNAL;
                    else { n.SdkFrame++; n.SdkPc = n.SdkFrame == frames ? S_DONE : S_STORE; }
                    yield return n;
                    break;
                }
                case S_SIGNAL:
                    if (s.SdkBuf.Count == 0)
                    {
                        // SetEvent on an auto-reset event: one blocked waiter
                        // wakes and the event stays clear, or the event is set.
                        var blocked = Enumerable.Range(0, s.Pc.Length).Where(i => s.Blocked[i]).ToList();
                        if (blocked.Count == 0)
                        {
                            var n = s.Clone(); n.Event = 1; Advance(n, frames); n.Trace += " sdk:set"; yield return n;
                        }
                        else
                            foreach (int i in blocked)
                            {
                                var n = s.Clone(); n.Blocked[i] = false; n.Pc[i] = WakeTo(v); Advance(n, frames);
                                n.Trace += $" sdk:set-wakes-w{i}";
                                yield return n;
                            }
                    }
                    break;
            }
        }

        for (int w = 0; w < s.Pc.Length; w++)
        {
            if (s.Blocked[w])
            {
                if (v.Timeouts && s.Buf[w].Count == 0)
                {
                    var n = s.Clone(); n.Blocked[w] = false; n.Pc[w] = WakeTo(v); n.Trace += $" w{w}:timeout"; yield return n;
                }
                continue;
            }
            switch (s.Pc[w])
            {
                case W_INC:
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone(); n.MemWait++; n.Pc[w] = W_CHECK; n.Trace += $" w{w}:inc"; yield return n;
                    }
                    break;
                case W_INC_LATE:
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone(); n.MemWait++; n.Pc[w] = W_WAIT; n.Trace += $" w{w}:inc"; yield return n;
                    }
                    break;
                case W_CHECK:
                    // The take: an exchange of the handoff with 0. A frame
                    // already read wakes nothing.
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone();
                        int took = n.MemHand; n.MemHand = 0;
                        bool pending = took != 0 && took != n.Seen[w];
                        n.Pc[w] = !v.WorkerAtomic ? (pending ? W_PROC : W_INC_LATE) : (pending ? W_DEC_PENDING : W_WAIT);
                        n.Trace += $" w{w}:take={took}";
                        yield return n;
                    }
                    break;
                case W_DEC_PENDING:
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone(); n.MemWait--; n.Pc[w] = W_TAKE; n.Trace += $" w{w}:dec-pending"; yield return n;
                    }
                    break;
                case W_TAKE:
                    // The pending path's wait with a zero timeout: it may take
                    // a signal already set, or find none.
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone(); n.Pc[w] = W_PROC; n.Trace += $" w{w}:poll"; yield return n;
                        if (s.Event == 1)
                        {
                            var m = s.Clone(); m.Event = 0; m.Pc[w] = W_PROC; m.Trace += $" w{w}:poll-takes"; yield return m;
                        }
                    }
                    break;
                case W_WAIT:
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone();
                        if (n.Event == 1) { n.Event = 0; n.Pc[w] = WakeTo(v); n.Trace += $" w{w}:wait-takes"; }
                        else { n.Blocked[w] = true; n.Trace += $" w{w}:blocks"; }
                        yield return n;
                    }
                    break;
                case W_DEC:
                    if (s.Buf[w].Count == 0)
                    {
                        var n = s.Clone(); n.MemWait--; n.Pc[w] = W_PROC; n.Trace += $" w{w}:dec"; yield return n;
                    }
                    break;
                case W_PROC:
                {
                    var n = s.Clone();
                    n.Seen[w] = Load(n.Buf[w], SEQ, n.MemSeq);
                    n.Pc[w] = v.Proto == Proto.Always ? W_WAIT : (v.WorkerAtomic ? W_INC : W_CHECK);
                    n.Trace += $" w{w}:read={n.Seen[w]}";
                    yield return n;
                    break;
                }
            }
        }
    }

    // Where a worker goes once its wait returns.
    static int WakeTo(Variant v) => v.Proto == Proto.Always ? W_PROC : W_DEC;

    static void Advance(State n, int frames)
    {
        n.SdkFrame++;
        n.SdkPc = n.SdkFrame == frames ? S_DONE : S_STORE;
    }
}
