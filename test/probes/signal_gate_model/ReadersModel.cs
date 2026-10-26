// The XUSB companion's readers (docs/design/signal-gate.md, "Every reader
// once", and driver/companion.c, CompanionServeReaders): every interleaving of
// the SDK's writer, the pump's turns on the doorbell and the kick, the 8 ms
// timer's turns, and several readers, each a handle of its own that keeps
// one or two WAIT_FOR_INPUT requests outstanding and sends one again the
// moment it completes, each request's dispatch on a thread of its own.
//
// Each step is one critical section: of ReadersLock, of the queue's lock,
// an interlocked operation, or a read or write of the section. The SDK's
// store and its read of the count, and a dispatch's increment and its look,
// each with a full fence between, face each other as on the input event, so
// the model needs no store buffer (CompanionModel and Round6FenceModel check
// the fences). Frame 0 is the state before any frame, which a reader's first
// request gets.
//
// Invariants:
//   a. With the timer off, in a final state every reader has the newest
//      frame. A request in flight has a step left, so in a final state every
//      request is parked, and the last completion to each reader must have
//      carried the newest frame.
//   b. No reader gets one frame twice.
//   c. No reader gets a frame older than one it had.
// d4103a21 and v2.0.3 serve one request a turn, and their timer serves the
// frame there is whether its reader had it or not.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

internal static class ReadersModel
{
    // PerReader: a turn serves every reader that lacks its frame, through
    // the reader's oldest request (this driver). Without it a turn serves
    // the oldest request in the queue, whoever's. NewerOnly: a turn on the
    // doorbell or the kick serves only a frame newer than the last
    // completion (d4103a21). Kick: a request's dispatch looks at the
    // section after its park and kicks the pump. KickPerReader: that look
    // compares with its reader's last frame, else with the last completion
    // of any reader. InLock: a turn completes its requests inside
    // ReadersLock, else after it. Gated: the SDK rings only while the count
    // of parked requests is not zero.
    internal sealed record Variant(string Name, bool PerReader, bool NewerOnly, bool Kick, bool KickPerReader, bool InLock, bool Gated,
                                   string Expect);

    internal static readonly Variant[] Variants =
    {
        new("v2.0.3 and b0fb7dc9 under an SDK before 2.1.0: a turn serves the oldest request with the frame there is, a doorbell on every frame, no kick",
            false, false, false, false, false, false, "broken"),
        new("d4103a21: a turn serves the oldest request, with a frame newer than the last completion on a doorbell or a kick; the kick compares with the last completion",
            false, true, true, false, false, true, "broken"),
        new("this driver: a turn serves every reader that lacks the frame, its oldest request, under ReadersLock; the kick compares with the reader's last frame",
            true, false, true, true, true, true, ""),
        new("this driver under an SDK before 2.1.0: a doorbell on every frame",
            true, false, true, true, true, false, ""),
        new("this driver without the kick",
            true, false, false, false, true, true, "a"),
        new("this driver with the kick comparing the last completion of any reader",
            true, false, true, false, true, true, "a"),
        new("this driver completing after ReadersLock",
            true, false, true, true, false, true, "c"),
    };

    // Ticks: the timer's turns in the run, 0 for no timer.
    internal sealed record Scenario(string Name, int[] Requests, int Frames, int Ticks);

    internal static readonly Scenario[] Scenarios =
    {
        new("two readers, one request each, two frames, no timer", new[] { 1, 1 }, 2, 0),
        new("two readers, one request each, three frames, no timer", new[] { 1, 1 }, 3, 0),
        new("three readers, one request each, one frame, no timer", new[] { 1, 1, 1 }, 1, 0),
        new("two readers, two requests and one, two frames, no timer", new[] { 2, 1 }, 2, 0),
        new("two readers, one request each, two frames, two ticks", new[] { 1, 1 }, 2, 2),
        new("two readers, two requests and one, one frame, one tick", new[] { 2, 1 }, 1, 1),
    };

