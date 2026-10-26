// The XUSB companion's WAIT_FOR_INPUT under xusb22's rule
// (docs/design/signal-gate.md, "The companion's reports", and
// driver/companion.c, CompanionReportLocked): every interleaving of the
// SDK's writer, the pump's turns on the doorbell and the kick, GET_STATE's
// turns or the 8 ms timer's, and several readers, each a handle of its own
// that keeps one or two WAIT_FOR_INPUT requests outstanding and sends one
// again the moment it completes, each request's dispatch on a thread of its
// own.
//
// xusb22.sys 10.0.26100.9549 completes, on each report from the pad, every
// wait parked through any handle, in the order they were parked, all with
// the same bytes, and the pad reports a change of its state
// (XInputControllerDevice::HandleIncomingStateReport and
// ProcessWaitRequests). Here a report is a frame whose state differs from
// the last report's, found by the first turn that reads it, and a request
// is parked before a frame when its first look at the section came before
// the frame was written. Each step is one critical section: a turn's start
// (ReportLock and its read of the section), one take from the queue, the
// put back, a completion, or a dispatch's look at the section, its park in
// the queue, or its count and its second look. A reader's send and its
// first look are one step, since nothing of the companion's sees the
// request before its park. The SDK's store and its read of the count, and a
// dispatch's increment and its second look, face each other with a full
// fence between: either the SDK finds the count or the second look finds
// the frame, so the increment and the look are one step, and the model
// needs no store buffer (CompanionModel and Round6FenceModel check the
// fences). A frame's state is a small number,
// and two frames may share one. The SDK here never starts again, so its
// SeqNo only grows, and the frame a look finds stands for the observation
// the companion counts (ObservedGen): of two looks the later one finds a
// greater frame exactly when its observation is greater. A program before
// 2.1.0 counts from SeqNo 0 again and can repeat the SeqNo of the last frame
// read. The companion then tells the two frames apart by their GIP bytes
// (companion.c, ObserveLocked), and a frame that repeats both carries no
// new state. The wake harness places that order (hm_case_wake.c,
// seqno-again).
//
// Invariants:
//   a. With the doorbell and no timer, in a final state no parked request
//      looked before the frame of the last report, and none looked before
//      the newest frame while that frame's state differs from the last
//      report's.
//   b. A completion carries a report whose frame was written after the
//      request first looked.
//   c. No reader gets an older report after a newer one.
//   d. A turn completes requests in the order they were parked.
//   e. Two reports in a row differ, and two GET_STATE answers with one
//      packet number carry one state.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

internal static class ReadersModel
{
    // Gated: the SDK rings only while the count of requests held is not
    // zero. Kick: a dispatch looks at the section again after its count
    // and kicks the pump when it finds another SeqNo. LateDrain: a kick's
    // turn completes, with the last report, a request that looked before
    // that report's frame. ParkRule: a report completes only requests that
    // looked before its frame, else every request parked. InLock: a turn
    // completes inside ReportLock, else after it. ChangeOnly: a report is a
    // change of state, else every new frame. RequeueHead: a turn puts the
    // requests it keeps back at the head of the queue, else at its tail.
    internal sealed record Variant(string Name, bool Gated, bool Kick, bool LateDrain, bool ParkRule, bool InLock, bool ChangeOnly,
                                   bool RequeueHead, string Expect);

    internal static readonly Variant[] Variants =
    {
        new("this driver: a report on each change of state completes every request that looked before its frame, in order, inside ReportLock; a kick's turn completes a request the report missed",
            true, true, true, true, true, true, true, ""),
        new("this driver under an SDK before 2.1.0: a doorbell on every frame",
            false, true, true, true, true, true, true, ""),
        new("this driver without the kick",
            true, false, true, true, true, true, true, "a"),
        new("this driver with a kick's turn that completes nothing without a new report",
            true, true, false, true, true, true, true, "a"),
        new("this driver completing every request parked when a report comes, whenever it looked",
            true, true, true, false, true, true, true, "b"),
        new("this driver completing after ReportLock",
            true, true, true, true, false, true, true, "c"),
        new("this driver putting the requests it keeps back at the tail of the queue",
            true, true, true, true, true, true, false, "d"),
        new("this driver with a report on every new frame, changed or not",
            true, true, true, true, true, false, true, "e"),
    };

    // Requests: each reader's requests. States: each frame's state, in the
    // order the SDK writes them. Polls: GET_STATEs, each a turn of its own
    // at any moment. Doorbell: the SDK makes one. Without it the 8 ms
    // timer's Ticks turns report.
    internal sealed record Scenario(string Name, int[] Requests, int[] States, int Polls, bool Doorbell, int Ticks);

