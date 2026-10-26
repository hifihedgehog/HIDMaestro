// The SDK's diagnostic logs with several processes writing them at once.
// Every program that embeds the SDK appends to %TEMP%\HIDMaestro\
// teardown_diag.log under HIDMAESTRO_DIAG=1 and to setup_timing.log under
// HIDMAESTRO_TIMING=1, the test app writes its own lines into
// teardown_diag.log, and every USB/IP device appends to the log
// HIDMAESTRO_DIAG_READS names. On the Celeron on 2026-10-26 a program's
// Dispose wrote its lines over those of the full cleanup that ran beside it:
// one of the cleanup's lines was left as a fragment and the others before it
// were gone. A FileStream opened with FileMode.Append writes at an offset it
// counts itself, from the file's length at its open, so a process writes
// over whatever the others appended since.
//
// Each run starts writer processes of this probe ("diag-append") with TEMP
// and TMP in a scratch folder, where the SDK's logs then land. A writer
// writes its first line, which opens the log, and waits until every writer
// has, so each holds the log open before any goes on. Then each writes the
// rest of its first half at once with the others, and after the second
// barrier its second half. Between the halves the parent can delete the log,
// as the battery deletes teardown_diag.log at its start. Every line carries
// its writer, its number and a payload made from both, so a line written over
// or cut is seen. Each line must arrive whole, exactly once, and in its
// writer's order.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using HIDMaestro.Internal;

internal static class DiagLogAppend
{
    const int Writers = 4;
    const int Lines = 400;
    const int Half = Lines / 2;

    static string Payload(int writer, int length) => new((char)('a' + writer), length);

    // The diag log's lines vary in length, so a cut lands mid-line.
    static string Text(int writer, int seq) => $"diaglog {writer} {seq:D5} {Payload(writer, 24 + seq % 40)}";

    static readonly Assembly s_sdk = typeof(DeviceOrchestrator).Assembly;

