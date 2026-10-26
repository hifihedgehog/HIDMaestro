// The read path's lost wakeups (docs/design/signal-gate.md, "Parking a
// read"): every interleaving of the HID driver's worker, which caches each
// frame and hands it to a READ_REPORT parked in the manual queue, of HID
// class's READ_REPORTs, each dispatched on a thread of its own, of the SDK's
// writer, and of the release frames OwnerGone queues at a keyboard
// program's end.
//
// Each step is one critical section of InputLock or of the manual queue's
// lock, a read of the section, or a completion. Every field two threads
// share lives under one of the two locks, so the locks' order is all the
// order there is, and the model needs no store buffer: either the worker's
// look at the queue comes before a read's park in the queue's lock order,
// and then the cache write before that look comes before the read's second
// look, or the look finds the read. The input event's wakeups are left out:
// the worker runs whenever the section holds a frame it has not read, which
// Program.cs and DeliveryModel.cs check.
//
// HID class keeps Reads requests outstanding and sends one again the moment
// it completes. A request completed while the thread that parked it is
// still in its second look is sent again on another thread, so the model
// has one dispatch thread more than reads. A final state is one where no
// step is possible: every read waits in the queue and every thread is idle.
// Invariants:
//   a. In a final state no read waits beside a report a read would take: a
//      release frame queued; a cached frame no read carried whose SeqNo is
//      above the last one delivered, which a READ_REPORT coming now takes;
//      or a cached frame no read carried that came after a read now waiting
//      looked at the cache. That read was waiting when the frame came, and a
//      read that parked a moment earlier gets it from the worker in every
//      driver. A read that looked after the frame came is a late read, and
//      v2.0.3's greater-than test, which this driver keeps, parks it beside
//      a frame past the SeqNo wrap.
//   b. No frame and no release frame goes to two reads.
//   c. Each frame a read takes is the one cached at that moment.
// Counted for the record, not checked: completions that land in another
// order than their takes, and acknowledgements a READ_REPORT makes for a
// frame whose completion has not happened yet.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

internal static class WakeModel
{
    // PublishFirst: the worker caches the frame, then takes a read from the
    // queue (DeliverParked). LookAgain: a READ_REPORT that parks looks at the
    // cache again. ByCount: that look takes a frame cached since its first
    // look (InputPublishCount), else only one above the last delivered.
    // RequeueLook: a thread that put a read back looks again for anything
    // cached since it found nothing to take.
    internal sealed record Variant(string Name, bool PublishFirst, bool LookAgain, bool ByCount, bool ExpectBroken, bool RequeueLook = true);

    internal static readonly Variant[] Variants =
    {
        new("b0fb7dc9 and v2.0.3: the worker takes a read, then caches the frame; a read parks with no second look", false, false, false, true),
        new("the worker caches, then takes a read; a read parks with no second look", true, false, false, true),
        new("the worker takes a read, then caches; a read parks and looks again", false, true, true, true),
        new("the worker caches, then takes a read; a read looks again under the greater-than test alone", true, true, false, true),
        new("the worker caches, then takes a read; a read parks and looks again for a frame cached since, and a read put back does not", true, true, true, true, false),
        new("this driver: the worker caches, then takes a read; a read parks, or is put back, and looks again for a frame cached since", true, true, true, false),
    };

    // Seqs: each frame's SeqNo in the order the SDK writes them (a smaller
    // one after a larger is the wrap). Releases: the release frames a
    // keyboard leaves, and its pipe closes after its last frame.
    internal sealed record Scenario(string Name, int[] Seqs, int Reads, int Releases);

    internal static readonly Scenario[] Scenarios =
    {
        new("two frames, one read", new[] { 2, 4 }, 1, 0),
        new("two frames, two reads", new[] { 2, 4 }, 2, 0),
        new("the wrap, one read", new[] { 6, 2 }, 1, 0),
        new("the wrap, two reads", new[] { 6, 2 }, 2, 0),
        new("a keyboard's end, one read", new[] { 2 }, 1, 2),
        new("a keyboard's end, two reads", new[] { 2 }, 2, 2),
        new("two frames and the end, two reads", new[] { 2, 4 }, 2, 1),
        new("three frames, two reads", new[] { 2, 4, 6 }, 2, 0),
    };