    internal static readonly Scenario[] Scenarios =
    {
        new("two readers, one request each, frames A B", new[] { 1, 1 }, new[] { 1, 2 }, 0, true, 0),
        new("two readers, one request each, frames A B A", new[] { 1, 1 }, new[] { 1, 2, 1 }, 0, true, 0),
        new("three readers, one request each, frame A", new[] { 1, 1, 1 }, new[] { 1 }, 0, true, 0),
        new("two readers, two requests and one, frames A B", new[] { 2, 1 }, new[] { 1, 2 }, 0, true, 0),
        new("two readers, one request each, frames A A B, one GET_STATE", new[] { 1, 1 }, new[] { 1, 1, 2 }, 1, true, 0),
        new("one reader, two requests, frames A B, one GET_STATE", new[] { 2 }, new[] { 1, 2 }, 1, true, 0),
        new("two readers, one request each, frames A B, no doorbell, two ticks", new[] { 1, 1 }, new[] { 1, 2 }, 0, false, 2),
    };

    // A request is idle once completed, and its reader sends it again. It
    // is out from its send while its dispatch runs and while it is parked,
    // and taken while a turn holds it for a completion after ReportLock.
    const int Q_IDLE = 0, Q_OUT = 1, Q_TAKEN = 2;
    // A dispatch thread's steps after its send and first look. A request
    // completed while the thread that parked it is still before its second
    // look is sent again on another thread, so there is one thread more
    // than requests.
    const int D_FREE = 0, D_FWD = 2, D_LOOK2 = 4;
    // A turn's steps: ReportLock and the read, the takes, the put back,
    // then the completions after ReportLock.
    const int T_IDLE = 0, T_START = 1, T_DRAIN = 2, T_REQUEUE = 3, T_DONE = 4;
    // The turns: the pump's, GET_STATE's and the timer's, as ReportLock's
    // holder.
    const int PUMP = 1, POLL = 2, TIMER = 3;