    // A request is idle once completed, and its reader sends it next. It is
    // out from its send while its dispatch runs and while it is parked, and
    // taken while a turn holds it uncompleted.
    const int Q_IDLE = 0, Q_OUT = 1, Q_TAKEN = 2;
    // A dispatch thread's steps. A request completed while the thread that
    // parked it is still in its look is sent again on another thread, so
    // there is one thread more than requests.
    const int D_FREE = 0, D_LINK = 1, D_FWD = 2, D_INC = 3, D_LOOK = 4;
    // A turn's steps, for the pump and the timer alike. T_SERVE walks the
    // readers (PerReader) or takes the queue's head. T_DONE completes what
    // the turn took after ReadersLock (InLock false).
    const int T_IDLE = 0, T_START = 1, T_SERVE = 2, T_BUILD = 3, T_DONE = 4;

    sealed class Turn
    {
        public int Pc, Seq, Index;
        public List<int> Taken = new();   // requests taken, completed at T_DONE
        public Turn Clone() => new() { Pc = Pc, Seq = Seq, Index = Index, Taken = new List<int>(Taken) };
        public void Key(StringBuilder b)
        {
            b.Append(Pc).Append(',').Append(Seq).Append(',').Append(Index).Append('[').Append(string.Join(".", Taken)).Append(']');
        }
    }

    // The order that led to a state: each state keeps its last step and the
    // state before it, so states share their history.
    sealed class Path
    {
        public readonly Path? Up;
        public readonly string Step;
        public Path(Path? up, string step) { Up = up; Step = step; }
        public override string ToString()
        {
            var steps = new List<string>();
            for (var p = this; p != null; p = p.Up) steps.Add(p.Step);
            steps.Reverse();
            return string.Concat(steps);
        }
    }

    sealed class St
    {
        public int Written, SdkPc;                // SdkPc 0: write the next frame, 1: read the count
        public bool Doorbell, Kick;
        public int Count, Lock, LastServed = -1;  // Lock: 0 free, 1 the pump's turn, 2 the timer's
        public int TimerLeft;
        public Turn Pump = new(), Timer = new();
        public int[] Q = Array.Empty<int>(), QFrame = Array.Empty<int>();
        public int[] Dpc = Array.Empty<int>(), Dq = Array.Empty<int>();
        public List<int> Queue = new();
        public int[] Served = Array.Empty<int>(), Got = Array.Empty<int>();
        public bool[] Linked = Array.Empty<bool>();
        public List<int> Order = new();
        public Path? Steps;
        public string Trace => Steps?.ToString() ?? "";
        public void Add(string step) => Steps = new Path(Steps, step);

        public St Clone()
        {
            var s = (St)MemberwiseClone();
            s.Pump = Pump.Clone(); s.Timer = Timer.Clone();
            s.Q = (int[])Q.Clone(); s.QFrame = (int[])QFrame.Clone();
            s.Dpc = (int[])Dpc.Clone(); s.Dq = (int[])Dq.Clone();
            s.Queue = new List<int>(Queue); s.Order = new List<int>(Order);
            s.Served = (int[])Served.Clone(); s.Got = (int[])Got.Clone(); s.Linked = (bool[])Linked.Clone();
            return s;
        }

        public string Key()
        {
            var b = new StringBuilder();
            b.Append(Written).Append(SdkPc).Append(Doorbell ? 'd' : '-').Append(Kick ? 'k' : '-')
             .Append(Count).Append(',').Append(Lock).Append(',').Append(LastServed).Append(',').Append(TimerLeft).Append('|');
            Pump.Key(b); b.Append('|'); Timer.Key(b); b.Append('|');
            b.Append(string.Join(".", Q)).Append('|').Append(string.Join(".", QFrame)).Append('|').Append(string.Join(".", Queue)).Append('|');
            // Any free thread takes the next send: the threads in sorted order.
            var slots = new List<string>();
            for (int d = 0; d < Dpc.Length; d++) slots.Add(Dpc[d] == D_FREE ? "F" : $"{Dpc[d]}:{Dq[d]}");
            slots.Sort(StringComparer.Ordinal);
            b.Append(string.Join(";", slots)).Append('|');
            b.Append(string.Join(".", Served)).Append('|').Append(string.Join(".", Got)).Append('|');
            b.Append(string.Concat(Linked.Select(l => l ? '1' : '0'))).Append('|').Append(string.Join(".", Order));
            return b.ToString();
        }