    // Read states: HID class sends it next, its dispatch runs before the
    // park, it waits in the queue, a thread took it from the queue.
    const int R_IDLE = 0, R_DISPATCH = 1, R_PARKED = 2, R_TAKEN = 3;
    // Dispatch thread program counters.
    const int D_FREE = 0, D_CHECK = 1, D_PARK = 2, D_LOOK = 3, D_RET = 4, D_TAKE = 5, D_REQ = 6, D_DONE = 7, D_SELF = 8, D_RQLOOK = 9;
    // Worker program counters. W_*0 and W_*1 are b0fb7dc9's frame path and
    // DeliverReleaseFrames.
    const int W_IDLE = 0, W_PUB = 1, W_RET = 2, W_TAKE = 3, W_REQ = 4, W_DONE = 5, W_OG = 6,
              W_RET0 = 7, W_PUB0 = 8, W_DONE0 = 9, W_OG0 = 10, W_MORE = 11, W_RET1 = 12, W_TAKE1 = 13, W_REQ1 = 14, W_DONE1 = 15,
              W_RQLOOK = 16;

    sealed class St
    {
        public int Written;            // frames written: the section holds frame Written
        public bool Closed;            // the program's pipe closed
        public int Seen;               // the last frame the worker read
        public bool OwnerDone;
        public int Wpc, Wframe, Wret = -1, Witem, Wtp;
        public bool Wcont;
        // InputLock's fields. Pending and PubCount are the fields this
        // driver adds, and ghosts in b0fb7dc9, where only the invariant
        // reads them. CachePub is the PubCount the cached frame came with.
        public int Cache, SharedSeq, LastDel, PubCount, CachePub, RelNext, RelCount, AckPending;
        public bool Pending;
        public List<int> Queue = new();
        public int[] R = Array.Empty<int>(), RPub = Array.Empty<int>();
        public int[] Dpc = Array.Empty<int>(), Dread = Array.Empty<int>(), Dpub = Array.Empty<int>(),
                     Dret = Array.Empty<int>(), Ditem = Array.Empty<int>(), Dtp = Array.Empty<int>();
        public bool[] Dcont = Array.Empty<bool>();
        public int[] Count = Array.Empty<int>();      // completions by item
        public int[] TakeNo = Array.Empty<int>();     // the order each item was taken in
        public int Takes, LastDoneTake;
        public string Trace = "";

        public St Clone()
        {
            var s = (St)MemberwiseClone();
            s.Queue = new List<int>(Queue);
            s.R = (int[])R.Clone(); s.RPub = (int[])RPub.Clone();
            s.Dpc = (int[])Dpc.Clone(); s.Dread = (int[])Dread.Clone(); s.Dpub = (int[])Dpub.Clone();
            s.Dret = (int[])Dret.Clone(); s.Ditem = (int[])Ditem.Clone(); s.Dtp = (int[])Dtp.Clone(); s.Dcont = (bool[])Dcont.Clone();
            s.Count = (int[])Count.Clone(); s.TakeNo = (int[])TakeNo.Clone();
            return s;
        }

