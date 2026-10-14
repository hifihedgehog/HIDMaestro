// Two or more programs that embed HIDMaestro on one machine.
//
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
