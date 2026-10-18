// Z. The transport's install and repair, the machine-wide locks and the
//    WinUSB binding.
//      1. A pnputil.exe that hangs, planted in an elevated program's current
//         directory. The SDK's pnputil calls run System32's and return
//         within their timeout. The probe's own bare launch from that
//         directory, which finds the planted one, is the positive control.
//         The runner they use, given a program that hangs and leaves a
//         process holding its output open past the kill, returns within
//         its timeout and its bounded read.
//      2. Another program's transport work holds its lock for 1.5 s while a
//         program runs the deploy gate every UMDF2 create passes
//         (DriverDeploy.EnsureForCreate). The gate's time, p50 and p95 over
//         30 runs, with and without the work.
//      3. The install's progress callback runs with no machine-wide lock
//         held: while it runs, another thread takes the deploy and the
//         transport locks, and a callback that blocks past the end of the
//         work leaves both free. A callback that throws leaves the outcome
//         as it was.
//      4. A host controller repair whose PnP work outlives its 1 s budget.
//         Another program's repair starts its PnP work only after the first
//         one's ends. Both run with the PnP restart and the transport's
//         availability replaced, so no host controller is touched.
//      5. The transport's legacy check while another program holds the
//         transport lock, on a machine whose host controller is the pinned
//         one: a composite create's install step looks without the lock and
//         returns at once. The program runs with HIDMAESTRO_TIMEOUT_SCALE=0.1,
//         so a build that waits for the lock gives up after 60 s.
//      6. A usbip-win2 installer another program left running, played by a
//         copy of this probe named USBip-zprobe.exe that runs 6 s. An install
//         waits for it to end, and gives up at its own timeout.
//      7. A sweep that finds a WinUSB entry of another program while another
//         thread's WinUSB work holds the binding's locks for 3 s. The sweep
//         takes that lock only when no one holds it, so it does not wait.
//         The ledger's lock stays free and the entry stays while the locks
//         are held. Once they are free, and with no devnode to wait for, the
//         follow-up the sweep started restores the entry, with no second
//         sweep (docs/design/coexistence.md, "Zero setup for Dolphin and
//         Slippi").
//      8. A WinUSB bind waits for the deploy lock to add its package, and two
//         programs bind at the same moment and both bind.
//      9. A GameCube adapter bound to WinUSB loses its connection, another
//         program's adapter takes its vhci port and the serial-named
//         instance it left, and the host controller imports it again on
//         another port. It is on WinUSB again and its ledger hold moved with
//         it. The other program's adapter, which never asked for WinUSB,
//         keeps WinUSB while it is present, since a restore installs nothing
//         on a present devnode, and the entry waits. Meanwhile a third
//         program's teardown sweep, 30 times, pays no wait for that entry and
//         starts no follow-up of its own. Then the first program exits,
//         which ends the follow-up it started, and a fourth program, which
//         starts none, sweeps 30 times with the WinUSB lock free and 30 times
//         with the probe holding it: no wait, and no pnputil for the present
//         devnode. Once the other adapter is disposed and gone, the instance
//         gets the reinstall flag and the entry goes.
//      a. A bind that claims a WinUSB entry between a sweep's queue and its
//         restore keeps its hold, for a sweep and for the full cleanup, and a
//         fresh claim under a holder name the full cleanup saw keeps it too.
//         The claim runs in the SDK's hook, on the sweep's thread after the
//         queue, so the restore decides before the sweep returns. Each part
//         has an instance id of its own. A fourth part reaches the
//         follow-up's decision: a thread of the peer holds the WinUSB lock
//         through the full cleanup's restore, so the entry goes to the
//         follow-up thread, then claims under the holder name the cleanup
//         saw and lets the lock go, and the follow-up keeps the entry.
//      b. Two programs ensure a test certificate under a name that does not
//         contain HIDMaestroTestCert while the probe holds the deploy lock:
//         neither adds it while the lock is held, and one certificate is in
//         each store after.
//      c. A bind waiting for the WinUSB lock while its adapter's connection
//         drops. The bind throws, changes nothing on the devnode that left
//         and holds no ledger entry for it. A bind that threw binds nothing
//         later, so the re-import 30 s later keeps input.inf for 15 s, and
//         the program's next bind binds it.
//      d. A program disposes its adapter while a bind waits for the WinUSB
//         lock. The dispose does not wait for the bind.
//      e. A bound adapter's WinUSB interface held open by the probe across
//         its program's dispose, and another program's adapter on the vhci
//         port it frees. The open handle does not keep the devnode: it
//         leaves at the dispose, and before the other adapter arrives it has
//         the reinstall flag, the entry is gone and its driver and setup
//         class are still WinUSB's. The other adapter, of the same serial,
//         arrives on that instance and installs by rank, and stays on
//         input.inf with no flag and no entry while the stale handle is
//         held and after it closes. The run logs the devnode's arrival
//         before and after the bind and the other adapter's PnP status.
//    "--z-parts=<characters>" after the section letters runs only those
//    parts. Parts 8, 9, c, d and e bind a real adapter and dispose it, which
//    on an SDK below the departing-devnode rule they were written for
//    (WinUsbBinding.DepartingDevnodeRule 6) can change the driver of a
//    devnode on its way out. Each asks the program for the rule first and
//    fails its check, running nothing, when the SDK is older.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

internal static partial class Program
{
    static void SectionZ()
    {
        if (ZPart('1')) ZPnputilPath();
        if (ZPart('2')) ZGateBesideTransportWork();
        if (ZPart('3')) ZProgressCallback();
        if (ZPart('4')) ZRepairOutlivesBudget();
        if (ZPart('5')) ZLegacyCheckWithoutLock();
        if (ZPart('6')) ZRunningInstaller();
        if (ZPart('7')) ZLedgerWinUsbSweep();
        if (ZPart('8')) { ZWinUsbPackageLock(); Clean(); }
        if (ZPart('9')) { ZRebindAfterReimport(); Clean(); }
        if (ZPart('a')) ZClaimDuringSweep();
        if (ZPart('b')) ZCertificateRace();
        if (ZPart('c')) { ZBindWhileDevnodeLeaves(); Clean(); }
        if (ZPart('d')) { ZDisposeDuringBind(); Clean(); }
        if (ZPart('e')) { ZHeldOpenAtTeardown(); Clean(); }
    }

    /// <summary>Whether "--z-parts=" names this part, or is absent.</summary>
    static bool ZPart(char part)
    {
        string? arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--z-parts=", StringComparison.Ordinal));
        return arg == null || arg.AsSpan("--z-parts=".Length).Contains(part);
    }