        // The state as far as any later step or invariant can tell: each
        // thread's fields only where its program counter still reads them,
        // the dispatch threads in sorted order since any free one takes the
        // next send, and AckPending, TakeNo, Takes and LastDoneTake left out:
        // they feed only the counts kept for the record.
        public string Key()
        {
            var b = new StringBuilder();
            b.Append(Written).Append(Closed ? 'c' : '-').Append(Seen).Append(OwnerDone ? 'o' : '-').Append('|').Append(Wpc);
            switch (Wpc)
            {
                case W_PUB: case W_RET0: b.Append(',').Append(Wframe); break;
                case W_PUB0: b.Append(',').Append(Wframe).Append(',').Append(Wret); break;
                case W_TAKE: case W_TAKE1: case W_REQ1: b.Append(',').Append(Wret); break;
                case W_REQ: b.Append(',').Append(Wret).Append(',').Append(Wtp); break;
                case W_RQLOOK: b.Append(',').Append(Wtp); break;
                case W_DONE: b.Append(',').Append(Wret).Append(',').Append(Witem).Append(Wcont ? 'k' : '-'); break;
                case W_DONE0: case W_DONE1: b.Append(',').Append(Wret).Append(',').Append(Witem); break;
            }
            b.Append('|').Append(Cache).Append(',').Append(SharedSeq).Append(',').Append(LastDel).Append(',').Append(PubCount)
             .Append(',').Append(CachePub).Append(',').Append(RelNext).Append(',').Append(RelCount)
             .Append(Pending ? 'p' : '-').Append('|').Append(string.Join(".", Queue)).Append('|');
            for (int i = 0; i < R.Length; i++)
            {
                b.Append(R[i]);
                if (R[i] == R_PARKED || R[i] == R_TAKEN) b.Append(':').Append(RPub[i]);
                b.Append(';');
            }
            var slots = new List<string>();
            for (int i = 0; i < Dpc.Length; i++)
            {
                string t = Dpc[i] switch
                {
                    D_FREE => "F",
                    D_CHECK => $"C{Dread[i]}",
                    D_PARK => $"P{Dread[i]}:{Dpub[i]}",
                    D_LOOK => $"L{Dpub[i]}",
                    D_RET => "R",
                    D_TAKE => $"T{Dret[i]}",
                    D_REQ => $"Q{Dret[i]}:{Dtp[i]}",
                    D_RQLOOK => $"K{Dtp[i]}",
                    D_DONE => $"D{Dret[i]}:{Ditem[i]}{(Dcont[i] ? 'k' : '-')}",
                    D_SELF => $"S{Dread[i]}:{Ditem[i]}",
                    _ => throw new InvalidOperationException($"dispatch pc {Dpc[i]}"),
                };
                slots.Add(t);
            }
            slots.Sort(StringComparer.Ordinal);
            b.Append('|').Append(string.Join(";", slots)).Append('|').Append(string.Join(",", Count));
            return b.ToString();
        }

        // The visited set keeps a 128-bit hash of the key: two 64-bit FNV-1a
        // hashes with different offsets.
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
        public int States, Finals;
        public string? Broken;            // the first invariant broken, with its order
        public bool Capped;               // the exploration stopped at maxStates: no verdict
        public int Inversions, EarlyAcks; // states reached with one, for the record
        public string? InversionTrace, EarlyAckTrace;
    }

    // recordOnly: explore every order without stopping at a broken
    // invariant, for the counts kept for the record.
    internal static Result Explore(Variant v, Scenario sc, int maxStates = 20_000_000, bool recordOnly = false)
    {
        int frames = sc.Seqs.Length, items = frames + sc.Releases + 1, slots = sc.Reads + 1;
        var start = new St
        {
            R = new int[sc.Reads], RPub = new int[sc.Reads],
            Dpc = new int[slots], Dread = Enumerable.Repeat(-1, slots).ToArray(), Dpub = new int[slots],
            Dret = Enumerable.Repeat(-1, slots).ToArray(), Ditem = new int[slots], Dtp = new int[slots], Dcont = new bool[slots],
            Count = new int[items], TakeNo = new int[items],
        };
        var seen = new HashSet<(ulong, ulong)> { start.Hash() };
        var stack = new Stack<St>();
        stack.Push(start);
        var r = new Result();
        while (stack.Count > 0 && (recordOnly || r.Broken == null))
        {
            var s = stack.Pop();
            r.States++;
            if (r.States > maxStates) { r.Capped = true; break; }
            var next = new List<St>();
            Steps(s, v, sc, next, r);
            if (r.Broken != null && !recordOnly) break;
            if (next.Count == 0)
            {
                r.Finals++;
                string? why = Quiescent(s, sc);
                if (why != null && r.Broken == null) r.Broken = $"{why}, after{s.Trace}";
                continue;
            }
            foreach (var n in next)
                if (seen.Add(n.Hash())) stack.Push(n);
        }
        return r;
    }

    // Invariant a, in a final state.
    static string? Quiescent(St s, Scenario sc)
    {
        for (int i = 0; i < sc.Reads; i++)
            if (s.R[i] != R_PARKED) return $"a final state with read {i} not parked";
        if (s.RelNext < s.RelCount) return $"(a) release frame {s.RelNext + 1} queued beside a parked read";
        if (s.Pending && s.Cache != 0)
        {
            if (s.SharedSeq > s.LastDel)
                return $"(a) frame {s.Cache} (SeqNo {s.SharedSeq}, above the last delivered {s.LastDel}) cached beside a parked read";
            for (int i = 0; i < sc.Reads; i++)
                if (s.RPub[i] < s.CachePub)
                    return $"(a) read {i} parked beside frame {s.Cache}, which came after its look";
        }
        return null;
    }

