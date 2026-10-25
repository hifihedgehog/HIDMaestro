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
// Usage: SignalGateModel [frames]   (3 unless given). Exit 0 when every
// variant gives the expected verdict, 1 otherwise.
using System;
using System.Collections.Generic;
using System.Linq;

internal static class Program
{
    enum Proto { Always, Gated }

    sealed record Variant(string Name, Proto Proto, bool SdkFence, bool WorkerAtomic, int Workers, bool Timeouts, bool ExpectLost);

    static int Main(string[] args)
    {
        int frames = args.Length > 0 && args[0] != "witness" ? int.Parse(args[0]) : 3;
        // witness <frames> <scenario> <frame> twice|lost [v203]: the shortest
        // order, one state a line.
        if (args.Length >= 5 && args[0] == "witness")
        {
            var sc = DeliveryModel.Scenarios().First(x => x.Name == args[2]);
            var drv = args.Length > 5 ? DeliveryModel.Driver.V203 : DeliveryModel.Driver.This;
            foreach (var st in DeliveryModel.Witness(sc, drv, DeliveryModel.This, int.Parse(args[1]), args[3], args[4] == "twice"))
                Console.WriteLine(st);
            return 0;
        }
        var variants = new[]
        {
            new Variant("today: SetEvent every frame, one worker", Proto.Always, true, true, 1, false, false),
            new Variant("today: SetEvent every frame, two workers", Proto.Always, true, true, 2, false, false),
            new Variant("gate, both fences, one worker", Proto.Gated, true, true, 1, false, false),
            new Variant("gate, both fences, two workers", Proto.Gated, true, true, 2, false, false),
            new Variant("gate, both fences, two workers, timeouts", Proto.Gated, true, true, 2, true, false),
            new Variant("gate, no fence in the SDK, one worker", Proto.Gated, false, true, 1, false, true),
            new Variant("gate, plain increment in the worker, one worker", Proto.Gated, true, false, 1, false, true),
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
        bad += Delivery(frames);
        Console.WriteLine(bad == 0 ? "signal_gate_model: PASS" : $"signal_gate_model: FAIL ({bad})");
        return bad == 0 ? 0 : 1;
    }

    // The delivery rules against v2.0.3's (DeliveryModel.cs): in every
    // scenario this driver loses no frame v2.0.3 does not lose, sends none
    // twice that v2.0.3 does not, and sends no zeroed section. Then each
    // rule taken away must break that somewhere.
    static int Delivery(int frames)
    {
        int bad = 0, dupOld = 0, dupNew = 0, lostOld = 0, lostNew = 0, zeroOld = 0;
        long oldStates = 0, newStates = 0;
        var scenarios = DeliveryModel.Scenarios().ToList();
        var olds = new Dictionary<string, DeliveryModel.Result>();
        foreach (var sc in scenarios)
        {
            var o = DeliveryModel.Explore(sc, DeliveryModel.Driver.V203, DeliveryModel.This, frames);
            var n = DeliveryModel.Explore(sc, DeliveryModel.Driver.This, DeliveryModel.This, frames);
            olds[sc.Name] = o;
            if (o.Duplicate.Count > 0) dupOld++;
            if (n.Duplicate.Count > 0) dupNew++;
            if (o.Lost.Count > 0) lostOld++;
            if (n.Lost.Count > 0) lostNew++;
            if (o.ZeroSent) zeroOld++;
            oldStates += o.States; newStates += n.States;
            string? why = Worse(n, o);
            if (why == null) continue;
            bad++;
            Console.WriteLine($"[FAIL] delivery: {sc.Name}: v2.0.3 {o.States} states, this driver {n.States}: {why}");
        }
        string verdict = bad == 0 ? "PASS" : "FAIL";
        Console.WriteLine($"[{verdict}] delivery against v2.0.3's rules, {frames} frame(s) a program: {scenarios.Count} scenarios, " +
            $"{oldStates} states under v2.0.3, {newStates} under this driver. A frame sent twice in {dupOld} scenarios under v2.0.3 " +
            $"and {dupNew} under this driver, a final frame never sent in {lostOld} and {lostNew}, a zeroed section sent in {zeroOld} and 0" +
            (bad == 0 ? ". Each outcome of this driver's is matched or bettered by one of v2.0.3's in the same scenario" : ""));
        foreach (var rules in DeliveryModel.Broken)
        {
            string? found = null;
            foreach (var sc in scenarios)
            {
                var why = Worse(DeliveryModel.Explore(sc, DeliveryModel.Driver.This, rules, frames), olds[sc.Name]);
                if (why != null) { found = $"{sc.Name}: {why}"; break; }
            }
            bool ok = found != null;
            if (!ok) bad++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] delivery with {rules.Name}: {(found ?? "no scenario breaks it")}");
        }
        return bad;
    }