    sealed class Turn
    {
        public int Pc;
        public bool Late;
        public List<int> Kept = new();
        public List<(int Q, int Report, int Frame)> Taken = new();
        public Turn Clone() => new() { Pc = Pc, Late = Late, Kept = new List<int>(Kept), Taken = new(Taken) };
        public void Key(StringBuilder b)
        {
            b.Append(Pc).Append(Late ? 'l' : '-').Append('[').Append(string.Join(".", Kept)).Append(']');
            foreach (var t in Taken) b.Append(t.Q).Append(':').Append(t.Report).Append(':').Append(t.Frame).Append(';');
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
        public int Written, SdkPc;                 // SdkPc 0: write the next frame, 1: read the count
        public bool Doorbell, Kick;
        public int Count;
        public bool HaveReport;
        public int LastState, ReportFrame, Reports;
        public int Lock;                           // ReportLock: 0 free, else the turn's PUMP, POLL or TIMER
        public Turn Pump = new(), Poll = new(), Timer = new();
        public int PollsLeft, TicksLeft;
        // ParkNo: the order requests entered the queue, among those out.
        public int[] Q = Array.Empty<int>(), LookFrame = Array.Empty<int>(), ParkNo = Array.Empty<int>();
        // DFrame: the frame a dispatch's first look found, its own copy.
        public int[] Dpc = Array.Empty<int>(), Dq = Array.Empty<int>(), DFrame = Array.Empty<int>();
        public List<int> Queue = new();
        public int NextParkNo;
        public int[] Got = Array.Empty<int>();     // each reader's last report, 0 for none
        public int LastPollReports = -1, LastPollState = -1;
        public Path? Steps;
        public string Trace => Steps?.ToString() ?? "";
        public void Add(string step) => Steps = new Path(Steps, step);

        public St Clone()
        {
            var s = (St)MemberwiseClone();
            s.Pump = Pump.Clone(); s.Poll = Poll.Clone(); s.Timer = Timer.Clone();
            s.Q = (int[])Q.Clone(); s.LookFrame = (int[])LookFrame.Clone(); s.ParkNo = (int[])ParkNo.Clone();
            s.Dpc = (int[])Dpc.Clone(); s.Dq = (int[])Dq.Clone(); s.DFrame = (int[])DFrame.Clone();
            s.Queue = new List<int>(Queue); s.Got = (int[])Got.Clone();
            return s;
        }

        public string Key()
        {
            var b = new StringBuilder();
            b.Append(Written).Append(SdkPc).Append(Doorbell ? 'd' : '-').Append(Kick ? 'k' : '-').Append(Count).Append(',')
             .Append(HaveReport ? 'r' : '-').Append(LastState).Append(',').Append(ReportFrame).Append(',').Append(Reports).Append(',')
             .Append(Lock).Append(',').Append(PollsLeft).Append(',').Append(TicksLeft).Append('|');
            Pump.Key(b); b.Append('|'); Poll.Key(b); b.Append('|'); Timer.Key(b); b.Append('|');
            // Only the order of the parks matters: their ranks.
            var parked = ParkNo.Where(x => x >= 0).OrderBy(x => x).ToList();
            b.Append(string.Join(".", Q)).Append('|').Append(string.Join(".", LookFrame)).Append('|')
             .Append(string.Join(".", ParkNo.Select(x => x < 0 ? -1 : parked.IndexOf(x)))).Append('|').Append(string.Join(".", Queue)).Append('|');
            // Any free thread takes the next send: the threads in sorted order.
            var slots = new List<string>();
            for (int d = 0; d < Dpc.Length; d++) slots.Add(Dpc[d] == D_FREE ? "F" : $"{Dpc[d]}:{Dq[d]}:{DFrame[d]}");
            slots.Sort(StringComparer.Ordinal);
            b.Append(string.Join(";", slots)).Append('|').Append(string.Join(".", Got)).Append('|')
             .Append(LastPollReports).Append(',').Append(LastPollState);
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

    internal sealed class Result
    {
        public int States, Finals, Reports;
        public bool Capped;
        public readonly Dictionary<string, string> Broken = new();   // invariant letter: its first trace
        public void Break(string inv, string why) { if (!Broken.ContainsKey(inv)) Broken[inv] = why; }
    }

    // Reader of each request, from the scenario.
    static int[] Owners(Scenario sc)
    {
        var o = new List<int>();
        for (int r = 0; r < sc.Requests.Length; r++)
            for (int k = 0; k < sc.Requests[r]; k++) o.Add(r);
        return o.ToArray();
    }

    internal static Result Explore(Variant v, Scenario sc, int maxStates = 20_000_000)
    {
        int[] owner = Owners(sc);
        int requests = owner.Length;
        var start = new St
        {
            Q = new int[requests], LookFrame = new int[requests], ParkNo = Enumerable.Repeat(-1, requests).ToArray(),
            Dpc = new int[requests + 1], Dq = Enumerable.Repeat(-1, requests + 1).ToArray(), DFrame = new int[requests + 1],
            Got = new int[sc.Requests.Length], PollsLeft = sc.Polls, TicksLeft = sc.Doorbell ? 0 : sc.Ticks,
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
            // A variant expected to break needs only its first break.
            if (v.Expect != "" && res.Broken.ContainsKey(v.Expect)) break;
            var next = new List<St>();
            Steps(s, v, sc, owner, next, res);
            if (next.Count == 0)
            {
                res.Finals++;
                res.Reports = Math.Max(res.Reports, s.Reports);
                Final(s, sc, owner, res);
                continue;
            }
            foreach (var n in next)
                if (seen.Add(n.Hash())) stack.Push(n);
        }
        return res;
    }

    // Invariant a, in a final state, and that every request waits there.
    static void Final(St s, Scenario sc, int[] owner, Result res)
    {
        for (int q = 0; q < owner.Length; q++)
            if (s.Q[q] != Q_OUT || !s.Queue.Contains(q))
            {
                res.Break("a", $"a final state with request {q} not parked, after{s.Trace}");
                return;
            }
        if (!sc.Doorbell) return;
        foreach (int q in s.Queue)
        {
            if (s.HaveReport && s.LookFrame[q] < s.ReportFrame)
                res.Break("a", $"request {q} of reader {owner[q]}, which looked at frame {s.LookFrame[q]}, waits beside report {s.Reports} of frame {s.ReportFrame}, after{s.Trace}");
            if (s.Written >= 1 && s.LookFrame[q] < s.Written && (!s.HaveReport || sc.States[s.Written - 1] != s.LastState))
                res.Break("a", $"request {q} of reader {owner[q]}, which looked at frame {s.LookFrame[q]}, waits while frame {s.Written}'s state {sc.States[s.Written - 1]} was never reported, after{s.Trace}");
        }
    }

    static void Deliver(St s, int q, int report, int frame, int[] owner, Result res)
    {
        int r = owner[q];
        if (s.LookFrame[q] >= frame)
            res.Break("b", $"request {q} of reader {r} looked at frame {s.LookFrame[q]} and got report {report} of frame {frame}, after{s.Trace}");
        if (report < s.Got[r])
            res.Break("c", $"reader {r} got report {report} after report {s.Got[r]}, after{s.Trace}");
        s.Got[r] = Math.Max(s.Got[r], report);
        s.Q[q] = Q_IDLE;
        s.ParkNo[q] = -1;
    }

    static Turn TurnOf(St s, int who) => who == PUMP ? s.Pump : who == POLL ? s.Poll : s.Timer;
    static string NameOf(int who) => who == PUMP ? "pump" : who == POLL ? "get-state" : "timer";

    static void Steps(St s, Variant v, Scenario sc, int[] owner, List<St> next, Result res)
    {
        St n;
        // The SDK: the frame's store, then, with a doorbell, its read of the
        // count and the ring.
        if (s.SdkPc == 0 && s.Written < sc.States.Length)
        {
            n = s.Clone(); n.Written++; n.SdkPc = sc.Doorbell ? 1 : 0; n.Add($" sdk:write{n.Written}"); next.Add(n);
        }
        else if (s.SdkPc == 1)
        {
            n = s.Clone(); n.SdkPc = 0;
            bool ring = !v.Gated || n.Count != 0;
            if (ring) n.Doorbell = true;
            n.Add(ring ? " sdk:ring" : " sdk:no-ring"); next.Add(n);
        }

        // Each request its reader sends again once it completed, on a free
        // thread, which looks at the section.
        for (int q = 0; q < s.Q.Length; q++)
        {
            if (s.Q[q] != Q_IDLE) continue;
            int d = Array.IndexOf(s.Dpc, D_FREE);
            if (d < 0) break;
            n = s.Clone(); n.Q[q] = Q_OUT; n.Dq[d] = q; n.DFrame[d] = n.Written; n.LookFrame[q] = n.Written; n.Dpc[d] = D_FWD;
            n.Add($" r{owner[q]}:send{q}(t{d})-f{n.Written}"); next.Add(n);
        }
        // Each dispatch thread's next step.
        for (int d = 0; d < s.Dpc.Length; d++)
        {
            if (s.Dpc[d] == D_FREE) continue;
            int q = s.Dq[d];
            n = s.Clone();
            switch (s.Dpc[d])
            {
                case D_FWD:
                    n.Queue.Add(q); n.ParkNo[q] = n.NextParkNo++; n.Dpc[d] = D_LOOK2; n.Add($" t{d}:park{q}");
                    break;
                case D_LOOK2:
                {
                    n.Count++;
                    bool moved = n.Written != n.DFrame[d];
                    if (v.Kick && moved) n.Kick = true;
                    n.Dpc[d] = D_FREE; n.Dq[d] = -1; n.DFrame[d] = 0;
                    n.Add($" t{d}:count-look-again{(v.Kick && moved ? "-kick" : "")}");
                    break;
                }
            }
            next.Add(n);
        }

        // The pump: a turn on the doorbell or the kick.
        if (s.Pump.Pc == T_IDLE)
        {
            if (s.Doorbell) { n = s.Clone(); n.Doorbell = false; n.Pump.Pc = T_START; n.Pump.Late = false; n.Add(" pump:doorbell"); next.Add(n); }
            if (s.Kick) { n = s.Clone(); n.Kick = false; n.Pump.Pc = T_START; n.Pump.Late = true; n.Add(" pump:kick"); next.Add(n); }
        }
        else TurnStep(s, v, sc, owner, PUMP, next, res);
        // GET_STATE: a turn at any moment, while polls are left.
        if (s.Poll.Pc == T_IDLE)
        {
            if (s.PollsLeft > 0) { n = s.Clone(); n.PollsLeft--; n.Poll.Pc = T_START; n.Poll.Late = false; n.Add(" get-state"); next.Add(n); }
        }
        else TurnStep(s, v, sc, owner, POLL, next, res);
        // The timer: a turn at any moment, while ticks are left.
        if (s.Timer.Pc == T_IDLE)
        {
            if (s.TicksLeft > 0) { n = s.Clone(); n.TicksLeft--; n.Timer.Pc = T_START; n.Timer.Late = false; n.Add(" timer:tick"); next.Add(n); }
        }
        else TurnStep(s, v, sc, owner, TIMER, next, res);
    }

    // The end of a turn under ReportLock: GET_STATE answers the last report.
    static void EndInLock(St n, Turn t, int who, Result res)
    {
        if (who == POLL)
        {
            int state = n.HaveReport ? n.LastState : 0;
            if (n.LastPollReports == n.Reports && n.LastPollState != state)
                res.Break("e", $"GET_STATE answered packet {n.Reports} with state {state} after state {n.LastPollState}, after{n.Trace}");
            n.LastPollReports = n.Reports; n.LastPollState = state;
        }
        n.Lock = 0;
        t.Pc = t.Taken.Count > 0 ? T_DONE : T_IDLE;
    }

    static void TurnStep(St s, Variant v, Scenario sc, int[] owner, int who, List<St> next, Result res)
    {
        string name = NameOf(who);
        St n;
        Turn t;
        switch (TurnOf(s, who).Pc)
        {
            case T_START:
            {
                if (s.Lock != 0) return;   // another turn holds ReportLock
                n = s.Clone(); t = TurnOf(n, who);
                n.Lock = who;
                bool fresh = false;
                if (n.Written >= 1)
                {
                    int state = sc.States[n.Written - 1];
                    bool report = v.ChangeOnly ? !n.HaveReport || state != n.LastState : n.Written != n.ReportFrame;
                    if (report)
                    {
                        if (n.HaveReport && state == n.LastState)
                            res.Break("e", $"report {n.Reports + 1} of frame {n.Written} repeats state {state}, after{n.Trace} {name}:read");
                        n.HaveReport = true; n.LastState = state; n.ReportFrame = n.Written; n.Reports++;
                        fresh = true;
                    }
                }
                bool drain = n.HaveReport && (fresh || (t.Late && v.LateDrain));
                n.Add($" {name}:read-f{n.Written}{(fresh ? $"-report{n.Reports}" : "")}");
                if (!drain) EndInLock(n, t, who, res);
                else t.Pc = T_DRAIN;
                next.Add(n);
                return;
            }
            case T_DRAIN:
            {
                n = s.Clone(); t = TurnOf(n, who);
                if (n.Queue.Count == 0) { t.Pc = T_REQUEUE; n.Add($" {name}:queue-empty"); next.Add(n); return; }
                int q = n.Queue[0];
                n.Queue.RemoveAt(0);
                bool eligible = !v.ParkRule || n.LookFrame[q] < n.ReportFrame;
                if (!eligible) { t.Kept.Add(q); n.Add($" {name}:keep{q}"); next.Add(n); return; }
                n.Count--;
                // The queue keeps the order of the parks: no request still in
                // it was parked before the one completed.
                foreach (int x in n.Queue)
                    if (n.ParkNo[x] < n.ParkNo[q])
                        res.Break("d", $"a turn completed request {q} ahead of request {x}, parked before it, after{n.Trace}");
                n.Add($" {name}:take{q}");
                if (v.InLock) Deliver(n, q, n.Reports, n.ReportFrame, owner, res);
                else { n.Q[q] = Q_TAKEN; t.Taken.Add((q, n.Reports, n.ReportFrame)); }
                next.Add(n);
                return;
            }
            case T_REQUEUE:
            {
                n = s.Clone(); t = TurnOf(n, who);
                if (v.RequeueHead) n.Queue.InsertRange(0, t.Kept); else n.Queue.AddRange(t.Kept);
                n.Add($" {name}:put-back[{string.Join(".", t.Kept)}]");
                t.Kept.Clear();
                EndInLock(n, t, who, res);
                next.Add(n);
                return;
            }
            case T_DONE:
            {
                n = s.Clone(); t = TurnOf(n, who);
                var (q, report, frame) = t.Taken[0];
                t.Taken.RemoveAt(0);
                n.Add($" {name}:complete{q}-report{report}");
                Deliver(n, q, report, frame, owner, res);
                if (t.Taken.Count == 0) t.Pc = T_IDLE;
                next.Add(n);
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
            string? capped = null;
            var broken = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var sc in Scenarios)
            {
                var r = Explore(v, sc);
                states += r.States; finals += r.Finals;
                if (r.Capped) capped ??= sc.Name;
                foreach (var (inv, why) in r.Broken)
                    if (!broken.ContainsKey(inv)) broken[inv] = $"{sc.Name}: {why}";
                if (v.Expect != "" && broken.ContainsKey(v.Expect)) break;
            }
            bool ok = v.Expect == "" ? broken.Count == 0 && capped == null : broken.ContainsKey(v.Expect);
            if (!ok) bad++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] readers, {v.Name}: {states} states, {finals} final, " +
                (broken.Count == 0 ? (capped != null ? $"no verdict: {capped} passed the state cap" : "invariants a to e hold") : $"broken: {string.Join(", ", broken.Keys)}") +
                (v.Expect == "" ? "" : $" (expected {v.Expect} broken)"));
            foreach (var (inv, why) in broken) Console.WriteLine($"         ({inv}) {why}");
        }
        return bad;
    }
}