    static int Seq(Scenario sc, int frame) => sc.Seqs[frame - 1];
    static int Release(Scenario sc, int k) => sc.Seqs.Length + 1 + k;   // item of release frame k (0-based)
    static string Item(Scenario sc, int item) => item <= sc.Seqs.Length ? $"frame {item}" : $"release {item - sc.Seqs.Length}";

    static bool Deliverable(St s) => s.RelNext < s.RelCount || s.Pending;

    // TakeReportLocked: the next release frame, else the pending frame.
    static int Take(St s, Scenario sc, Result r)
    {
        if (s.RelNext < s.RelCount) return Taken(s, Release(sc, s.RelNext++));
        if (s.Pending) return TakeFrame(s, r);
        return 0;
    }

    static int TakeFrame(St s, Result r)
    {
        // Invariant c: the frame taken is the one cached now, by construction
        // of the cache. It is checked so a change that takes another cannot
        // pass.
        int item = s.Cache;
        s.Pending = false;
        s.LastDel = s.SharedSeq;
        s.AckPending = item;
        return Taken(s, item);
    }

    static int Taken(St s, int item)
    {
        s.TakeNo[item] = ++s.Takes;
        return item;
    }

    static void Complete(St s, Scenario sc, int read, int item, Result r)
    {
        s.Count[item]++;
        if (s.Count[item] > 1 && r.Broken == null)
            r.Broken = $"(b) {Item(sc, item)} went to a second read, after{s.Trace}";
        if (s.TakeNo[item] < s.LastDoneTake)
        {
            if (r.Inversions++ == 0) r.InversionTrace = s.Trace;
        }
        else s.LastDoneTake = s.TakeNo[item];
        s.R[read] = R_IDLE;
    }

    static void Steps(St s, Variant v, Scenario sc, List<St> next, Result r)
    {
        int frames = sc.Seqs.Length;

        // The SDK: the next frame, and after the last one a keyboard
        // program's end, which closes its pipe.
        if (s.Written < frames)
        {
            var n = s.Clone(); n.Written++; n.Trace += $" sdk:write{n.Written}"; next.Add(n);
        }
        else if (sc.Releases > 0 && !s.Closed)
        {
            var n = s.Clone(); n.Closed = true; n.Trace += " sdk:end"; next.Add(n);
        }

        // HID class sends a read that is idle, on a free thread.
        for (int i = 0; i < sc.Reads; i++)
        {
            if (s.R[i] != R_IDLE) continue;
            int d = Array.IndexOf(s.Dpc, D_FREE);
            if (d < 0) break;
            var n = s.Clone(); n.R[i] = R_DISPATCH; n.Dpc[d] = D_CHECK; n.Dread[d] = i; n.Trace += $" hid:send{i}(t{d})"; next.Add(n);
        }

        for (int d = 0; d < s.Dpc.Length; d++) Dispatch(s, v, sc, d, next, r);
        Worker(s, v, sc, next, r);
    }