    // DeviceOrchestrator's timing writer: (ctrlIdx, profileId, step, elapsedMs).
    static MethodInfo? TimingWriter() =>
        typeof(DeviceOrchestrator).GetMethod("LogTiming", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(int), typeof(string), typeof(string), typeof(long) }, null);

    // The one-line appender a writer of a line now and then uses: the USB/IP
    // read log and the test app.
    static MethodInfo? OnceWriter() =>
        s_sdk.GetType("HIDMaestro.Internal.UserLog")?.GetMethod("Append", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(string), typeof(string) }, null);

    /// <summary>A writer process: diag-append kind writer log name.</summary>
    internal static int Child(string[] args)
    {
        string kind = args[1], log = args[3], name = args[4];
        int writer = int.Parse(args[2]);
        Action<int>? write = null;
        switch (kind)
        {
            case "diag":
                write = seq => DeviceOrchestrator.LogDiag(Text(writer, seq));
                break;
            case "timing":
                var timing = TimingWriter();
                if (timing != null)
                    write = seq => timing.Invoke(null, new object[] { writer, "diaglog", $"{seq:D5} {Payload(writer, 24)}", (long)seq });
                break;
            case "once":
                var once = OnceWriter();
                if (once != null) write = seq => once.Invoke(null, new object[] { log, Text(writer, seq) + "\n" });
                break;
        }
        if (write == null)
        {
            Console.Error.WriteLine($"the SDK has no {kind} writer");
            return 4;
        }
        using var ready = Semaphore.OpenExisting(name + "-ready");
        using var go = EventWaitHandle.OpenExisting(name + "-go");
        using var half = Semaphore.OpenExisting(name + "-half");
        using var go2 = EventWaitHandle.OpenExisting(name + "-go2");
        write(0);
        ready.Release();
        if (!go.WaitOne(60_000)) return 5;
        for (int seq = 1; seq < Half; seq++) write(seq);
        half.Release();
        if (!go2.WaitOne(60_000)) return 5;
        for (int seq = Half; seq < Lines; seq++) write(seq);
        return 0;
    }

    internal static void Run(Action<string, bool, string> check)
    {
        check("diag log: DeviceOrchestrator's timing writer exists", TimingWriter() != null, "");
        bool once = OnceWriter() != null;
        check("diag log: the SDK has a one-line appender for its occasional writers (UserLog.Append)", once, "");

        RunOne(check, "diag", delete: false);
        if (TimingWriter() != null) RunOne(check, "timing", delete: false);
        if (once) RunOne(check, "once", delete: false);
        RunOne(check, "diag", delete: true);
        if (once) Cost();
    }

    static readonly Regex s_diagLine = new(@"^\[\d\d:\d\d:\d\d\.\d{3}\] tid=\d+ +diaglog (\d) (\d{5}) ([a-z]+)$");
    static readonly Regex s_timingLine = new(@"^\[\d\d:\d\d:\d\d\.\d{3}\] ctrl=(\d) profile=diaglog +step=(\d{5}) ([a-z]+) +(\d+)ms$");
    static readonly Regex s_onceLine = new(@"^diaglog (\d) (\d{5}) ([a-z]+)$");

    static void RunOne(Action<string, bool, string> check, string kind, bool delete)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "hm-diaglog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        string log = kind switch
        {
            "diag" => Path.Combine(scratch, "HIDMaestro", "teardown_diag.log"),
            "timing" => Path.Combine(scratch, "HIDMaestro", "setup_timing.log"),
            _ => Path.Combine(scratch, "once.log"),
        };
        string what = kind switch
        {
            "diag" => "teardown_diag.log (LogDiag)",
            "timing" => "setup_timing.log (LogTiming)",
            _ => "a log of UserLog.Append",
        };
        string run = $"diag log, {Writers} processes appending {Lines} lines each to {what}" + (delete ? ", the log deleted between the halves" : "");
        string name = @"Local\hm-diaglog-" + Guid.NewGuid().ToString("N");
        var procs = new List<(Process P, System.Threading.Tasks.Task<string> Err)>();
        string barriers = "";
        try
        {
            using var ready = new Semaphore(0, Writers, name + "-ready");
            using var go = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-go");
            using var half = new Semaphore(0, Writers, name + "-half");
            using var go2 = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-go2");
            for (int w = 0; w < Writers; w++)
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string a in new[] { "diag-append", kind, w.ToString(), log, name }) psi.ArgumentList.Add(a);
                psi.Environment["TEMP"] = scratch;
                psi.Environment["TMP"] = scratch;
                psi.Environment["HIDMAESTRO_DIAG"] = kind == "diag" ? "1" : null;
                psi.Environment["HIDMAESTRO_TIMING"] = kind == "timing" ? "1" : null;
                var p = Process.Start(psi)!;
                _ = p.StandardOutput.ReadToEndAsync();
                procs.Add((p, p.StandardError.ReadToEndAsync()));
            }
            bool opened = Count(ready, procs);
            go.Set();
            bool halfway = opened && Count(half, procs);
            if (delete && halfway)
            {
                try { File.Delete(log); barriers = "deleted between the halves"; }
                catch (Exception e) { barriers = $"the delete failed: {e.Message}"; }
            }
            go2.Set();
            foreach (var (p, _) in procs)
                if (!p.WaitForExit(120_000)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } p.WaitForExit(); }
            if (!opened) barriers = "not every writer opened the log";
            else if (!halfway) barriers = "not every writer wrote its first half";
        }
        catch (Exception e) { barriers = $"{e.GetType().Name}: {e.Message}"; }

        string exits = string.Join(", ", procs.Select(x => x.P.HasExited ? x.P.ExitCode.ToString() : "running"));
        string errors = string.Join(" ", procs.Select(x => x.Err.IsCompleted ? x.Err.Result.Trim() : "").Where(e => e.Length > 0));
        bool ranToEnd = procs.Count == Writers && procs.All(x => x.P.HasExited && x.P.ExitCode == 0)
                        && (!delete || barriers == "deleted between the halves");
        check($"{run}: every writer ran to its end", ranToEnd, $"exits {exits}{(barriers.Length > 0 ? ", " + barriers : "")}{(errors.Length > 0 ? ", " + errors : "")}");
        foreach (var (p, _) in procs) p.Dispose();

        string text = "";
        try
        {
            using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f, Encoding.UTF8);
            text = r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        // Each writer's numbers in the file's order, and every line that is
        // not one of the lines a writer wrote.
        var seen = new List<int>[Writers];
        for (int w = 0; w < Writers; w++) seen[w] = new List<int>();
        var broken = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = (kind switch { "diag" => s_diagLine, "timing" => s_timingLine, _ => s_onceLine }).Match(line);
            bool whole = m.Success;
            int w = whole ? int.Parse(m.Groups[1].Value) : -1;
            int seq = whole ? int.Parse(m.Groups[2].Value) : -1;
            if (whole)
                whole = w < Writers && seq < Lines
                        && m.Groups[3].Value == Payload(w, kind == "timing" ? 24 : 24 + seq % 40)
                        && (kind != "timing" || long.Parse(m.Groups[4].Value) == seq);
            if (whole) seen[w].Add(seq);
            else broken.Add(line.Length > 80 ? line[..80] + "..." : line);
        }
        int first = delete ? Half : 0;
        var missing = new List<string>();
        var twice = new List<string>();
        var outOfOrder = new List<string>();
        for (int w = 0; w < Writers; w++)
        {
            var want = Enumerable.Range(first, Lines - first);
            int lost = want.Except(seen[w]).Count();
            if (lost > 0) missing.Add($"writer {w}: {lost}");
            // Lines from before the delete went with the deleted file.
            int early = seen[w].Count(s => s < first);
            if (early > 0) missing.Add($"writer {w}: {early} from before the delete");
            int dup = seen[w].Count - seen[w].Distinct().Count();
            if (dup > 0) twice.Add($"writer {w}: {dup}");
            for (int i = 1; i < seen[w].Count; i++)
                if (seen[w][i] <= seen[w][i - 1]) { outOfOrder.Add($"writer {w}: {seen[w][i - 1]} then {seen[w][i]}"); break; }
        }
        int total = seen.Sum(s => s.Count);
        check($"{run}: every line arrives whole", text.Length > 0 && broken.Count == 0,
              text.Length == 0 ? $"no log at {log}" : $"{broken.Count} broken line(s) of {total + broken.Count}{(broken.Count > 0 ? ", first: " + broken[0] : "")}");
        check($"{run}: every line arrives, once", missing.Count == 0 && twice.Count == 0,
              $"{total} of {Writers * (Lines - first)} whole lines" + (missing.Count > 0 ? "; missing " + string.Join(", ", missing) : "")
              + (twice.Count > 0 ? "; twice " + string.Join(", ", twice) : ""));
        check($"{run}: each writer's lines in its own order", outOfOrder.Count == 0, string.Join("; ", outOfOrder));
        try { Directory.Delete(scratch, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Takes one count per writer from the semaphore, or fails as
    /// soon as a writer has ended.</summary>
    static bool Count(Semaphore s, List<(Process P, System.Threading.Tasks.Task<string> Err)> procs)
    {
        var sw = Stopwatch.StartNew();
        for (int taken = 0; taken < procs.Count;)
        {
            if (s.WaitOne(50)) { taken++; continue; }
            // No writer ends before the last barrier.
            if (sw.ElapsedMilliseconds > 60_000 || procs.Any(x => x.P.HasExited)) return false;
        }
        return true;
    }

    /// <summary>What a line costs three ways, for the record: the SDK's
    /// held append handle, its open-append-close for one line, and a named
    /// mutex around a FileMode.Append open, write and close, the other way to
    /// keep two processes' lines apart.</summary>
    static void Cost()
    {
        var type = s_sdk.GetType("HIDMaestro.Internal.UserLog")!;
        string dir = Path.Combine(Path.GetTempPath(), "hm-diaglog-cost-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            const int held = 2000, each = 500;
            var log = Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { Path.Combine(dir, "held.log") }, null)!;
            var write = type.GetMethod("Write", BindingFlags.NonPublic | BindingFlags.Instance)!;
            write.Invoke(log, new object[] { "warm\n" });
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < held; i++) write.Invoke(log, new object[] { Text(0, i) + "\n" });
            double heldUs = sw.Elapsed.TotalMicroseconds / held;
            ((IDisposable)log).Dispose();

            var once = OnceWriter()!;
            string oncePath = Path.Combine(dir, "once.log");
            once.Invoke(null, new object[] { oncePath, "warm\n" });
            sw.Restart();
            for (int i = 0; i < each; i++) once.Invoke(null, new object[] { oncePath, Text(1, i) + "\n" });
            double onceUs = sw.Elapsed.TotalMicroseconds / each;

            string mutexPath = Path.Combine(dir, "mutex.log");
            using var mutex = new Mutex(false, @"Local\hm-diaglog-cost-" + Guid.NewGuid().ToString("N"));
            sw.Restart();
            for (int i = 0; i < each; i++)
            {
                mutex.WaitOne();
                try
                {
                    using var f = new FileStream(mutexPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] b = Encoding.UTF8.GetBytes(Text(2, i) + "\n");
                    f.Write(b, 0, b.Length);
                }
                finally { mutex.ReleaseMutex(); }
            }
            double mutexUs = sw.Elapsed.TotalMicroseconds / each;
            Console.WriteLine($"  info: a line costs {heldUs:F1} us through the held append handle, {onceUs:F1} us opened, appended and closed, " +
                              $"{mutexUs:F1} us under a named mutex with FileMode.Append opened, written and closed");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