        // The visited set keeps a 128-bit hash of the key, as WakeModel's.
        public (ulong, ulong) Hash()
        {
            string k = Key();
            ulong a = 14695981039346656037UL, c = 0x84222325CBF29CE4UL;
            foreach (char ch in k)
            {
                a = (a ^ ch) * 1099511628211UL;
                c = (c ^ ch) * 0x100000001B3UL ^ (c >> 29);
            }
            return (a, c);
        }
    }

    internal sealed class Result { public int States, Finals; public bool Capped; public string? Lost, Twice, Older; }

    // Reader of each request, from the scenario.
    static int[] Owners(Scenario sc)
    {
        var o = new List<int>();
        for (int r = 0; r < sc.Requests.Length; r++)
            for (int k = 0; k < sc.Requests[r]; k++) o.Add(r);
        return o.ToArray();
    }

    internal static Result Explore(Variant v, Scenario sc, int maxStates = 6_000_000)
    {
        int ticks = sc.Ticks;
        int[] owner = Owners(sc);
        int readers = sc.Requests.Length;
        var start = new St
        {
            Q = new int[owner.Length], QFrame = Enumerable.Repeat(-1, owner.Length).ToArray(),
            Dpc = new int[owner.Length + 1], Dq = Enumerable.Repeat(-1, owner.Length + 1).ToArray(),
            Served = Enumerable.Repeat(-2, readers).ToArray(), Got = Enumerable.Repeat(-1, readers).ToArray(),
            Linked = new bool[readers], TimerLeft = ticks,
        };
        var seen = new HashSet<(ulong, ulong)> { start.Hash() };
        var stack = new Stack<St>();
        stack.Push(start);
        var res = new Result();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            res.States++;
            if (res.States > maxStates) { res.Capped = true; break; }
            // A variant expected to break needs only its first breaks.
            if (v.Expect != "" && res.Lost != null && res.Twice != null && res.Older != null) break;
            var next = new List<St>();
            Steps(s, v, sc, owner, next, res);
            if (next.Count == 0)
            {
                res.Finals++;
                if (ticks == 0 && res.Lost == null)
                    for (int r = 0; r < readers; r++)
                        if (s.Got[r] != s.Written)
                        {
                            res.Lost = $"reader {r} left with frame {s.Got[r]} while frame {s.Written} is the newest, after{s.Trace}";
                            break;
                        }
                continue;
            }
            foreach (var n in next)
                if (seen.Add(n.Hash())) stack.Push(n);
        }
        return res;
    }

    static void Deliver(St s, int q, int frame, int[] owner, Result res)
    {
        int r = owner[q];
        if (frame == s.Got[r] && res.Twice == null) res.Twice = $"reader {r} got frame {frame} a second time, after{s.Trace}";
        if (frame < s.Got[r] && res.Older == null) res.Older = $"reader {r} got frame {frame} after frame {s.Got[r]}, after{s.Trace}";
        s.Got[r] = Math.Max(s.Got[r], frame);
        s.Q[q] = Q_IDLE;
        s.QFrame[q] = -1;
    }

    static void Steps(St s, Variant v, Scenario sc, int[] owner, List<St> next, Result res)
    {
        St n;
        // The SDK: the frame's store, then its read of the count and the ring.
        if (s.SdkPc == 0 && s.Written < sc.Frames)
        {
            n = s.Clone(); n.Written++; n.SdkPc = 1; n.Add($" sdk:write{n.Written}"); next.Add(n);
        }
        else if (s.SdkPc == 1)
        {
            n = s.Clone(); n.SdkPc = 0;
            bool ring = !v.Gated || n.Count != 0;
            if (ring) n.Doorbell = true;
            n.Add(ring ? " sdk:ring" : " sdk:no-ring"); next.Add(n);
        }

        // Each request its reader sends again once it completed, on a free
        // thread.
        for (int q = 0; q < s.Q.Length; q++)
        {
            if (s.Q[q] != Q_IDLE) continue;
            int d = Array.IndexOf(s.Dpc, D_FREE);
            if (d < 0) break;
            // A request whose reader needs no joining enters the queue in
            // the step that sends it: nothing reads it in between.
            n = s.Clone(); n.Q[q] = Q_OUT; n.Dq[d] = q;
            if (v.PerReader && !n.Linked[owner[q]]) { n.Dpc[d] = D_LINK; n.Add($" r{owner[q]}:send{q}(t{d})"); }
            else { n.Queue.Add(q); n.Dpc[d] = v.Gated ? D_INC : D_LOOK; n.Add($" r{owner[q]}:send-park{q}(t{d})"); }
            next.Add(n);
        }
        // Each dispatch thread's next step.
        for (int d = 0; d < s.Dpc.Length; d++)
        {
            if (s.Dpc[d] == D_FREE) continue;
            int q = s.Dq[d], r = owner[q];
            switch (s.Dpc[d])
            {
                case D_LINK:
                    // A reader's first request joins it to the readers under
                    // ReadersLock, which a turn holds. Another request of the
                    // reader may have joined it meanwhile.
                    if (s.Linked[r]) { n = s.Clone(); n.Dpc[d] = D_FWD; n.Add($" t{d}:linked"); next.Add(n); }
                    else if (s.Lock == 0)
                    {
                        n = s.Clone(); n.Linked[r] = true; n.Order.Add(r); n.Dpc[d] = D_FWD; n.Add($" t{d}:link{r}"); next.Add(n);
                    }
                    break;
                case D_FWD:
                    n = s.Clone(); n.Queue.Add(q); n.Dpc[d] = v.Gated ? D_INC : D_LOOK; n.Add($" t{d}:park{q}"); next.Add(n);
                    break;
                case D_INC:
                    n = s.Clone(); n.Count++; n.Dpc[d] = D_LOOK; n.Add($" t{d}:count"); next.Add(n);
                    break;
                case D_LOOK:
                {
                    // A turn may have served the request already: the look
                    // then reads its reader's new frame.
                    n = s.Clone();
                    bool newer = v.KickPerReader ? n.Written != n.Served[r] : n.Written != n.LastServed;
                    if (v.Kick && newer) n.Kick = true;
                    n.Dpc[d] = D_FREE; n.Dq[d] = -1;
                    n.Add($" t{d}:look{(v.Kick && newer ? "-kick" : "")}");
                    next.Add(n);
                    break;
                }
            }
        }

        // The pump: a turn on the doorbell or the kick.
        if (s.Pump.Pc == T_IDLE)
        {
            if (s.Doorbell) { n = s.Clone(); n.Doorbell = false; n.Pump.Pc = T_START; n.Add(" pump:doorbell"); next.Add(n); }
            if (s.Kick) { n = s.Clone(); n.Kick = false; n.Pump.Pc = T_START; n.Add(" pump:kick"); next.Add(n); }
        }
        else TurnStep(s, v, owner, true, next, res);

        // The timer: a turn at any moment, while ticks are left.
        if (s.Timer.Pc == T_IDLE)
        {
            if (s.TimerLeft > 0) { n = s.Clone(); n.TimerLeft--; n.Timer.Pc = T_START; n.Add(" timer:tick"); next.Add(n); }
        }
        else TurnStep(s, v, owner, false, next, res);
    }

    static void TurnStep(St s, Variant v, int[] owner, bool pump, List<St> next, Result res)
    {
        var t = pump ? s.Pump : s.Timer;
        string who = pump ? "pump" : "timer";
        int me = pump ? 1 : 2;
        St n;
        if (v.PerReader)
        {
            switch (t.Pc)
            {
                case T_START:
                    if (s.Lock != 0) return;   // the other turn holds ReadersLock
                    n = s.Clone(); n.Lock = me;
                    var nt = pump ? n.Pump : n.Timer;
                    nt.Seq = n.Written; nt.Index = 0; nt.Pc = T_SERVE;
                    n.Add($" {who}:lock-seq{n.Written}"); next.Add(n);
                    return;
                case T_SERVE:
                {
                    n = s.Clone();
                    var nt2 = pump ? n.Pump : n.Timer;
                    if (nt2.Index >= n.Order.Count)
                    {
                        n.Lock = 0;
                        nt2.Pc = nt2.Taken.Count > 0 ? T_DONE : T_IDLE;
                        n.Add($" {who}:unlock"); next.Add(n);
                        return;
                    }
                    int r = n.Order[nt2.Index++];
                    int q = n.Served[r] == nt2.Seq ? -1 : n.Queue.FirstOrDefault(x => owner[x] == r, -1);
                    if (q >= 0)
                    {
                        n.Queue.Remove(q);
                        if (v.Gated) n.Count--;
                        n.Served[r] = nt2.Seq;
                        n.LastServed = nt2.Seq;
                        n.Add($" {who}:serve{r}-q{q}-f{nt2.Seq}");
                        if (v.InLock) Deliver(n, q, nt2.Seq, owner, res);
                        else { n.Q[q] = Q_TAKEN; n.QFrame[q] = nt2.Seq; nt2.Taken.Add(q); }
                    }
                    else n.Add($" {who}:skip{r}");
                    next.Add(n);
                    return;
                }
                case T_DONE:
                {
                    n = s.Clone();
                    var nt3 = pump ? n.Pump : n.Timer;
                    int q = nt3.Taken[0];
                    nt3.Taken.RemoveAt(0);
                    n.Add($" {who}:complete-q{q}-f{n.QFrame[q]}");
                    Deliver(n, q, n.QFrame[q], owner, res);
                    if (nt3.Taken.Count == 0) nt3.Pc = T_IDLE;
                    next.Add(n);
                    return;
                }
            }
            return;
        }
        // One request a turn, as through d4103a21: the queue's head, the
        // state built after the take, then the completion. No lock.
        switch (t.Pc)
        {
            case T_START:
            {
                n = s.Clone();
                var nt = pump ? n.Pump : n.Timer;
                bool skip = pump && v.NewerOnly && n.Written == n.LastServed;
                if (skip || n.Queue.Count == 0) { nt.Pc = T_IDLE; n.Add($" {who}:none"); next.Add(n); return; }
                int q = n.Queue[0]; n.Queue.RemoveAt(0);
                if (v.Gated) n.Count--;
                n.Q[q] = Q_TAKEN; nt.Taken.Add(q); nt.Pc = T_BUILD;
                n.Add($" {who}:take-q{q}"); next.Add(n);
                return;
            }
            case T_BUILD:
            {
                n = s.Clone();
                var nt = pump ? n.Pump : n.Timer;
                int q = nt.Taken[0];
                n.QFrame[q] = n.Written; n.LastServed = n.Written; nt.Pc = T_DONE;
                n.Add($" {who}:build-f{n.Written}"); next.Add(n);
                return;
            }
            case T_DONE:
            {
                n = s.Clone();
                var nt = pump ? n.Pump : n.Timer;
                int q = nt.Taken[0]; nt.Taken.Clear();
                n.Add($" {who}:complete-q{q}-f{n.QFrame[q]}");
                n.Served[owner[q]] = n.QFrame[q];
                Deliver(n, q, n.QFrame[q], owner, res);
                nt.Pc = T_IDLE; next.Add(n);
                return;
            }
        }
    }

    // SignalGateModel readers: every variant against every scenario.
    internal static int Run()
    {
        int bad = 0;
        foreach (var v in Variants)
        {
            int states = 0, finals = 0;
            string? lost = null, twice = null, older = null, capped = null;
            foreach (var sc in Scenarios)
                {
                    var r = Explore(v, sc);
                    states += r.States; finals += r.Finals;
                    if (r.Capped) capped ??= sc.Name;
                    string where = $"{sc.Name}: ";
                    lost ??= r.Lost != null ? where + r.Lost : null;
                    twice ??= r.Twice != null ? where + r.Twice : null;
                    older ??= r.Older != null ? where + r.Older : null;
                }
            var brokenSet = new List<string>();
            if (lost != null) brokenSet.Add("a");
            if (twice != null) brokenSet.Add("b");
            if (older != null) brokenSet.Add("c");
            bool ok = v.Expect == "broken" ? brokenSet.Count > 0
                    : v.Expect == "" ? brokenSet.Count == 0 && capped == null
                    : brokenSet.Contains(v.Expect);
            if (!ok) bad++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] readers, {v.Name}: {states} states, {finals} final, " +
                (brokenSet.Count == 0 ? (capped != null ? $"no verdict: {capped} passed the state cap" : "invariants a, b and c hold") : $"broken: {string.Join(", ", brokenSet)}") +
                (v.Expect == "" ? "" : v.Expect == "broken" ? " (expected broken)" : $" (expected {v.Expect} broken)"));
            if (lost != null) Console.WriteLine($"         (a) {lost}");
            if (twice != null) Console.WriteLine($"         (b) {twice}");
            if (older != null) Console.WriteLine($"         (c) {older}");
        }
        return bad;
    }
}