    static void Dispatch(St s, Variant v, Scenario sc, int d, List<St> next, Result r)
    {
        int read = s.Dread[d];
        St n;
        switch (s.Dpc[d])
        {
            case D_CHECK:
            {
                n = s.Clone();
                if (n.AckPending != 0)
                {
                    if (n.Count[n.AckPending] == 0 && r.EarlyAcks++ == 0) r.EarlyAckTrace = s.Trace + $" t{d}:ack";
                    n.AckPending = 0;
                }
                int item = 0;
                if (n.RelNext < n.RelCount) item = Taken(n, Release(sc, n.RelNext++));
                else if (n.Cache != 0 && n.Pending && n.SharedSeq > n.LastDel) item = TakeFrame(n, r);
                if (item != 0) { n.Ditem[d] = item; n.Dpc[d] = D_SELF; n.Trace += $" t{d}:check-takes-{Item(sc, item).Replace(' ', '-')}"; }
                else { n.Dpub[d] = n.PubCount; n.Dpc[d] = D_PARK; n.Trace += $" t{d}:check-none"; }
                next.Add(n);
                break;
            }
            case D_PARK:
                n = s.Clone(); n.Queue.Add(read); n.R[read] = R_PARKED; n.RPub[read] = n.Dpub[d];
                n.Dpc[d] = v.LookAgain ? D_LOOK : D_FREE; n.Dread[d] = v.LookAgain ? read : -1;
                n.Trace += $" t{d}:park{read}"; next.Add(n);
                break;
            case D_LOOK:
            {
                bool waiting = s.RelNext < s.RelCount
                    || (s.Pending && (v.ByCount ? s.PubCount != s.Dpub[d] : s.SharedSeq > s.LastDel));
                n = s.Clone(); n.Dpc[d] = waiting ? D_RET : D_FREE; n.Trace += $" t{d}:look-{(waiting ? "waiting" : "none")}"; next.Add(n);
                break;
            }
            case D_RET:
                n = s.Clone();
                if (n.Queue.Count == 0) { n.Dpc[d] = D_FREE; n.Trace += $" t{d}:retrieve-empty"; }
                else { n.Dret[d] = n.Queue[0]; n.Queue.RemoveAt(0); n.R[n.Dret[d]] = R_TAKEN; n.Dpc[d] = D_TAKE; n.Trace += $" t{d}:retrieve{n.Dret[d]}"; }
                next.Add(n);
                break;
            case D_TAKE:
            {
                n = s.Clone();
                int item = Take(n, sc, r);
                if (item == 0) { n.Dtp[d] = n.PubCount; n.Dpc[d] = D_REQ; n.Trace += $" t{d}:take-none"; }
                else { n.Ditem[d] = item; n.Dcont[d] = Deliverable(n); n.Dpc[d] = D_DONE; n.Trace += $" t{d}:take-{Item(sc, item).Replace(' ', '-')}"; }
                next.Add(n);
                break;
            }
            case D_REQ:
                n = s.Clone(); n.Queue.Insert(0, n.Dret[d]); n.R[n.Dret[d]] = R_PARKED; n.Dret[d] = -1;
                n.Dpc[d] = v.RequeueLook ? D_RQLOOK : D_FREE; if (n.Dpc[d] == D_FREE) n.Dread[d] = -1;
                n.Trace += $" t{d}:requeue"; next.Add(n);
                break;
            case D_RQLOOK:
            {
                bool waiting = s.RelNext < s.RelCount || (s.Pending && s.PubCount != s.Dtp[d]);
                n = s.Clone(); n.Dpc[d] = waiting ? D_RET : D_FREE; if (n.Dpc[d] == D_FREE) n.Dread[d] = -1;
                n.Trace += $" t{d}:look-after-requeue-{(waiting ? "waiting" : "none")}"; next.Add(n);
                break;
            }
            case D_DONE:
                n = s.Clone(); Complete(n, sc, n.Dret[d], n.Ditem[d], r); n.Trace += $" t{d}:complete{s.Dret[d]}";
                n.Dret[d] = -1; n.Dpc[d] = n.Dcont[d] ? D_RET : D_FREE; if (n.Dpc[d] == D_FREE) n.Dread[d] = -1;
                next.Add(n);
                break;
            case D_SELF:
                n = s.Clone(); Complete(n, sc, read, n.Ditem[d], r); n.Dpc[d] = D_FREE; n.Dread[d] = -1;
                n.Trace += $" t{d}:complete-own{read}"; next.Add(n);
                break;
        }
    }