    /// <summary>A copy of this probe under another name: pnputil.exe hangs
    /// 40 s, longer than the SDK's 30 s pnputil timeouts, or exits at once
    /// with "--zquick", and USBip-*.exe runs 6 s. hmzorphan.exe starts a copy
    /// of itself that starts a third and exits, so the third holds the
    /// output it inherited with no parent alive to tie it to the first,
    /// and a kill of the first's process tree leaves it running. The first
    /// and the third hang 40 s. Exit code 7.</summary>
    static int Planted(string[] args)
    {
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
        if (name.StartsWith("USBip-", StringComparison.OrdinalIgnoreCase))
            Thread.Sleep(6_000);
        else if (name.Equals(OrphanName, StringComparison.OrdinalIgnoreCase))
        {
            if (args.Contains("--zgrand"))
                Thread.Sleep(40_000);
            else if (args.Contains("--zmiddle"))
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--zgrand") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            else
            {
                using (var middle = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--zmiddle") { UseShellExecute = false, CreateNoWindow = true })!)
                    middle.WaitForExit(10_000);
                Thread.Sleep(40_000);
            }
        }
        else if (!args.Contains("--zquick"))
            Thread.Sleep(40_000);
        return 7;
    }

    const string OrphanName = "hmzorphan";

    static bool IsPlanted()
    {
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
        return name.Equals("pnputil", StringComparison.OrdinalIgnoreCase)
            || name.Equals(OrphanName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("USBip-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The departing-devnode rule section Z was written for
    /// (WinUsbBinding.DepartingDevnodeRule): a restore never installs a
    /// driver on a present devnode, no cleanup removes a devnode, and a
    /// process exit stops its re-binds first.</summary>
    internal const int ZDepartingRule = 6;

    /// <summary>Whether the program's SDK has the departing-devnode rule
    /// section Z was written for. Without it a bind, a restore or a cleanup
    /// can change the driver of a devnode on its way out, so a part that
    /// binds a real adapter fails here and runs nothing.</summary>
    static bool ZGuarded(PeerProcess p, string part)
    {
        string r = p.Send("zguard");
        bool ok = PeerProcess.Field(r, "guard") == "True";
        if (!ok) Check($"{part}: the program's SDK has the departing-devnode guard (pre-fix SDK, not run)", false, r);
        return ok;
    }

    /// <summary>Copy this probe into a new directory under
    /// <paramref name="exeName"/>. The apphost finds CoexistCheck.dll beside
    /// it, and nothing the planted program runs needs another assembly.</summary>
    static string PlantCopy(string dir, string exeName)
    {
        string self = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        Directory.CreateDirectory(dir);
        string planted = Path.Combine(dir, exeName);
        File.Copy(Environment.ProcessPath!, planted);
        foreach (string f in new[] { ".dll", ".runtimeconfig.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, self + f), Path.Combine(dir, self + f));
        return planted;
    }

    static void RemovePlanted(string dir, string part)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Console.WriteLine($"    {part}: {dir} not removed: {ex.Message}"); }
    }

    static long LongField(string reply, string name) => long.TryParse(PeerProcess.Field(reply, name), out long v) ? v : -1;

    // ── Z1 ─────────────────────────────────────────────────────────────

    static void ZPnputilPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hm-z1-" + Environment.ProcessId);
        try
        {
            string planted = PlantCopy(dir, "pnputil.exe");
            string cwd = Environment.CurrentDirectory;
            int control;
            try
            {
                Environment.CurrentDirectory = dir;
                using var p = Process.Start(new ProcessStartInfo("pnputil.exe", "--zquick") { UseShellExecute = false, CreateNoWindow = true })!;
                control = p.WaitForExit(20_000) ? p.ExitCode : -1;
            }
            finally { Environment.CurrentDirectory = cwd; }
            Check("Z1: a bare pnputil.exe launched from that directory runs the planted one", control == 7, $"exit {control}");

            using var peer = PeerProcess.Start("Z1");
            string c = peer.Send($"zcwd {dir}");
            if (!Ok(c)) { Check("Z1: the program's current directory is the planted one's", false, c); return; }
            string r1 = peer.Send("zproduct", 120_000);
            long ms1 = LongField(r1, "ms");
            Check("Z1: IsProductInstalled runs System32's pnputil and returns within its 30 s timeout",
                  PeerProcess.Field(r1, "installed") == "True" && ms1 >= 0 && ms1 < 30_000, r1);
            string r2 = peer.Send("zpnputil /enum-drivers", 120_000);
            long ms2 = LongField(r2, "ms");
            Check("Z1: the legacy move's pnputil runs System32's and returns within its 60 s timeout",
                  PeerProcess.Field(r2, "rc") == "0" && ms2 >= 0 && ms2 < 60_000, r2);
            peer.Send($"zcwd {cwd}");

            // The runner itself, given a program that hangs and leaves a
            // process holding its output: a 3 s timeout, the kill, up to 5 s
            // for the exit and the read's own 5 s bound. A read with no bound
            // waits for that process, 40 s.
            string orphan = Path.Combine(dir, OrphanName + ".exe");
            File.Copy(planted, orphan);
            string r3 = peer.Send($"zruntool 3000 {orphan}", 120_000);
            long ms3 = LongField(r3, "ms");
            var left = Process.GetProcessesByName(OrphanName);
            bool held = left.Length > 0;
            foreach (var x in left) x.Dispose();
            // The read waits for that process until its own bound, so the
            // runner takes at least the timeout and that bound.
            Check("Z1: a process the planted program left holds its output past the kill (positive control)",
                  held && ms3 >= 7000, r3);
            Check("Z1: the pnputil runner ends a program that hangs at its timeout and returns within its bounded read",
                  Ok(r3) && ms3 >= 2500 && ms3 < 16_000, r3);
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName(OrphanName))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                p.Dispose();
            }
            RemovePlanted(dir, "Z1");
        }
    }

    // ── Z2 ─────────────────────────────────────────────────────────────

    static void ZGateBesideTransportWork()
    {
        const int runs = 30, holdMs = 1500;
        using var work = PeerProcess.Start("Z2-transport");
        using var create = PeerProcess.Start("Z2-create");

        static double Ms(string reply) => double.TryParse(PeerProcess.Field(reply, "ms"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

        var idle = new List<double>();
        for (int i = 0; i < runs; i++) idle.Add(Ms(create.Send("zgate")));

        var held = new List<double>();
        for (int i = 0; i < runs; i++)
        {
            work.Post($"zrun {holdMs} none");
            string started = work.Receive("zrun, its work started", 30_000);
            if (started != "OK zrun started") { Check("Z2: the transport work starts", false, started); return; }
            held.Add(Ms(create.Send("zgate")));
            work.Receive("zrun", 30_000);
        }

        static string Stats(List<double> v)
        {
            var s = v.OrderBy(x => x).ToArray();
            return FormattableString.Invariant($"p50 {s[s.Length / 2]:F1} ms, p95 {s[(int)Math.Ceiling(s.Length * 0.95) - 1]:F1} ms, max {s[^1]:F1} ms");
        }
        Console.WriteLine($"    Z2: the deploy gate alone, {runs} runs: {Stats(idle)}");
        Console.WriteLine($"    Z2: the deploy gate while transport work holds its lock {holdMs} ms, {runs} runs: {Stats(held)}");
        double p95 = held.OrderBy(x => x).ToArray()[(int)Math.Ceiling(runs * 0.95) - 1];
        Check("Z2: the deploy gate does not wait for another program's transport work",
              idle.All(double.IsFinite) && held.All(double.IsFinite) && p95 < 500, Stats(held));
    }

    // ── Z3 ─────────────────────────────────────────────────────────────

    static void ZProgressCallback()
    {
        using var p = PeerProcess.Start("Z3");
        string r1 = p.Send("zrun 0 check");
        Check("Z3: the progress callback runs once, and meanwhile another thread takes the deploy and the transport locks",
              PeerProcess.Field(r1, "result") == "True" && PeerProcess.Field(r1, "calls") == "1" && PeerProcess.Field(r1, "free") == "True", r1);
        string r2 = p.Send("zrun 0 throw");
        Check("Z3: a callback that throws leaves the work's outcome as it was",
              PeerProcess.Field(r2, "result") == "True" && PeerProcess.Field(r2, "calls") == "1", r2);
        // The work reports, then holds its lock 3 s more and ends. The
        // callback blocks 7 s, and 4.5 s in, after the work has ended,
        // another thread tries both locks.
        string r3 = p.Send("zrun 0 block", 60_000);
        Check("Z3: a callback that blocks past the end of the work leaves the deploy and the transport locks free",
              PeerProcess.Field(r3, "result") == "True" && PeerProcess.Field(r3, "free") == "True", r3);
    }

    // ── Z4 ─────────────────────────────────────────────────────────────

    static void ZRepairOutlivesBudget()
    {
        using var first = PeerProcess.Start("Z4-first");
        using var second = PeerProcess.Start("Z4-second");
        string r1 = first.Send("zrepair 6000 1000");
        long ms1 = LongField(r1, "ms");
        Check("Z4: the first repair gives up after its 1 s budget while its PnP work runs on",
              PeerProcess.Field(r1, "result") == "False" && ms1 >= 0 && ms1 < 3000, r1);
        string r2 = second.Send("zrepair 300 1500", 60_000);
        Check("Z4: the second program's repair returns", PeerProcess.Field(r2, "result") == "False", r2);
        string l1 = first.Send("zrepairlog", 30_000), l2 = second.Send("zrepairlog", 30_000);
        bool parsed = long.TryParse(PeerProcess.Field(l1, "end"), out long end1) & long.TryParse(PeerProcess.Field(l2, "start"), out long start2);
        Check("Z4: the second repair's PnP work starts only after the first one's ends",
              parsed && start2 >= end1, parsed ? $"second started {(start2 - end1) / 10_000.0:F0} ms after the first ended" : $"{l1} / {l2}");
    }

    // ── Z5 ─────────────────────────────────────────────────────────────

    static void ZLegacyCheckWithoutLock()
    {
        if (!Vhci.Available()) { Check("Z5: the usbip-win2 host controller answers", false); return; }
        // A build that waits for the lock gives up after 60 s, not 10 min.
        PeerProcess started;
        Environment.SetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE", "0.1");
        try { started = PeerProcess.Start("Z5"); }
        finally { Environment.SetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE", null); }
        using var p = started;
        // The lock the legacy move takes: the deploy lock through a7e5015,
        // the transport lock after.
        var held = new List<Mutex>();
        try
        {
            foreach (string name in new[] { "Deploy", "Transport" })
            {
                var m = Native.AdminMutex(name);
                try { m.WaitOne(); } catch (AbandonedMutexException) { }
                held.Add(m);
            }
            string r = p.Send("zlegacy", 120_000);
            long ms = LongField(r, "ms");
            Check("Z5: with the transport lock held, a composite create's install step returns at once on the pinned host controller",
                  PeerProcess.Field(r, "result") == "True" && ms >= 0 && ms < 5000, r);
            Check("Z5: and the legacy check is done", PeerProcess.Field(r, "checked") == "True", r);
        }
        finally
        {
            foreach (var m in held) { m.ReleaseMutex(); m.Dispose(); }
        }
    }

    // ── Z6 ─────────────────────────────────────────────────────────────

    static void ZRunningInstaller()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hm-z6-" + Environment.ProcessId);
        try
        {
            string planted = PlantCopy(dir, "USBip-zprobe.exe");
            using var peer = PeerProcess.Start("Z6");
            using (var first = Process.Start(new ProcessStartInfo(planted) { UseShellExecute = false, CreateNoWindow = true })!)
            {
                string r1 = peer.Send("zinstallers 1000");
                Check("Z6: an install gives up at its timeout while another installer runs",
                      PeerProcess.Field(r1, "result") == "False" && LongField(r1, "ms") >= 900, r1);
                first.WaitForExit(20_000);
            }
            using (var second = Process.Start(new ProcessStartInfo(planted) { UseShellExecute = false, CreateNoWindow = true })!)
            {
                string r2 = peer.Send("zinstallers 30000", 60_000);
                // Read as the reply comes: an install that went on without
                // waiting answers while the 6 s installer still runs. The
                // lower bound is the installer's: one that ended at once, on a
                // loader failure, leaves nothing to wait for, and the 3 s it
                // allows against the installer's 6 s is no race.
                bool endedFirst = second.HasExited;
                long ms = LongField(r2, "ms");
                Check("Z6: an install waits for the running installer to end, and goes on",
                      PeerProcess.Field(r2, "result") == "True" && endedFirst && ms >= 3000 && ms < 20_000,
                      $"{r2}, the installer {(endedFirst ? "had ended" : "still ran")} when the reply came");
                second.WaitForExit(20_000);
            }
            string r3 = peer.Send("zinstallers 1000");
            Check("Z6: with no installer running an install goes on at once",
                  PeerProcess.Field(r3, "result") == "True" && LongField(r3, "ms") < 900, r3);
        }
        finally { RemovePlanted(dir, "Z6"); }
    }

    // ── Z7 ─────────────────────────────────────────────────────────────

    static void ZLedgerWinUsbSweep()
    {
        using var p = PeerProcess.Start("Z7");
        string r = p.Send("zwinusbsweep 3000", 60_000);
        long ledger = LongField(r, "ledgerms");
        long sweep = LongField(r, "sweepms");
        Check("Z7: with the binding's locks held, the sweep does not wait for them",
              PeerProcess.Field(r, "order") == "before" && sweep >= 0 && sweep < 1500, r);
        Check("Z7: the ledger's lock stays free while they are held", ledger >= 0 && ledger < 1000, r);
        Check("Z7: the entry stays while they are held",
              PeerProcess.Field(r, "during") == "True" && PeerProcess.Field(r, "aftersweep") == "True", r);
        Check("Z7: once they are free, the follow-up the sweep started restores the entry",
              PeerProcess.Field(r, "gone") == "True" && PeerProcess.Field(r, "restored") == "after"
              && PeerProcess.Field(r, "via") == "followup", r);
    }

    // ── Z8 ─────────────────────────────────────────────────────────────

    static void ZWinUsbPackageLock()
    {
        using (var p = PeerProcess.Start("Z8-held"))
        {
            if (!ZGuarded(p, "Z8")) return;
            string c = p.Send("create gc -");
            if (!Ok(c)) { Check("Z8: the program creates a GameCube adapter", false, c); return; }
            var deploy = Native.AdminMutex("Deploy");
            string early;
            try
            {
                try { deploy.WaitOne(); } catch (AbandonedMutexException) { }
                p.Post("bindwinusb");
                // Longer than a first add takes: inf2cat, signtool, pnputil.
                early = p.Receive("bindwinusb, while the probe holds the deploy lock", 30_000);
                Check("Z8: BindWinUsb does not add its package while another program holds the deploy lock",
                      early.StartsWith("ERR timeout", StringComparison.Ordinal), early);
            }
            finally { deploy.ReleaseMutex(); deploy.Dispose(); }
            string bound = early.StartsWith("ERR timeout", StringComparison.Ordinal) ? p.Receive("bindwinusb, once it is free", 120_000) : early;
            Check("Z8: it binds once the lock is free", bound == "OK bindwinusb Bound", bound);
        }
        Clean();

        using var a = PeerProcess.Start("Z8-a");
        using var b = PeerProcess.Start("Z8-b");
        string ca = a.Send("create gc -"), cb = b.Send("create gc -");
        if (!Ok(ca) || !Ok(cb)) { Check("Z8: two programs create a GameCube adapter each", false, $"{ca} / {cb}"); return; }
        a.Post("bindwinusb");
        b.Post("bindwinusb");
        string ra = a.Receive("bindwinusb", 180_000), rb = b.Receive("bindwinusb", 180_000);
        Check("Z8: two programs of one build bind at the same moment, and both bind", ra == "OK bindwinusb Bound" && rb == "OK bindwinusb Bound", $"{ra} / {rb}");
    }

    // ── Z9 ─────────────────────────────────────────────────────────────

    /// <summary>The holders of the ledger's WinUSB entry for one devnode.</summary>
    static List<string> WinUsbHolders(string instanceId)
    {
        var list = new List<string>();
        using var kind = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroRegistryLedger\WinUsb");
        if (kind == null) return list;
        foreach (var n in kind.GetSubKeyNames())
        {
            using var e = kind.OpenSubKey(n);
            if (!string.Equals(e?.GetValue("InstanceId") as string, instanceId, StringComparison.OrdinalIgnoreCase)) continue;
            using var h = e!.OpenSubKey("Holders");
            if (h != null) list.AddRange(h.GetValueNames());
        }
        return list;
    }

    /// <summary>The service and setup class an instance's Enum key names,
    /// present or not.</summary>
    static (string? Service, string? Class) EnumDriver(string instanceId)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        return (k?.GetValue("Service") as string, k?.GetValue("ClassGUID") as string);
    }

    static (int Port, string Bus, int Vhci) GcImport(PeerProcess p, string created, HashSet<(int Port, string BusId)> touched)
    {
        string bus = "1-" + (int.Parse(PeerProcess.Field(created, "transport")!) + 1);
        int port = RecordUsbipPort(p.Pid);
        touched.Add((port, bus));
        int vhci = 0;
        WaitFor(() => (vhci = Vhci.Imported().FirstOrDefault(r => r.Port == port && r.BusId == bus).VhciPort) > 0, 10_000);
        return (port, bus, vhci);
    }

    static void ZRebindAfterReimport()
    {
        var touched = new HashSet<(int Port, string BusId)>();
        try
        {
            using var a = PeerProcess.Start("Z9-bound");
            if (!ZGuarded(a, "Z9")) return;
            string ca = a.Send("create gc -");
            if (!Ok(ca)) { Check("Z9: the first program creates a GameCube adapter", false, ca); return; }
            var (portA, busA, x) = GcImport(a, ca, touched);
            string? devA = null;
            WaitFor(() => (devA = Usb.OnVhciPort(0x057E, 0x0337, x)) != null, 15_000);
            if (devA == null) { Check("Z9: its adapter enumerates", false, $"vhci {x}"); return; }
            string bound = a.Send("bindwinusb", 120_000);
            Check("Z9: its BindWinUsb binds", bound == "OK bindwinusb Bound", bound);
            if (bound != "OK bindwinusb Bound") return;

            int reset = Tcp.ResetServerEnd(portA, a.Pid);
            Check("Z9: the probe resets the adapter's connection", reset == 1, $"{reset} connection(s) reset");
            Check($"Z9: the host controller drops it from vhci port {x}",
                  WaitFor(() => !Vhci.Imported().Any(r => r.Port == portA && r.BusId == busA), 10_000));
            var dropped = Stopwatch.StartNew();
            WaitFor(() => Native.CM_Locate_DevNodeW(out _, devA, 0) != 0, 10_000);

            using var b = PeerProcess.Start("Z9-other");
            string cb = b.Send("create gc -");
            if (!Ok(cb)) { Check("Z9: another program creates a GameCube adapter", false, cb); return; }
            var (_, _, xb) = GcImport(b, cb, touched);
            string? devB = null;
            WaitFor(() => (devB = Usb.OnVhciPort(0x057E, 0x0337, xb)) != null, 15_000);
            Check($"Z9: the other program's adapter takes vhci port {x}", xb == x, $"vhci {xb}");
            Check("Z9: and the instance the binding left", string.Equals(devB, devA, StringComparison.OrdinalIgnoreCase),
                  $"left {devA}, other {devB ?? "none"} on {Usb.Service(devB ?? "") ?? "no driver"}");

            // The host controller's attempt goes out 30 s after the drop
            // (persistent.cpp start_attach_attempts, DELAY).
            int y = 0;
            WaitFor(() => (y = Vhci.Imported().FirstOrDefault(r => r.Port == portA && r.BusId == busA).VhciPort) > 0,
                    (int)Math.Max(0, 45_000 - dropped.ElapsedMilliseconds));
            Check($"Z9: the host controller imports {busA} again, on another vhci port", y > 0 && y != x, $"vhci port {y}");
            if (y <= 0) return;
            string? devA2 = null;
            WaitFor(() => (devA2 = Usb.OnVhciPort(0x057E, 0x0337, y)) != null, 15_000);
            Console.WriteLine($"    Z9: it came back as {devA2 ?? "nothing"}");
            Check("Z9: the adapter that came back is on WinUSB again",
                  devA2 != null && WaitFor(() => string.Equals(Usb.Service(devA2), "WinUSB", StringComparison.OrdinalIgnoreCase), 30_000),
                  devA2 == null ? "no devnode" : Usb.Service(devA2) ?? "no driver");
            string self = $"{a.Pid}-";
            Check("Z9: the first program's ledger hold moved to the devnode that came back",
                  devA2 != null && WaitFor(() => WinUsbHolders(devA2).Any(h => h.StartsWith(self, StringComparison.Ordinal))
                                              && !WinUsbHolders(devA!).Any(h => h.StartsWith(self, StringComparison.Ordinal)), 30_000),
                  $"{devA2}: {string.Join(",", devA2 == null ? new List<string>() : WinUsbHolders(devA2))}; {devA}: {string.Join(",", WinUsbHolders(devA!))}");
            if (devB != null)
            {
                // A restore installs nothing on a present devnode: the other
                // program's adapter keeps the instance's WinUSB while it is
                // there, and the entry waits for it to go.
                bool kept = true;
                for (int i = 0; i < 25 && kept; i++)
                {
                    kept = string.Equals(Usb.Service(devB), "WinUSB", StringComparison.OrdinalIgnoreCase)
                           && ((ConfigFlags(devB) ?? 0) & 0x20) == 0 && WinUsbEntryExists(devB);
                    Thread.Sleep(200);
                }
                Check("Z9: the other program's adapter keeps WinUSB while present, with no reinstall flag, and the entry stays",
                      kept, $"{Usb.Service(devB) ?? "no driver"}, ConfigFlags 0x{ConfigFlags(devB) ?? 0:X}, entry {WinUsbEntryExists(devB)}");

                // What the lingering entry costs any other controller's
                // teardown: a third program releases a controller index it
                // holds nothing for, 30 times, the sweep every teardown runs.
                using (var c = PeerProcess.Start("Z9-teardown"))
                {
                    string cost = c.Send("zreleasecost 30", 300_000);
                    Console.WriteLine($"    Z9: a teardown's sweep beside the lingering entry, 30 runs: {cost}");
                    double p95 = double.TryParse(PeerProcess.Field(cost, "p95"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                    Check("Z9: the lingering entry costs another controller's teardown no wait (p95 under 100 ms)",
                          Ok(cost) && p95 < 100, cost);
                    string t = c.Send("threads HMWinUsbFollowUp");
                    Check("Z9: and starts no follow-up of its own there, since one already watches the entry",
                          PeerProcess.Field(t, "n") == "0", t);
                }

                // With no follow-up anywhere, each teardown's sweep takes the
                // entry's own path: the WinUSB lock only when it is free, and
                // the present devnode's driver read without pnputil. The
                // first program's exit ends the follow-up it started.
                string ea = a.Send("exit");
                Check("Z9: the first program exits, disposing its adapter", Ok(ea) && WaitFor(() => a.Exited, 20_000), ea);
                using (var c = PeerProcess.Start("Z9-unfollowed"))
                {
                    c.Send("zreleasecost 1 nofollow", 300_000);
                    string free = c.Send("zreleasecost 30 nofollow", 300_000);
                    Console.WriteLine($"    Z9: a teardown's sweep beside the entry with no follow-up, the WinUSB lock free, 30 runs: {free}");
                    var devnodes = Native.AdminMutex("WinUsb");
                    string held;
                    try
                    {
                        try { devnodes.WaitOne(); } catch (AbandonedMutexException) { }
                        held = c.Send("zreleasecost 30 nofollow", 300_000);
                    }
                    finally { devnodes.ReleaseMutex(); devnodes.Dispose(); }
                    Console.WriteLine($"    Z9: the same with the probe holding the WinUSB lock, 30 runs: {held}");
                    static double P95(string r) => double.TryParse(PeerProcess.Field(r, "p95"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                    Check("Z9: with no follow-up and the WinUSB lock free, the entry costs a teardown no wait and no pnputil (p95 under 100 ms)",
                          Ok(free) && P95(free) < 100, free);
                    Check("Z9: with no follow-up and the WinUSB lock held elsewhere, the teardown does not wait for it (p95 under 100 ms)",
                          Ok(held) && P95(held) < 100, held);
                    Check("Z9: and the entry stays while the devnode is present",
                          Native.IsPresent(devB) && WinUsbEntryExists(devB) && ((ConfigFlags(devB) ?? 0) & 0x20) == 0,
                          $"present {Native.IsPresent(devB)}, entry {WinUsbEntryExists(devB)}, ConfigFlags 0x{ConfigFlags(devB) ?? 0:X}");
                }
                string db = b.Send("dispose");
                Check("Z9: once that adapter is disposed and gone, the instance gets the reinstall flag and the entry goes",
                      Ok(db) && WaitFor(() => !Native.IsPresent(devB) && ((ConfigFlags(devB) ?? 0) & 0x20) != 0 && !WinUsbEntryExists(devB), 60_000),
                      $"{db}; present {Native.IsPresent(devB)}, ConfigFlags 0x{ConfigFlags(devB) ?? 0:X}, entry {WinUsbEntryExists(devB)}");
            }
            else
            {
                string d = a.Send("dispose");
                Check("Z9: the first program disposes its adapter", Ok(d), d);
            }
        }
        finally
        {
            foreach (var (port, busId) in touched) Vhci.Stop("127.0.0.1", port, busId);
        }
    }

    // ── Za ─────────────────────────────────────────────────────────────

    static void ZClaimDuringSweep()
    {
        using var p = PeerProcess.Start("Za");
        // Each part claims inside the sweep, on its thread, so the restore
        // decides before the sweep returns (handshake=hook).
        string r1 = p.Send("zclaimsweep sweep", 60_000);
        Check("Za: a claim between a sweep's queue and its restore keeps the entry and its holder",
              PeerProcess.Field(r1, "kept") == "True" && PeerProcess.Field(r1, "holder") == "True"
              && PeerProcess.Field(r1, "handshake") == "hook", r1);
        string r2 = p.Send("zclaimsweep all", 60_000);
        Check("Za: a claim between the full cleanup's queue and its restore keeps the entry and its holder",
              PeerProcess.Field(r2, "kept") == "True" && PeerProcess.Field(r2, "holder") == "True"
              && PeerProcess.Field(r2, "handshake") == "hook", r2);
        string r3 = p.Send("zclaimsweep same", 60_000);
        Check("Za: a fresh claim under a holder name the full cleanup saw keeps the entry and its holder",
              PeerProcess.Field(r3, "kept") == "True" && PeerProcess.Field(r3, "holder") == "True"
              && PeerProcess.Field(r3, "handshake") == "hook", r3);

        // The follow-up's decision. The full cleanup cannot take the WinUSB
        // lock, which a thread of the peer holds, so it leaves the entry to
        // its follow-up thread and returns.
        string q = p.Send("zfollow queue", 60_000);
        Check("Za: with the WinUSB lock held, the full cleanup returns and leaves the entry to its follow-up",
              PeerProcess.Field(q, "kept") == "True", q);
        string t1 = p.Send("threads HMWinUsbFollowUp");
        Check("Za: the follow-up thread runs", int.TryParse(PeerProcess.Field(t1, "n"), out int n1) && n1 >= 1, t1);
        string c = p.Send("zfollow claim", 30_000);
        Check("Za: the lock holder claims under the holder name the cleanup saw and lets the lock go", Ok(c), c);
        // The follow-up retries every 250 ms, so in 3 s it has decided more
        // than once.
        Thread.Sleep(3000);
        string k = p.Send("zfollow check");
        string t2 = p.Send("threads HMWinUsbFollowUp");
        Check("Za: the follow-up keeps the entry and the fresh claim",
              PeerProcess.Field(k, "kept") == "True" && PeerProcess.Field(k, "fresh") == "True"
              && int.TryParse(PeerProcess.Field(t2, "n"), out int n2) && n2 >= 1, $"{k}, {t2}");
        string e = p.Send("zfollow end", 30_000);
        Check("Za: with the entry gone, the follow-up ends", PeerProcess.Field(e, "threads") == "0", e);
    }

    // ── Zb ─────────────────────────────────────────────────────────────

    static int CertCount(StoreName store, string name)
    {
        using var s = new X509Store(store, StoreLocation.LocalMachine);
        s.Open(OpenFlags.ReadOnly);
        var found = s.Certificates.Find(X509FindType.FindBySubjectName, name, false);
        int n = found.Count;
        foreach (var c in found) c.Dispose();
        return n;
    }

    /// <summary>Remove every certificate of this name from the three stores,
    /// and the private keys the creator persisted for it.</summary>
    static void RemoveCertificates(string name)
    {
        foreach (var store in new[] { StoreName.My, StoreName.Root, StoreName.TrustedPublisher })
        {
            using var s = new X509Store(store, StoreLocation.LocalMachine);
            s.Open(OpenFlags.ReadWrite);
            foreach (var c in s.Certificates.Find(X509FindType.FindBySubjectName, name, false))
            {
                if (store == StoreName.My)
                {
                    try
                    {
                        using var key = c.GetRSAPrivateKey();
                        if (key is RSACng cng) cng.Key.Delete();
                        else if (key is RSACryptoServiceProvider csp) { csp.PersistKeyInCsp = false; csp.Clear(); }
                    }
                    catch (Exception ex) { Console.WriteLine($"    Zb: the key of {c.Thumbprint} not deleted: {ex.Message}"); }
                }
                s.Remove(c);
                c.Dispose();
            }
        }
    }

    static void ZCertificateRace()
    {
        string name = "HMZCertSeam-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            using var a = PeerProcess.Start("Zb-a");
            using var b = PeerProcess.Start("Zb-b");
            var deploy = Native.AdminMutex("Deploy");
            string ea, eb;
            try
            {
                try { deploy.WaitOne(); } catch (AbandonedMutexException) { }
                a.Post($"zcert {name}");
                b.Post($"zcert {name}");
                ea = a.Receive("zcert, while the probe holds the deploy lock", 4000);
                eb = b.Receive("zcert, while the probe holds the deploy lock", 4000);
                Check("Zb: neither program adds the certificate while another holds the deploy lock",
                      ea.StartsWith("ERR timeout", StringComparison.Ordinal) && eb.StartsWith("ERR timeout", StringComparison.Ordinal)
                      && CertCount(StoreName.My, name) == 0, $"{ea} / {eb}, {CertCount(StoreName.My, name)} in My");
            }
            finally { deploy.ReleaseMutex(); deploy.Dispose(); }
            string ra = ea.StartsWith("ERR timeout", StringComparison.Ordinal) ? a.Receive("zcert", 60_000) : ea;
            string rb = eb.StartsWith("ERR timeout", StringComparison.Ordinal) ? b.Receive("zcert", 60_000) : eb;
            int my = CertCount(StoreName.My, name), root = CertCount(StoreName.Root, name), pub = CertCount(StoreName.TrustedPublisher, name);
            Check("Zb: both programs return, and one certificate is in each of My, Root and TrustedPublisher",
                  Ok(ra) && Ok(rb) && my == 1 && root == 1 && pub == 1, $"{ra} / {rb}, My {my}, Root {root}, TrustedPublisher {pub}");
        }
        finally { RemoveCertificates(name); }
        Check("Zb: nothing of the test certificate is left",
              CertCount(StoreName.My, name) + CertCount(StoreName.Root, name) + CertCount(StoreName.TrustedPublisher, name) == 0);
    }

    // ── Zc ─────────────────────────────────────────────────────────────

    static void ZBindWhileDevnodeLeaves()
    {
        var touched = new HashSet<(int Port, string BusId)>();
        try
        {
            using var a = PeerProcess.Start("Zc");
            if (!ZGuarded(a, "Zc")) return;
            string ca = a.Send("create gc -");
            if (!Ok(ca)) { Check("Zc: the program creates a GameCube adapter", false, ca); return; }
            var (portA, busA, x) = GcImport(a, ca, touched);
            string? devA = null;
            WaitFor(() => (devA = Usb.OnVhciPort(0x057E, 0x0337, x)) != null, 15_000);
            if (devA == null) { Check("Zc: its adapter enumerates", false, $"vhci {x}"); return; }
            var before = EnumDriver(devA);

            var devnodes = Native.AdminMutex("WinUsb");
            Stopwatch dropped;
            try
            {
                try { devnodes.WaitOne(); } catch (AbandonedMutexException) { }
                a.Post("bindwinusb");
                // The bind finds the devnode and waits for the WinUSB lock.
                Thread.Sleep(3000);
                int reset = Tcp.ResetServerEnd(portA, a.Pid);
                Check("Zc: the probe resets the adapter's connection while the bind waits", reset == 1, $"{reset} connection(s) reset");
                Check($"Zc: the host controller drops it from vhci port {x}",
                      WaitFor(() => !Vhci.Imported().Any(r => r.Port == portA && r.BusId == busA), 10_000));
                dropped = Stopwatch.StartNew();
                WaitFor(() => Native.CM_Locate_DevNodeW(out _, devA, 0) != 0, 5000);
            }
            finally { devnodes.ReleaseMutex(); devnodes.Dispose(); }
            string r = a.Receive("bindwinusb", 120_000);
            var after = EnumDriver(devA);
            Console.WriteLine($"    Zc: the bind: {r}; {devA} was {before.Service} {before.Class}, is {after.Service} {after.Class}");
            Check("Zc: the bind changes neither the driver nor the setup class of the devnode that left",
                  string.Equals(after.Service, before.Service, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(after.Class, before.Class, StringComparison.OrdinalIgnoreCase),
                  $"{before.Service} {before.Class} -> {after.Service} {after.Class}");
            Check("Zc: the bind does not report a binding it did not make", r != "OK bindwinusb Bound", r);
            string self = $"{a.Pid}-";
            Check("Zc: the program holds no ledger entry for the devnode that left",
                  !WinUsbHolders(devA).Any(h => h.StartsWith(self, StringComparison.Ordinal)),
                  string.Join(",", WinUsbHolders(devA)));

            // The host controller's attempt goes out 30 s after the drop.
            int y = 0;
            WaitFor(() => (y = Vhci.Imported().FirstOrDefault(i => i.Port == portA && i.BusId == busA).VhciPort) > 0,
                    (int)Math.Max(0, 45_000 - dropped.ElapsedMilliseconds));
            string? dev2 = null;
            if (y > 0) WaitFor(() => (dev2 = Usb.OnVhciPort(0x057E, 0x0337, y)) != null, 15_000);
            // The bind threw, so the program was told nothing is bound, and a
            // BindWinUsb that threw binds nothing at a later re-import
            // (WinUsbBinding.Bind, BindPhase.Refused). The import that comes
            // back keeps the driver Windows gives it: input.inf, which the
            // adapter fails to start on (problem 10). A re-bind on the pool
            // thread lands within seconds of the re-import, so 15 s of
            // samples on the settled devnode would see one.
            bool settled = dev2 != null && WaitFor(() => string.Equals(Usb.Service(dev2), "HidUsb", StringComparison.OrdinalIgnoreCase), 30_000);
            bool stayed = settled;
            var keep = Stopwatch.StartNew();
            while (stayed && keep.ElapsedMilliseconds < 15_000)
            {
                stayed = !string.Equals(Usb.Service(dev2!), "WinUSB", StringComparison.OrdinalIgnoreCase)
                         && !WinUsbHolders(dev2!).Any(h => h.StartsWith(self, StringComparison.Ordinal));
                Thread.Sleep(100);
            }
            Check("Zc: the import that comes back keeps input.inf for 15 s, since a bind that threw binds nothing later",
                  stayed, dev2 == null ? $"vhci {y}, no devnode" : $"{Usb.Service(dev2) ?? "no driver"}, holders {string.Join(",", WinUsbHolders(dev2))}");
            // The positive control: the re-import takes a binding, so the
            // input.inf above is the rule's, not a device that cannot bind.
            string again = dev2 == null ? "no devnode" : a.Send("bindwinusb", 120_000);
            Check("Zc: the program's next BindWinUsb binds the import that came back",
                  again == "OK bindwinusb Bound"
                  && WaitFor(() => string.Equals(Usb.Service(dev2!), "WinUSB", StringComparison.OrdinalIgnoreCase), 10_000),
                  $"{again}; {(dev2 == null ? "" : Usb.Service(dev2) ?? "no driver")}");
            a.Send("dispose");
        }
        finally
        {
            foreach (var (port, busId) in touched) Vhci.Stop("127.0.0.1", port, busId);
        }
    }

    // ── Ze ─────────────────────────────────────────────────────────────

    /// <summary>The CONFIGFLAG_REINSTALL bit (regstr.h) of an instance's
    /// ConfigFlags, present or not, or null when unreadable.</summary>
    static uint? ConfigFlags(string instanceId)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        return k?.GetValue("ConfigFlags") is int f ? (uint)f : null;
    }

    static (uint Status, uint Problem)? DevnodeStatus(string instanceId)
    {
        if (Native.CM_Locate_DevNodeW(out uint dev, instanceId, 0) != 0) return null;
        return Native.CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) == 0 ? (status, problem) : null;
    }

    /// <summary>Learn's test for an unplugged device's devnode ("Determining
    /// Whether a Device is Plugged In"), the one WinUsbBinding.HasLeft
    /// makes: not in the tree, and CM_Get_DevNode_Status on its phantom
    /// answers CR_NO_SUCH_DEVINST.</summary>
    static bool HasLeft(string instanceId)
    {
        const uint CM_LOCATE_DEVNODE_PHANTOM = 1, CR_NO_SUCH_DEVINST = 0x0D;
        if (Native.IsPresent(instanceId)) return false;
        if (Native.CM_Locate_DevNodeW(out uint dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != 0) return false;
        return Native.CM_Get_DevNode_Status(out _, out _, dev, 0) == CR_NO_SUCH_DEVINST;
    }

    static void ZHeldOpenAtTeardown()
    {
        const uint CONFIGFLAG_REINSTALL = 0x20, GENERIC_RW = 0xC0000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        // usbConfiguration.winUsb.interfaceGuid of profiles/nintendo/gamecube-adapter.json.
        var winUsbGuid = new Guid("7F216CFC-A59A-41F6-947D-E7BF407BCAD0");
        var touched = new HashSet<(int Port, string BusId)>();
        IntPtr handle = IntPtr.Zero;
        try
        {
            using var a = PeerProcess.Start("Ze-bound");
            if (!ZGuarded(a, "Ze")) return;
            string ca = a.Send("create gc -");
            if (!Ok(ca)) { Check("Ze: the program creates a GameCube adapter", false, ca); return; }
            var (portA, busA, x) = GcImport(a, ca, touched);
            string? devA = null;
            WaitFor(() => (devA = Usb.OnVhciPort(0x057E, 0x0337, x)) != null, 15_000);
            if (devA == null) { Check("Ze: its adapter enumerates", false, $"vhci {x}"); return; }
            string before = a.Send($"zarrival {devA}");
            string bound = a.Send("bindwinusb", 120_000);
            Check("Ze: its BindWinUsb binds", bound == "OK bindwinusb Bound", bound);
            if (bound != "OK bindwinusb Bound") return;
            string after = a.Send($"zarrival {devA}");
            Console.WriteLine($"    Ze: arrival before the bind {PeerProcess.Field(before, "arrival")}, after it {PeerProcess.Field(after, "arrival")}");

            string? path = null;
            WaitFor(() => (path = Native.InterfaceList(winUsbGuid, devA).FirstOrDefault()) != null, 10_000);
            if (path != null)
                handle = Native.CreateFileW(path, GENERIC_RW, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            Check("Ze: the probe opens the adapter's WinUSB interface", handle != IntPtr.Zero && handle != new IntPtr(-1),
                  path ?? "no interface");
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) { handle = IntPtr.Zero; return; }

            var driver = EnumDriver(devA);
            uint flags = ConfigFlags(devA) ?? 0;
            string d = a.Send("dispose", 120_000);
            Check("Ze: the program disposes its adapter while the probe holds it open", Ok(d), d);

            // An open handle does not keep the devnode in the device tree:
            // the PnP manager holds back only IRP_MN_REMOVE_DEVICE until the
            // handles close (Microsoft Learn, "Handling an
            // IRP_MN_SURPRISE_REMOVAL Request"), and the devnode reads as
            // gone at once (CR_NO_SUCH_DEVINST, "Determining Whether a Device
            // is Plugged In"). The handle the probe holds is stale from here.
            var left = Stopwatch.StartNew();
            bool leftAtDispose = WaitFor(() => HasLeft(devA), 5000);
            Check("Ze: with the probe's handle open, the devnode leaves at the dispose", leftAtDispose,
                  leftAtDispose ? $"{left.ElapsedMilliseconds} ms" : $"{(DevnodeStatus(devA) is var (s0, p0) ? $"status 0x{s0:X8} problem {p0}" : "status unreadable")}");
            // Before another device arrives on the instance: the dispose's
            // restore, or the follow-up it started, flagged it and dropped
            // the entry, and installed nothing on it.
            Check("Ze: before another device arrives, it has the reinstall flag and the entry is gone",
                  WaitFor(() => ((ConfigFlags(devA) ?? 0) & CONFIGFLAG_REINSTALL) != 0 && !WinUsbEntryExists(devA), 35_000),
                  $"ConfigFlags 0x{ConfigFlags(devA) ?? 0:X}, was 0x{flags:X}, entry {(WinUsbEntryExists(devA) ? "kept" : "gone")}");
            Check("Ze: and its driver and setup class are still what WinUSB gave it",
                  string.Equals(EnumDriver(devA).Service, driver.Service, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(EnumDriver(devA).Class, driver.Class, StringComparison.OrdinalIgnoreCase),
                  $"{driver.Service} {driver.Class} -> {EnumDriver(devA).Service} {EnumDriver(devA).Class}");

            using var b = PeerProcess.Start("Ze-other");
            if (!ZGuarded(b, "Ze")) return;
            string cb = b.Send("create gc -");
            var (_, _, xb) = Ok(cb) ? GcImport(b, cb, touched) : (0, "", 0);
            Check($"Ze: another program's adapter takes the vhci port {x} the bound one freed", Ok(cb) && xb == x, $"{cb}; vhci {xb}");
            string? devB = null;
            if (xb > 0) WaitFor(() => (devB = Usb.OnVhciPort(0x057E, 0x0337, xb)) != null, 15_000);
            Console.WriteLine($"    Ze: another program's adapter is {devB ?? "not enumerated"} on vhci port {xb} (the bound one was {devA} on {x})");
            // Same serial, so the same instance. The flag reinstalls it by
            // rank: input.inf, problem 10, and the install spends the flag.
            Check("Ze: it arrives on the instance the bound one left and installs by rank, the flag spent",
                  string.Equals(devB, devA, StringComparison.OrdinalIgnoreCase)
                  && WaitFor(() => string.Equals(Usb.Service(devA), "HidUsb", StringComparison.OrdinalIgnoreCase)
                                   && DevnodeStatus(devA) is { Problem: 10 }
                                   && ((ConfigFlags(devA) ?? 0) & CONFIGFLAG_REINSTALL) == 0, 15_000),
                  $"{devB ?? "none"}: {Usb.Service(devA) ?? "no driver"}, ConfigFlags 0x{ConfigFlags(devA) ?? 0:X}");

            // While the stale handle is still held, then after it closes:
            // nothing installs a driver on the other program's present
            // devnode, flags it, or claims it for the binding that left.
            bool kept = true;
            var statuses = new HashSet<string>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10_000)
            {
                if (sw.ElapsedMilliseconds >= 5000 && handle != IntPtr.Zero)
                {
                    Native.CloseHandle(handle);
                    handle = IntPtr.Zero;
                }
                if (DevnodeStatus(devA) is var (st, pr)) statuses.Add($"status 0x{st:X8} problem {pr}");
                kept &= Native.IsPresent(devA)
                        && string.Equals(Usb.Service(devA), "HidUsb", StringComparison.OrdinalIgnoreCase)
                        && ((ConfigFlags(devA) ?? 0) & CONFIGFLAG_REINSTALL) == 0
                        && !WinUsbEntryExists(devA);
                Thread.Sleep(100);
            }
            Console.WriteLine($"    Ze: the other adapter, 5 s with the stale handle held and 5 s after it closed: {(statuses.Count == 0 ? "never present" : string.Join("; ", statuses))}");
            Check("Ze: the other program's adapter stays present on input.inf, with no flag and no entry, as the stale handle closes",
                  kept, $"{Usb.Service(devA) ?? "no driver"}, ConfigFlags 0x{ConfigFlags(devA) ?? 0:X}, entry {WinUsbEntryExists(devA)}");
            if (Ok(cb)) b.Send("dispose");
        }
        finally
        {
            if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            foreach (var (port, busId) in touched) Vhci.Stop("127.0.0.1", port, busId);
        }
    }

    static bool WinUsbEntryExists(string instanceId)
    {
        using var kind = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroRegistryLedger\WinUsb");
        if (kind == null) return false;
        foreach (var n in kind.GetSubKeyNames())
        {
            using var e = kind.OpenSubKey(n);
            if (string.Equals(e?.GetValue("InstanceId") as string, instanceId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ── Zd ─────────────────────────────────────────────────────────────

    static void ZDisposeDuringBind()
    {
        using var a = PeerProcess.Start("Zd");
        if (!ZGuarded(a, "Zd")) return;
        string ca = a.Send("create gc -");
        if (!Ok(ca)) { Check("Zd: the program creates a GameCube adapter", false, ca); return; }
        var devnodes = Native.AdminMutex("WinUsb");
        string disposed;
        long ms;
        try
        {
            try { devnodes.WaitOne(); } catch (AbandonedMutexException) { }
            string s = a.Send("zbindasync");
            Check("Zd: a bind starts on another thread of the program", Ok(s), s);
            Thread.Sleep(3000);
            var sw = Stopwatch.StartNew();
            a.Post("dispose");
            disposed = a.Receive("dispose, while the bind waits", 15_000);
            ms = sw.ElapsedMilliseconds;
        }
        finally { devnodes.ReleaseMutex(); devnodes.Dispose(); }
        if (disposed.StartsWith("ERR timeout", StringComparison.Ordinal)) disposed = a.Receive("dispose", 120_000);
        Check("Zd: the dispose does not wait for the bind", Ok(disposed) && ms < 10_000, $"{disposed} in {ms} ms");
        string r = a.Send("zbindwait 60000", 90_000);
        Check("Zd: the bind gives up with ObjectDisposedException", r.Contains("ObjectDisposedException"), r);
    }
}

/// <summary>The peer's commands for section Z. Everything is read through
/// reflection, as in Peer.cs.
///
///   zcwd &lt;dir&gt;            set the program's current directory
///   zproduct              UsbipDriverInstaller.IsProductInstalled, timed
///   zpnputil &lt;args&gt;       UsbipDriverInstaller.RunPnputil, timed
///   zruntool &lt;ms&gt; &lt;path&gt;  PnputilHelper.RunTool on a program, timed
///   zrun &lt;ms&gt; &lt;mode&gt;     UsbipDriverInstaller.RunForProbe: work that
///                         replies "OK zrun started" and holds &lt;ms&gt;
///                         when &lt;ms&gt; is not 0, then reports progress.
///                         mode none: no callback. check: the callback
///                         tries the deploy and the transport locks from
///                         another thread. throw: the callback throws.
///                         block: the work reports, then holds its lock 3 s
///                         and ends, and the callback blocks 7 s and tries
///                         both locks 4.5 s in
///   zgate                 DriverDeploy.EnsureForCreate, timed
///   zrepair &lt;pnp&gt; &lt;budget&gt; UsbipDriverInstaller.RepairForProbe with a
///                         restart that sleeps &lt;pnp&gt; ms and a transport
///                         that never answers
///   zrepairlog            when that restart started and ended, UTC ticks
///   zlegacy               UsbipDriverInstaller.EnsureInstalled, and whether
///                         its legacy check is marked done
///   zinstallers &lt;ms&gt;      UsbipDriverInstaller.WaitForRunningInstallers
///   zwinusbsweep &lt;ms&gt;     a WinUSB ledger entry for a devnode that does
///                         not exist, swept while another thread holds the
///                         binding's locks &lt;ms&gt;, with follow-ups on.
///                         order=before when the sweep ended before the hold
///                         let go, via= who restored the entry (sweep, hold
///                         for a restore while the locks were held, followup,
///                         or none within 10 s), restored=after when that was
///                         after the hold let go
///   zfollow queue|claim|check|end  the follow-up's decision: queue holds
///                         the WinUSB lock on a thread, claims the entry,
///                         and runs the full cleanup, which leaves the entry
///                         to its follow-up. claim claims it again under the
///                         same name on that thread and lets the lock go.
///                         check reads the entry and whether its claim is
///                         the fresh one. end deletes the entry and waits
///                         for the follow-up to end
///   zclaimsweep sweep|all|same  such an entry, one per part, queued by a
///                         sweep or the full cleanup and claimed under the
///                         WinUSB lock in the sweep's hook, before the
///                         restore. same: the entry already has this
///                         program's holder when queued
///   zcert &lt;name&gt;          DriverBuilder.EnsureTestCertificate(name)
///   zguard                whether the SDK's WinUsbBinding.DepartingDevnodeRule
///                         is ZDepartingRule or later
///   zarrival &lt;id&gt;         WinUsbBinding.ArrivalOf on a devnode
///   zbindasync            the last controller's BindWinUsb on a thread
///   zbindwait &lt;ms&gt;        that bind's outcome
///   zreleasecost &lt;n&gt; [nofollow]  RegistryLedger.Release of an index this
///                         program holds nothing for, n times: p50, p95, max
///                         ms. With nofollow the program starts no follow-up
///                         from then on</summary>
internal static class PeerZ
{
    static Assembly Sdk => typeof(HMContext).Assembly;
    const string Installer = "HIDMaestro.Internal.Usbip.UsbipDriverInstaller";
    const string Ledger = "HIDMaestro.Internal.RegistryLedger";
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // By name alone when no types are given, which throws
    // AmbiguousMatchException once the name has a second overload. A name
    // with overloads goes through Exact.
    static MethodInfo M(string type, string name, params Type[] args) =>
        (args.Length == 0 ? Sdk.GetType(type)?.GetMethod(name, Any) : Sdk.GetType(type)?.GetMethod(name, Any, args))
        ?? throw new MissingMethodException(type, name);

    /// <summary>The overload with exactly these parameter types, none
    /// meaning the one without parameters.</summary>
    static MethodInfo Exact(string type, string name, params Type[] types) =>
        Sdk.GetType(type)?.GetMethod(name, Any, types) ?? throw new MissingMethodException(type, name);

    static readonly List<(long Start, long End)> s_restarts = new();
    static Thread? s_bind;
    static string s_bindResult = "";

    public static string? Run(string[] a)
    {
        switch (a[0])
        {
            case "zcwd":
                Environment.CurrentDirectory = string.Join(' ', a.Skip(1));
                return "OK zcwd";
            case "zproduct":
            {
                var sw = Stopwatch.StartNew();
                // The overload with no parameters, the one that runs
                // System32's pnputil. The other takes the run to make.
                bool v = (bool)Exact(Installer, "IsProductInstalled").Invoke(null, null)!;
                return $"OK zproduct installed={v} ms={sw.ElapsedMilliseconds}";
            }
            case "zpnputil":
            {
                var sw = Stopwatch.StartNew();
                int rc = (int)M(Installer, "RunPnputil").Invoke(null, new object[] { string.Join(' ', a.Skip(1)) })!;
                return $"OK zpnputil rc={rc} ms={sw.ElapsedMilliseconds}";
            }
            case "zruntool":
            {
                var sw = Stopwatch.StartNew();
                object r = M("HIDMaestro.Internal.PnputilHelper", "RunTool").Invoke(null,
                    new object[] { string.Join(' ', a.Skip(2)), "/enum-drivers", int.Parse(a[1]) })!;
                return $"OK zruntool result={r.ToString()!.Replace(' ', '_')} ms={sw.ElapsedMilliseconds}";
            }
            case "zrun":
                return RunWork(int.Parse(a[1]), a[2]);
            case "zgate":
            {
                if (!(bool)M("HIDMaestro.Internal.DriverBuilder", "DriverStoreHasBothPackages").Invoke(null, null)!)
                    return "ERR zgate the driver store lacks a package, and the gate would deploy";
                var gate = M("HIDMaestro.Internal.DriverDeploy", "EnsureForCreate");
                var sw = Stopwatch.StartNew();
                gate.Invoke(null, gate.GetParameters().Length == 0 ? null : new object?[gate.GetParameters().Length]);
                return FormattableString.Invariant($"OK zgate ms={sw.Elapsed.TotalMilliseconds:F2}");
            }
            case "zrepair":
            {
                int pnp = int.Parse(a[1]), budget = int.Parse(a[2]);
                Action restart = () =>
                {
                    long start = DateTime.UtcNow.Ticks;
                    Thread.Sleep(pnp);
                    lock (s_restarts) s_restarts.Add((start, DateTime.UtcNow.Ticks));
                };
                Func<bool> available = () => false;
                var sw = Stopwatch.StartNew();
                bool r = (bool)M(Installer, "RepairForProbe").Invoke(null, new object?[] { available, restart, budget, null })!;
                return $"OK zrepair result={r} ms={sw.ElapsedMilliseconds}";
            }
            case "zrepairlog":
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20_000)
                {
                    lock (s_restarts)
                        if (s_restarts.Count > 0) return $"OK zrepairlog start={s_restarts[0].Start} end={s_restarts[0].End} n={s_restarts.Count}";
                    Thread.Sleep(50);
                }
                return "ERR zrepairlog no restart ran";
            }
            case "zlegacy":
            {
                var sw = Stopwatch.StartNew();
                string result;
                try { result = M(Installer, "EnsureInstalled").Invoke(null, new object?[] { null })!.ToString()!; }
                catch (TargetInvocationException tie) { result = "threw:" + tie.InnerException?.GetType().Name; }
                object? flag = Sdk.GetType(Installer)?.GetField("s_legacyChecked", Any)?.GetValue(null);
                return $"OK zlegacy result={result} checked={flag} ms={sw.ElapsedMilliseconds}";
            }
            case "zinstallers":
            {
                var sw = Stopwatch.StartNew();
                bool r = (bool)M(Installer, "WaitForRunningInstallers").Invoke(null, new object[] { int.Parse(a[1]) })!;
                return $"OK zinstallers result={r} ms={sw.ElapsedMilliseconds}";
            }
            case "zwinusbsweep":
                return WinUsbSweep(int.Parse(a[1]));
            case "zclaimsweep":
                return ClaimDuringSweep(a[1]);
            case "zfollow":
                return FollowUpDecides(a[1]);
            case "zcert":
            {
                var sw = Stopwatch.StartNew();
                M("HIDMaestro.Internal.DriverBuilder", "EnsureTestCertificate", typeof(string)).Invoke(null, new object[] { a[1] });
                return $"OK zcert ms={sw.ElapsedMilliseconds}";
            }
            case "zbindasync":
            {
                var last = Peer.Last() ?? throw new InvalidOperationException("no controller");
                s_bindResult = "";
                s_bind = new Thread(() =>
                {
                    try { s_bindResult = $"bound {last.BindWinUsb()}"; }
                    catch (Exception ex) { s_bindResult = $"threw {ex.GetType().Name}"; }
                }) { IsBackground = true };
                s_bind.Start();
                return "OK zbindasync";
            }
            case "zguard":
            {
                object? rule = Sdk.GetType("HIDMaestro.Internal.WinUsbBinding")?.GetField("DepartingDevnodeRule", Any)?.GetValue(null);
                return $"OK zguard guard={rule is int r && r >= Program.ZDepartingRule} rule={rule ?? "none"}";
            }
            case "zarrival":
            {
                object? v = M("HIDMaestro.Internal.WinUsbBinding", "ArrivalOf").Invoke(null, new object[] { string.Join(' ', a.Skip(1)) });
                return $"OK zarrival arrival={v ?? "none"}";
            }
            case "zreleasecost":
            {
                // A controller index this program holds nothing for, so each
                // call is a teardown's sweep of the other entries only.
                // "nofollow": this program starts no follow-up, so every
                // sweep takes the path an unfollowed entry takes.
                if (a.Length > 2 && a[2] == "nofollow")
                    (Sdk.GetType(Ledger)?.GetField("NoFollowUpForProbe", Any)
                        ?? throw new MissingFieldException(Ledger, "NoFollowUpForProbe")).SetValue(null, true);
                // Release(int), not Release(IEnumerable<int>).
                var release = Exact(Ledger, "Release", typeof(int));
                var ms = new List<double>();
                for (int i = 0; i < int.Parse(a[1]); i++)
                {
                    var sw = Stopwatch.StartNew();
                    release.Invoke(null, new object[] { 9999 });
                    ms.Add(sw.Elapsed.TotalMilliseconds);
                }
                ms.Sort();
                return FormattableString.Invariant(
                    $"OK zreleasecost p50={ms[ms.Count / 2]:F1} p95={ms[(int)Math.Ceiling(ms.Count * 0.95) - 1]:F1} max={ms[^1]:F1}");
            }
            case "zbindwait":
                return s_bind != null && s_bind.Join(int.Parse(a[1])) ? $"OK zbindwait {s_bindResult}" : "ERR zbindwait still binding";
            default:
                return null;
        }
    }

    static string RunWork(int holdMs, string mode)
    {
        int calls = 0;
        bool? free = null;
        Action<string>? progress = mode switch
        {
            "none" => null,
            "check" => _ => { calls++; free = LocksFreeFromAnotherThread(); },
            "throw" => _ => { calls++; throw new InvalidOperationException("zrun: the progress callback throws"); },
            "block" => _ =>
            {
                calls++;
                var t = new Thread(() =>
                {
                    Thread.Sleep(4500);
                    free = LocksFreeFromAnotherThread();
                });
                t.Start();
                Thread.Sleep(7000);
                t.Join();
            },
            _ => throw new ArgumentException(mode),
        };
        Func<Action<string>, bool> work = report =>
        {
            if (holdMs > 0)
            {
                Console.WriteLine("OK zrun started");
                Console.Out.Flush();
                Thread.Sleep(holdMs);
            }
            report("zrun progress");
            if (mode == "block") Thread.Sleep(3000);
            return true;
        };
        var sw = Stopwatch.StartNew();
        string result;
        try { result = M(Installer, "RunForProbe").Invoke(null, new object?[] { work, progress })!.ToString()!; }
        catch (TargetInvocationException tie) { result = "threw:" + tie.InnerException?.GetType().Name; }
        return $"OK zrun result={result} calls={calls} free={free?.ToString() ?? "none"} ms={sw.ElapsedMilliseconds}";
    }

    /// <summary>Whether a thread other than the caller takes the deploy and
    /// the transport locks within 2 s each.</summary>
    static bool LocksFreeFromAnotherThread()
    {
        bool ok = false;
        var t = new Thread(() => ok = TryTake("Deploy") && TryTake("Transport"));
        t.Start();
        t.Join();
        return ok;
    }

    static string AdminName(string obj) => (string)M("HIDMaestro.Internal.AdminNamespace", "Name").Invoke(null, new object[] { obj })!;

    static bool TryTake(string obj)
    {
        using var m = new Mutex(false, AdminName(obj));
        try { if (!m.WaitOne(2000)) return false; }
        catch (AbandonedMutexException) { }
        m.ReleaseMutex();
        return true;
    }

    // Acquire(), not Acquire(int).
    static object AcquireLedger() => Exact("HIDMaestro.Internal.LedgerLock", "Acquire").Invoke(null, null)!;

    static void ReleaseLedger(object l)
    {
        l.GetType().GetMethod("ReleaseMutex")!.Invoke(l, null);
        ((IDisposable)l).Dispose();
    }

    /// <summary>A ledger entry for a WinUSB devnode that does not exist, with
    /// no holder.</summary>
    static string FakeEntry(string id)
    {
        string name = (string)M(Ledger, "WinUsbEntryName").Invoke(null, new object[] { id })!;
        string path = $@"SOFTWARE\HIDMaestroRegistryLedger\WinUsb\{name}";
        using var k = Registry.LocalMachine.CreateSubKey(path);
        k.SetValue("InstanceId", id);
        k.SetValue("PriorService", "");
        k.SetValue("PriorInf", "");
        return path;
    }

    static string WinUsbSweep(int holdMs)
    {
        const string id = @"USB\VID_057E&PID_0337\HMZ7TEST";
        string path = FakeEntry(id);
        try
        {
            // Another thread's WinUSB work: the binding's process lock, and
            // its machine-wide devnode lock where the SDK has one.
            object binding = Sdk.GetType("HIDMaestro.Internal.WinUsbBinding")!.GetField("s_lock", Any)!.GetValue(null)!;
            string devnodes = AdminName("WinUsb");
            using var held = new ManualResetEventSlim();
            long releasedAt = 0, sweptAt = 0;
            // This program starts follow-ups, as every program but the
            // probe's zreleasecost peer does.
            (Sdk.GetType(Ledger)?.GetField("NoFollowUpForProbe", Any)
                ?? throw new MissingFieldException(Ledger, "NoFollowUpForProbe")).SetValue(null, false);
            bool Present() { using var k = Registry.LocalMachine.OpenSubKey(path); return k != null; }
            var holder = new Thread(() =>
            {
                using var m = new Mutex(false, devnodes);
                try { m.WaitOne(); } catch (AbandonedMutexException) { }
                lock (binding)
                {
                    held.Set();
                    Thread.Sleep(holdMs);
                    // Stamped before either lock goes, so a restore that
                    // waited for them always ends after the stamp.
                    releasedAt = Stopwatch.GetTimestamp();
                }
                m.ReleaseMutex();
            });
            holder.Start();
            held.Wait();

            var sweepSw = Stopwatch.StartNew();
            long sweepMs = -1;
            var sweeper = new Thread(() =>
            {
                M(Ledger, "PruneDead").Invoke(null, null);
                sweptAt = Stopwatch.GetTimestamp();
                sweepMs = sweepSw.ElapsedMilliseconds;
            });
            sweeper.Start();
            // 300 ms into the 3 s hold: the sweep that does not wait has
            // ended, one that waits is still waiting.
            Thread.Sleep(300);
            bool during = Present();
            var sw = Stopwatch.StartNew();
            object l = AcquireLedger();
            long ledgerMs = sw.ElapsedMilliseconds;
            ReleaseLedger(l);
            sweeper.Join();
            bool afterSweep = Present();
            string via = afterSweep ? "none" : "sweep";
            long goneAt = afterSweep ? 0 : sweptAt;
            // Watched while the holder runs. A restore before its release
            // stamp did not wait for the locks. One after it is the
            // follow-up's, which waited for them.
            while (afterSweep && holder.IsAlive)
            {
                if (!Present())
                {
                    goneAt = Stopwatch.GetTimestamp();
                    via = Interlocked.Read(ref releasedAt) == 0 ? "hold" : "followup";
                    break;
                }
                Thread.Sleep(10);
            }
            holder.Join();
            // Once the locks are free the entry's follow-up, which waits for
            // the devnode lock, restores it. This devnode does not exist, so
            // there is nothing else to wait for. No second sweep runs: a
            // build that starts no follow-up leaves the entry.
            if (via == "none")
            {
                var wait = Stopwatch.StartNew();
                while (Present() && wait.ElapsedMilliseconds < 10_000) Thread.Sleep(20);
                if (!Present()) { via = "followup"; goneAt = Stopwatch.GetTimestamp(); }
            }
            bool gone = !Present();
            // Both threads are joined, so both stamps are written.
            string order = sweptAt > releasedAt ? "after" : "before";
            string restored = !gone ? "never" : goneAt > releasedAt ? "after" : "before";
            long goneMs = gone ? (long)Stopwatch.GetElapsedTime(releasedAt, goneAt).TotalMilliseconds : -1;
            return $"OK zwinusbsweep ledgerms={ledgerMs} during={during} aftersweep={afterSweep} sweepms={sweepMs} order={order} gone={gone} via={via} restored={restored} gonems={goneMs}";
        }
        finally
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); } catch { }
        }
    }

    /// <summary>A sweep (or the full cleanup) queues the entry, and another
    /// thread, holding the WinUSB lock as every claim does, claims it before
    /// the restore. The entry and that holder must survive. The claim waits
    /// for the SDK's hook after the queue (RegistryLedger.WinUsbQueuedForProbe),
    /// or 1 s on an SDK without it.</summary>
    static string ClaimDuringSweep(string part)
    {
        bool all = part is "all" or "same", sameName = part == "same";
        // An instance id of its own per part: a follow-up an earlier part
        // started in this program would otherwise watch the entry, and a
        // sweep skips an entry its own program follows.
        string id = $@"USB\VID_057E&PID_0337\HMZA{part.ToUpperInvariant()}";
        string path = FakeEntry(id);
        var hook = Sdk.GetType(Ledger)?.GetField("WinUsbQueuedForProbe", Any);
        try
        {
            string devnodes = AdminName("WinUsb");
            var claim = M(Ledger, "ClaimWinUsb");
            // The claim's parameters: the devnode, its prior service and INF,
            // the controller index, and the ledger timeout where it has one.
            object?[] args = claim.GetParameters().Length == 4
                ? new object?[] { id, "", "", 7 }
                : new object?[] { id, "", "", 7, 30_000 };
            // The full cleanup restores an entry regardless of the holders
            // it saw. With sameName the entry already has this holder when
            // it is queued, and the claim in the window is a fresh one under
            // the same name.
            if (sameName) claim.Invoke(null, args);
            // Every claim takes the WinUSB lock, as this one does.
            void Claim()
            {
                using var m = new Mutex(false, devnodes);
                try { m.WaitOne(); } catch (AbandonedMutexException) { }
                try { claim.Invoke(null, args); }
                finally { m.ReleaseMutex(); }
            }
            if (hook != null)
            {
                // The sweep calls the hook on its own thread once the entry is
                // queued and the ledger's lock is let go, before the restore.
                // The claim runs there and lets the WinUSB lock go, so the
                // restore takes the lock at once, decides on the same thread
                // before the sweep returns, and leaves nothing for a
                // follow-up thread to decide later.
                hook.SetValue(null, (Action)Claim);
                M(Ledger, all ? "ReleaseAll" : "PruneDead").Invoke(null, null);
            }
            else
            {
                // An SDK without the hook: a thread holds the WinUSB lock from
                // before the sweep and claims after a fixed wait.
                using var locked = new ManualResetEventSlim();
                using var queued = new ManualResetEventSlim();
                var claimer = new Thread(() =>
                {
                    using var m = new Mutex(false, devnodes);
                    try { m.WaitOne(); } catch (AbandonedMutexException) { }
                    locked.Set();
                    queued.Wait();
                    try { claim.Invoke(null, args); }
                    finally { m.ReleaseMutex(); }
                });
                claimer.Start();
                locked.Wait();
                var sweeper = new Thread(() => M(Ledger, all ? "ReleaseAll" : "PruneDead").Invoke(null, null));
                sweeper.Start();
                Thread.Sleep(1000);
                queued.Set();
                claimer.Join();
                sweeper.Join();
            }
            bool kept, holder;
            using (var k = Registry.LocalMachine.OpenSubKey(path))
            {
                kept = k != null;
                using var h = k?.OpenSubKey("Holders");
                holder = h != null && h.GetValueNames().Any(n => n.StartsWith($"{Environment.ProcessId}-", StringComparison.Ordinal));
            }
            return $"OK zclaimsweep kept={kept} holder={holder} handshake={(hook != null ? "hook" : "timed")}";
        }
        finally
        {
            hook?.SetValue(null, null);
            try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); } catch { }
        }
    }
    static Thread? s_followHolder;
    static ManualResetEventSlim? s_followClaim;
    static string? s_followPath, s_followFresh;

    /// <summary>The steps of zfollow.</summary>
    static string FollowUpDecides(string step)
    {
        const string id = @"USB\VID_057E&PID_0337\HMZAFOLLOW";
        var claim = M(Ledger, "ClaimWinUsb");
        object?[] args = claim.GetParameters().Length == 4
            ? new object?[] { id, "", "", 7 }
            : new object?[] { id, "", "", 7, 30_000 };
        string ClaimValue()
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{s_followPath}\Holders");
            return k?.GetValueNames().FirstOrDefault(n => n.StartsWith($"{Environment.ProcessId}-", StringComparison.Ordinal)) is string h
                ? $"{h}={k.GetValue(h)}" : "";
        }
        switch (step)
        {
            case "queue":
            {
                s_followPath = FakeEntry(id);
                claim.Invoke(null, args);
                string devnodes = AdminName("WinUsb");
                using var locked = new ManualResetEventSlim();
                s_followClaim = new ManualResetEventSlim();
                var go = s_followClaim;
                s_followHolder = new Thread(() =>
                {
                    using var m = new Mutex(false, devnodes);
                    try { m.WaitOne(); } catch (AbandonedMutexException) { }
                    locked.Set();
                    go.Wait();
                    try { claim.Invoke(null, args); }
                    finally { m.ReleaseMutex(); }
                }) { IsBackground = true };
                s_followHolder.Start();
                locked.Wait();
                M(Ledger, "ReleaseAll").Invoke(null, null);
                using var k = Registry.LocalMachine.OpenSubKey(s_followPath);
                return $"OK zfollow kept={k != null}";
            }
            case "claim":
            {
                string before = ClaimValue();
                s_followClaim!.Set();
                bool done = s_followHolder!.Join(20_000);
                s_followFresh = ClaimValue();
                return done && s_followFresh.Length > 0 && s_followFresh != before
                    ? "OK zfollow claimed" : $"ERR zfollow claim done={done} before={before} after={s_followFresh}";
            }
            case "check":
            {
                using var k = Registry.LocalMachine.OpenSubKey(s_followPath!);
                return $"OK zfollow kept={k != null} fresh={k != null && ClaimValue() == s_followFresh}";
            }
            case "end":
            {
                try { Registry.LocalMachine.DeleteSubKeyTree(s_followPath!, throwOnMissingSubKey: false); } catch { }
                var sw = Stopwatch.StartNew();
                int n;
                while ((n = Peer.CountThreads("HMWinUsbFollowUp")) > 0 && sw.ElapsedMilliseconds < 20_000) Thread.Sleep(100);
                return $"OK zfollow threads={n} ms={sw.ElapsedMilliseconds}";
            }
            default:
                return $"ERR zfollow {step}";
        }
    }
}
