// Two or more programs that embed HIDMaestro on one machine.
//
//   D. A program killed with TerminateProcess. The next sweep of a live
//      program, or the start of a new one, removes the killed program's
//      devices and releases its registry ledger holds, and leaves the live
//      program's controller running.
//   G. Two composite controllers on USB/IP. One program's sweep leaves
//      the other's import attached. Killing one lets the other remove it.
//   H. A program killed while a child process it started still runs. Its
//      ownership claim goes with it, and another program can take the
//      same identity at once.
//
// Each program is a peer process, this binary started with --peer.
// Requires
// elevation. Exit 0 PASS / 1 FAIL / 2 when another program that embeds
// HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Gaming.Input;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static bool Ok(string reply) => reply.StartsWith("OK", StringComparison.Ordinal);

    static bool WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
        return cond();
    }

    /// <summary>Another process that embeds the SDK: one with
    /// HIDMaestro.Core.dll loaded, or one an owner record names.</summary>
    static string? FindLiveSdkConsumer()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            if (p.Id == self) { p.Dispose(); continue; }
            try
            {
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                        return p.ProcessName;
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var (_, pid) in Native.OwnerRecords())
        {
            if (pid == self) continue;
            try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
            catch { }
        }
        return null;
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--peer") return Peer.Run();

        Console.WriteLine("=== Programs that embed HIDMaestro, side by side ===");
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }

        string only = args.Length > 0 ? args[0].ToUpperInvariant() : "ABCDEFGH";
        Clean();
        var sections = new (char Id, string Title, Action Body)[]
        {
            ('D', "a killed program's devices", SectionD),
            ('G', "two composite controllers", SectionG),
            ('H', "a program killed while its child runs", SectionH),
        };
        foreach (var (id, title, body) in sections)
        {
            if (!only.Contains(id)) continue;
            Console.WriteLine();
            Console.WriteLine($"{id}. {title}");
            try { body(); }
            catch (Exception ex) { Check($"{id} ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
            Clean();
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>Remove every controller the sections left, through a peer
    /// so the probe itself never loads the SDK, and put the driver back.</summary>
    static void Clean()
    {
        using var p = PeerProcess.Start("cleanup");
        p.Send("everything");
        p.Send("install");
    }

    static List<string> WaitPaths(ushort vid, ushort pid, int count, int ms, ICollection<string>? exclude = null)
    {
        List<string> found = new();
        WaitFor(() =>
        {
            found = Native.HidPaths(vid, pid).Where(p => exclude == null || !exclude.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            return found.Count >= count;
        }, ms);
        return found;
    }

    /// <summary>The input a device carried in one window, as distinct
    /// (buttons, X) pairs.</summary>
    static List<(string Buttons, uint X)> Patterns(HidCapture cap, long from, long to) =>
        cap.Since(from, to).Select(r => cap.Decode(r.Report)).Distinct().ToList();

    static bool Flows(HidCapture cap, int ms = 600)
    {
        long mark = cap.Mark();
        Thread.Sleep(ms);
        return cap.Since(mark).Count > 0;
    }

    static string Show(List<(string Buttons, uint X)> p) =>
        p.Count == 0 ? "(no reports)" : string.Join(", ", p.Take(4).Select(t => $"buttons {t.Buttons} X {t.X}")) + (p.Count > 4 ? $" (+{p.Count - 4})" : "");

    // ── D ──────────────────────────────────────────────────────────────

    static void SectionD()
    {
        using var p1 = PeerProcess.Start("D1");
        using var p2 = PeerProcess.Start("D2");
        p1.Send("install");
        string c1 = p1.Send("create pad F0D5 coexist-d-killed");
        p2.Send("install");
        string c2 = p2.Send("create pad F0D6 coexist-d-live");
        if (!Ok(c1) || !Ok(c2)) { Check("D: both programs create", false, $"{c1} / {c2}"); return; }
        p2.Send("stream 4 0.3 125 0");
        string inst1 = PeerProcess.Field(c1, "instance")!, inst2 = PeerProcess.Field(c2, "instance")!;
        var tree1 = Native.Subtree(inst1);
        bool held1 = Native.LedgerHolds("JoystickOem", "VID_BEEF&PID_F0D5");
        bool held2 = Native.LedgerHolds("JoystickOem", "VID_BEEF&PID_F0D6");
        var path2 = WaitPaths(0xBEEF, 0xF0D6, 1, 10_000);
        if (path2.Count != 1) { Check("D: the live program's device is present", false); return; }
        using var cap2 = HidCapture.Open(path2[0]);
        if (cap2 == null) { Check("D: the live program's device opens", false); return; }

        p1.Kill();
        Check("D: the killed program's devices are still there before anything sweeps", tree1.Count > 0 && tree1.All(Native.IsPresent));
        string s = p2.Send("sweep");
        Check("D: the live program's sweep runs", Ok(s), s);
        WaitFor(() => tree1.All(Native.IsGone), 5000);
        Check("D: the live program's sweep removes the killed program's devices", tree1.All(Native.IsGone),
              string.Join(", ", tree1.Where(t => !Native.IsGone(t))));
        if (held1)
            Check("D: the killed program's registry ledger hold is released", !Native.LedgerHolds("JoystickOem", "VID_BEEF&PID_F0D5"));
        Check("D: the sweep leaves the live program's controller", Native.IsStarted(inst2) && !cap2.Broken && Flows(cap2),
              cap2.Broken ? $"read error {cap2.ReadError}" : "");
        if (held2)
            Check("D: the live program's ledger hold stays", Native.LedgerHolds("JoystickOem", "VID_BEEF&PID_F0D6"));

        // The next program's start does the same.
        using var p3 = PeerProcess.Start("D3");
        p3.Send("install");
        string c3 = p3.Send("create pad F0D7 coexist-d-killed-2");
        if (!Ok(c3)) { Check("D: a third program creates", false, c3); return; }
        var tree3 = Native.Subtree(PeerProcess.Field(c3, "instance")!);
        p3.Kill();
        using var p4 = PeerProcess.Start("D4");
        string i4 = p4.Send("install");
        WaitFor(() => tree3.All(Native.IsGone), 5000);
        Check("D: a new program's start removes a killed program's devices", Ok(i4) && tree3.All(Native.IsGone),
              string.Join(", ", tree3.Where(t => !Native.IsGone(t))));
        Check("D: a new program's start leaves the live program's controller", Native.IsStarted(inst2) && !cap2.Broken && Flows(cap2));
    }

    // ── G ──────────────────────────────────────────────────────────────

    static void SectionG()
    {
        var before = Native.HidPaths(0x054C, 0x0CE6);
        using var p1 = PeerProcess.Start("G1");
        p1.Send("install");
        string c1 = p1.Send("create composite - coexist-g-a");
        var pathA = WaitPaths(0x054C, 0x0CE6, 1, 20_000, before);
        if (!Ok(c1) || pathA.Count != 1) { Check("G: the first composite appears", false, $"{c1}, {pathA.Count} interfaces"); return; }

        using var p2 = PeerProcess.Start("G2");
        p2.Send("install");
        string c2 = p2.Send("create composite - coexist-g-b");
        var pathB = WaitPaths(0x054C, 0x0CE6, 1, 20_000, before.Concat(pathA).ToList());
        Check("G: the second program's start leaves the first composite", Native.HidPaths(0x054C, 0x0CE6).Contains(pathA[0], StringComparer.OrdinalIgnoreCase));
        if (!Ok(c2) || pathB.Count != 1) { Check("G: the second composite appears", false, $"{c2}, {pathB.Count} interfaces"); return; }
        using (var capB = HidCapture.Open(pathB[0]))
        {
            if (capB == null) { Check("G: the second composite opens", false); return; }
            p2.Send("stream 7 0.6 125 0");
            string s = p1.Send("sweep");
            Thread.Sleep(1000);
            Check("G: one program's sweep leaves the other's import attached",
                  Ok(s) && !capB.Broken && Flows(capB) && Native.HidPaths(0x054C, 0x0CE6).Contains(pathB[0], StringComparer.OrdinalIgnoreCase),
                  capB.Broken ? $"read error {capB.ReadError}" : "");
        }
        p2.Kill();
        string s2 = p1.Send("sweep");
        bool gone = WaitFor(() => !Native.HidPaths(0x054C, 0x0CE6).Contains(pathB[0], StringComparer.OrdinalIgnoreCase), 15_000);
        Check("G: after the second program is killed, the first's sweep detaches its import", Ok(s2) && gone);
        Check("G: the first composite is still there", Native.HidPaths(0x054C, 0x0CE6).Contains(pathA[0], StringComparer.OrdinalIgnoreCase));
    }

    // ── H ──────────────────────────────────────────────────────────────

    static void SectionH()
    {
        Process? child = null;
        try
        {
            using (var p1 = PeerProcess.Start("H1"))
            {
                p1.Send("install");
                string c1 = p1.Send("create pad F0D8 coexist-h");
                if (!Ok(c1)) { Check("H: the parent program creates", false, c1); return; }
                string transport = PeerProcess.Field(c1, "transport")!;
                string sc = p1.Send("spawnchild");
                if (int.TryParse(PeerProcess.Field(sc, "pid"), out int childPid)) child = Process.GetProcessById(childPid);
                string? ownerId = Native.OwnerRecords().Where(r => r.Value == p1.Pid).Select(r => r.Key).FirstOrDefault();
                Check("H: the program has an owner record", ownerId != null);
                if (ownerId != null)
                    Check("H: its ownership claim is held while it runs", Native.PipeExists($"HIDMaestro.Owner.{ownerId}"));
                p1.Kill();
                Check("H: the child still runs", child != null && !child.HasExited);
                if (ownerId != null)
                    Check("H: the claim is gone with the parent though the child runs", !Native.PipeExists($"HIDMaestro.Owner.{ownerId}"));
                Check("H: the v1.12.0 owner pipe is gone too", !Native.PipeExists($"HIDMaestroOwner{transport}"));
            }
            using var p2 = PeerProcess.Start("H2");
            p2.Send("install");
            string c2 = p2.Send("create pad F0D8 coexist-h");
            Check("H: another program takes the same identity at once", Ok(c2), c2);
            if (Ok(c2))
            {
                p2.Send("stream 0 0.5 125 0");
                var path = WaitPaths(0xBEEF, 0xF0D8, 1, 10_000);
                using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
                Check("H: its input arrives", cap != null && Flows(cap));
            }
        }
        finally
        {
            try { child?.Kill(); } catch { }
        }
    }
}