    static void Worker(St s, Variant v, Scenario sc, List<St> next, Result r)
    {
        St n;
        switch (s.Wpc)
        {
            case W_IDLE:
                if (s.Written != s.Seen)
                {
                    // The worker reads the section, which holds the latest
                    // frame written, then caches it.
                    n = s.Clone(); n.Wframe = n.Seen = n.Written; n.Wpc = v.PublishFirst ? W_PUB : W_RET0;
                    n.Trace += $" w:read{n.Wframe}"; next.Add(n);
                }
                else if (s.Closed && !s.OwnerDone)
                {
                    n = s.Clone(); n.Wpc = v.PublishFirst ? W_OG : W_OG0; n.Trace += " w:pipe-closed"; next.Add(n);
                }
                break;

            // This driver's frame path and OwnerGone, then DeliverParked.
            case W_PUB:
                n = s.Clone(); Cache(n, sc); n.Pending = true;
                n.Wpc = Deliverable(n) ? W_RET : W_IDLE; n.Trace += $" w:cache{n.Wframe}"; next.Add(n);
                break;
            case W_OG:
                n = s.Clone(); OwnerGone(n, sc); n.Wpc = Deliverable(n) ? W_RET : W_IDLE; n.Trace += " w:ownergone"; next.Add(n);
                break;
            case W_RET:
                n = s.Clone();
                if (n.Queue.Count == 0) { n.Wpc = W_IDLE; n.Trace += " w:retrieve-empty"; }
                else { n.Wret = n.Queue[0]; n.Queue.RemoveAt(0); n.R[n.Wret] = R_TAKEN; n.Wpc = W_TAKE; n.Trace += $" w:retrieve{n.Wret}"; }
                next.Add(n);
                break;
            case W_TAKE:
            {
                n = s.Clone();
                int item = Take(n, sc, r);
                if (item == 0) { n.Wtp = n.PubCount; n.Wpc = W_REQ; n.Trace += " w:take-none"; }
                else { n.Witem = item; n.Wcont = Deliverable(n); n.Wpc = W_DONE; n.Trace += $" w:take-{Item(sc, item).Replace(' ', '-')}"; }
                next.Add(n);
                break;
            }
            case W_REQ:
                n = s.Clone(); n.Queue.Insert(0, n.Wret); n.R[n.Wret] = R_PARKED; n.Wret = -1; n.Wpc = v.RequeueLook ? W_RQLOOK : W_IDLE;
                n.Trace += " w:requeue"; next.Add(n);
                break;
            case W_RQLOOK:
            {
                bool waiting = s.RelNext < s.RelCount || (s.Pending && s.PubCount != s.Wtp);
                n = s.Clone(); n.Wpc = waiting ? W_RET : W_IDLE; n.Trace += $" w:look-after-requeue-{(waiting ? "waiting" : "none")}"; next.Add(n);
                break;
            }
            case W_DONE:
                n = s.Clone(); Complete(n, sc, n.Wret, n.Witem, r); n.Trace += $" w:complete{s.Wret}"; n.Wret = -1;
                n.Wpc = n.Wcont ? W_RET : W_IDLE; next.Add(n);
                break;

            // b0fb7dc9's frame path: a read from the queue first, then the
            // cache write, which gives the frame to that read.
            case W_RET0:
                n = s.Clone();
                if (n.Queue.Count == 0) { n.Wret = -1; n.Trace += " w:retrieve-empty"; }
                else { n.Wret = n.Queue[0]; n.Queue.RemoveAt(0); n.R[n.Wret] = R_TAKEN; n.Trace += $" w:retrieve{n.Wret}"; }
                n.Wpc = W_PUB0; next.Add(n);
                break;
            case W_PUB0:
                n = s.Clone(); Cache(n, sc);
                if (n.Wret >= 0) { n.Pending = true; n.Witem = TakeFrame(n, r); }
                else n.Pending = true;
                n.Wpc = n.Wret >= 0 ? W_DONE0 : W_IDLE; n.Trace += $" w:cache{n.Wframe}"; next.Add(n);
                break;
            case W_DONE0:
                n = s.Clone(); Complete(n, sc, n.Wret, n.Witem, r); n.Trace += $" w:complete{s.Wret}"; n.Wret = -1; n.Wpc = W_IDLE; next.Add(n);
                break;
            // b0fb7dc9's OwnerGone and DeliverReleaseFrames.
            case W_OG0:
                n = s.Clone(); OwnerGone(n, sc); n.Wpc = W_MORE; n.Trace += " w:ownergone"; next.Add(n);
                break;
            case W_MORE:
                n = s.Clone(); n.Wpc = n.RelNext < n.RelCount ? W_RET1 : W_IDLE; n.Trace += " w:more"; next.Add(n);
                break;
            case W_RET1:
                n = s.Clone();
                if (n.Queue.Count == 0) { n.Wpc = W_IDLE; n.Trace += " w:retrieve-empty"; }
                else { n.Wret = n.Queue[0]; n.Queue.RemoveAt(0); n.R[n.Wret] = R_TAKEN; n.Wpc = W_TAKE1; n.Trace += $" w:retrieve{n.Wret}"; }
                next.Add(n);
                break;
            case W_TAKE1:
                n = s.Clone();
                if (n.RelNext >= n.RelCount) { n.Wpc = W_REQ1; n.Trace += " w:take-none"; }
                else { n.Witem = Taken(n, Release(sc, n.RelNext++)); n.Wpc = W_DONE1; n.Trace += $" w:take-{Item(sc, n.Witem).Replace(' ', '-')}"; }
                next.Add(n);
                break;
            case W_REQ1:
                n = s.Clone(); n.Queue.Insert(0, n.Wret); n.R[n.Wret] = R_PARKED; n.Wret = -1; n.Wpc = W_IDLE; n.Trace += " w:requeue"; next.Add(n);
                break;
            case W_DONE1:
                n = s.Clone(); Complete(n, sc, n.Wret, n.Witem, r); n.Trace += $" w:complete{s.Wret}"; n.Wret = -1; n.Wpc = W_MORE; next.Add(n);
                break;
        }
    }