    // What this driver does worse than v2.0.3 in one scenario, or null: an
    // outcome (each frame's delivery count in a final state) that no outcome
    // of v2.0.3's in the scenario matches or betters, or a zeroed section.
    static string? Worse(DeliveryModel.Result n, DeliveryModel.Result o)
    {
        var parts = new List<string>();
        var worse = n.Outcomes.Where(t => !o.Outcomes.Any(v => DeliveryModel.NoWorse(t, v))).ToList();
        if (worse.Count > 0)
            parts.Add($"{worse.Count} outcome(s) v2.0.3 never gives, such as [{DeliveryModel.Show(worse[0])}] (={worse[0]})");
        if (n.ZeroSent) parts.Add("a zeroed section sent");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
    // Worker program counters.
    const int W_INC = 0, W_CHECK = 1, W_DEC_PENDING = 2, W_TAKE = 3, W_WAIT = 4, W_DEC = 5, W_PROC = 6;
    // A worker's plain increment (the broken variant): load, then a buffered store.
    const int W_INC_LOAD = 7, W_INC_STORE = 8;
    // SDK program counters.
    const int S_STORE = 0, S_FENCE = 1, S_LOAD = 2, S_SIGNAL = 3, S_DONE = 4;
    // Variables.
    const int SEQ = 0, WAIT = 1;

    sealed class State
    {
        public int MemSeq, MemWait, Event;
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
            var parts = new List<string> { $"{MemSeq},{MemWait},{Event}|{SdkPc},{SdkFrame}|{string.Join(";", SdkBuf)}" };
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
        for (int i = 0; i < v.Workers; i++) start.Pc[i] = v.Proto == Proto.Always ? W_WAIT : (v.WorkerAtomic ? W_INC : W_INC_LOAD);
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
            if (var == SEQ) n.MemSeq = val; else n.MemWait = val;
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

        // The SDK: frame k is a store of SEQ = k, then the gate.
        if (s.SdkPc != S_DONE)
        {
            switch (s.SdkPc)
            {
                case S_STORE:
                {
                    var n = s.Clone();
                    n.SdkBuf.Add((SEQ, n.SdkFrame + 1));
                    n.SdkPc = v.Proto == Proto.Always ? S_SIGNAL : (v.SdkFence ? S_FENCE : S_LOAD);
                    n.Trace += $" sdk:store{n.SdkFrame + 1}";
                    yield return n;
                    break;
                }
                case S_FENCE:
                    if (s.SdkBuf.Count == 0)
                    {
                        var n = s.Clone(); n.SdkPc = S_LOAD; n.Trace += " sdk:fence"; yield return n;
                    }
                    break;
                case S_LOAD:
                {
                    var n = s.Clone();
                    int waiters = Load(n.SdkBuf, WAIT, n.MemWait);
                    n.Trace += $" sdk:load-waiters={waiters}";
                    if (waiters != 0) n.SdkPc = S_SIGNAL;
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
                case W_INC_LOAD:
                {
                    var n = s.Clone(); n.Tmp[w] = Load(n.Buf[w], WAIT, n.MemWait); n.Pc[w] = W_INC_STORE;
                    n.Trace += $" w{w}:load-waiters"; yield return n;
                    break;
                }
                case W_INC_STORE:
                {
                    var n = s.Clone(); n.Buf[w].Add((WAIT, n.Tmp[w] + 1)); n.Pc[w] = W_CHECK;
                    n.Trace += $" w{w}:store-waiters"; yield return n;
                    break;
                }
                case W_CHECK:
                {
                    var n = s.Clone();
                    int seq = Load(n.Buf[w], SEQ, n.MemSeq);
                    n.Pc[w] = seq != n.Seen[w] ? W_DEC_PENDING : W_WAIT;
                    n.Trace += $" w{w}:check-seq={seq}";
                    yield return n;
                    break;
                }
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
                    n.Pc[w] = v.Proto == Proto.Always ? W_WAIT : (v.WorkerAtomic ? W_INC : W_INC_LOAD);
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