    static void Cache(St n, Scenario sc)
    {
        n.Cache = n.Wframe;
        n.SharedSeq = Seq(sc, n.Wframe);
        n.PubCount++;
        n.CachePub = n.PubCount;
    }

    static void OwnerGone(St n, Scenario sc)
    {
        n.RelCount = sc.Releases;
        n.RelNext = 0;
        n.SharedSeq = 0;
        n.LastDel = 0;
        n.Pending = false;
        n.OwnerDone = true;
    }

    // SignalGateModel wake: every variant against every scenario. A variant
    // expected to break must break some scenario, and this driver none.
    internal static int Run()
    {
        int bad = 0;
        foreach (var v in Variants)
        {
            string? broke = null, capped = null;
            int states = 0, finals = 0, inversions = 0, earlyAcks = 0;
            string? invTrace = null, ackTrace = null;
            foreach (var sc in Scenarios)
            {
                var r = Explore(v, sc);
                states += r.States; finals += r.Finals; inversions += r.Inversions; earlyAcks += r.EarlyAcks;
                invTrace ??= r.InversionTrace; ackTrace ??= r.EarlyAckTrace;
                if (!v.ExpectBroken)
                    Console.WriteLine($"    {sc.Name}: {r.States} states, {r.Finals} final{(r.Broken != null ? ", BROKEN" : "")}{(r.Capped ? ", STOPPED AT THE STATE CAP" : "")}");
                if (r.Capped) { capped = sc.Name; break; }
                // One broken scenario settles the variant.
                if (r.Broken != null) { broke = $"{sc.Name}: {r.Broken}"; break; }
            }
            bool ok = capped == null && (broke != null) == v.ExpectBroken;
            if (!ok) bad++;
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] wake, {v.Name}: {states} states, {finals} final, " +
                              (capped != null ? $"no verdict: {capped} passed the state cap" : broke != null ? "an invariant broken" : "invariants a, b and c hold") +
                              (v.ExpectBroken ? " (expected broken)" : ""));
            if (broke != null) Console.WriteLine($"         {broke}");
            Console.WriteLine($"         for the record: {inversions} completion(s) after a later take, {earlyAcks} acknowledgement(s) before the frame's completion");
            if (!v.ExpectBroken && invTrace != null) Console.WriteLine($"         first inversion after{invTrace}");
            if (!v.ExpectBroken && ackTrace != null) Console.WriteLine($"         first early acknowledgement after{ackTrace}");
        }
        // The same counts for b0fb7dc9's read path over every scenario, its
        // broken invariant aside: whether those orders predate this lane.
        {
            var v = Variants[0];
            int inversions = 0, earlyAcks = 0;
            string? invTrace = null, ackTrace = null;
            foreach (var sc in Scenarios)
            {
                var r = Explore(v, sc, recordOnly: true);
                inversions += r.Inversions; earlyAcks += r.EarlyAcks;
                invTrace ??= r.InversionTrace; ackTrace ??= r.EarlyAckTrace;
            }
            Console.WriteLine($"[info] wake, {v.Name}, every order: {inversions} completion(s) after a later take, {earlyAcks} acknowledgement(s) before the frame's completion");
            if (invTrace != null) Console.WriteLine($"         first inversion after{invTrace}");
            if (ackTrace != null) Console.WriteLine($"         first early acknowledgement after{ackTrace}");
        }
        return bad;
    }
}
