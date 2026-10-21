// S81: WinUSB goes on one virtual adapter and no real one, and the binding
// goes when the program that made it does.
//
// BindWinUsb installs a per-profile package whose INF matches the adapter
// only through its vendor-scoped compatible ID behind fifteen IDs nothing
// carries, so Windows ranks input.inf above it and never picks it on its
// own, and binds it with DiInstallDevice on the one devnode. The registry
// ledger records the binding and restores the devnode when the controller
// goes or, after a crash, at the next program's reclaim: a gone devnode gets
// the reinstall flag, so its next arrival installs by rank, and a present
// one keeps its driver and its entry until a sweep finds it gone. The bound
// devnode carries no flag, since libusb skips a device whose install state
// the flag makes nonzero.
//
// Checked here:
//  - the binding, the package's rank against input.inf's, no flag on the
//    bound devnode, and AlreadyBound on a second call.
//  - a second adapter beside a bound one stays on input.inf until it is
//    bound itself.
//  - dispose leaves no ledger hold, and the gone devnode gets the flag.
//  - five create-bind-dispose cycles, each binding on the first call.
//  - a bind while another thread restores under the devnode lock: the
//    bind waits for the lock and finishes, where taking the devnode and
//    ledger locks in the other order deadlocked both threads.
//  - the staging lock, a leaf below the deploy lock: a thread holds the
//    deploy lock and then the staging lock, as a deploy's prune does, while
//    a second thread stages and a third takes the deploy lock and stages.
//    Both wait for the first and finish.
//  - a program killed while bound: an adapter that arrives on the same
//    instance before any reclaim finds WinUSB, as after Zadig, and the
//    reclaim leaves it on WinUSB until it leaves, when the follow-up the
//    reclaim started flags it. When the reclaim comes first, the arrival
//    gets input.inf. When the arrival binds, BindWinUsb finds it already
//    bound and holds it, a sweep leaves it on WinUSB, and once it is
//    disposed and gone its dispose, or the follow-up the dispose started,
//    flags it. Each flag check waits for the follow-up, which polls every
//    250 ms, since a sweep leaves an entry a follow-up watches to it.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that embeds
// HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    const uint CONFIGFLAG_REINSTALL = 0x20;

    /// <summary>How long a check looks after a sweep for a driver change on
    /// a present devnode. A sweep restores its entries before it returns
    /// (RegistryLedger.Sweep), and a restore that installed a driver would
    /// do it with DiInstallDevice, which returns once the driver is on the
    /// devnode. What runs after the sweep is the follow-up it started, which
    /// looks again every 250 ms (RegistryLedger.FollowUp). Two of its laps.</summary>
    const int SweepSettleMs = 500;

    /// <summary>How long a devnode may take to get input.inf by rank. That is
    /// a device install, which Windows runs one at a time for the whole
    /// machine, so another program's install delays it with no defect. A
    /// hang bound, some 10 times the seconds an install takes.</summary>
    const int InstallBoundMs = 60_000;

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child")
            return Probe.RefuseWithoutDepartingRule("S81's child") ? 1 : Child();

        Console.WriteLine("=== S81: the WinUSB binding of the GameCube adapter persona ===");
        if (Probe.OtherSdkRunning()) return 2;
        if (Probe.RefuseWithoutDepartingRule("S81")) return Probe.Finish("S81");

        // A real adapter plugged into the machine is no adapter of this run's,
        // so the last check leaves out what was present before the first create.
        var present = Devnodes.PresentAdapters();
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var gc = ctx.GetProfile("gamecube-adapter")!;

        BindAndRank(ctx, gc);
        Cycles(ctx, gc);
        LockOrder(ctx, gc);
        StagingLockOrder();
        Crash(ctx, gc, reclaimFirst: false);
        Crash(ctx, gc, reclaimFirst: true);
        Crash(ctx, gc, reclaimFirst: false, bindArrival: true);

        Console.WriteLine("-- afterward");
        Probe.Check("no WinUSB hold left in the ledger", RegistryLedger.HeldWinUsbDevnodes().Count == 0,
            string.Join(", ", RegistryLedger.HeldWinUsbDevnodes()));
        List<string> Left() => Devnodes.PresentAdapters().Except(present, StringComparer.OrdinalIgnoreCase).ToList();
        Probe.Check("no adapter of this run's left present", Probe.WaitFor(() => Left().Count == 0, Probe.HangMs()),
            string.Join(", ", Left()));
        return Probe.Finish("S81");
    }

    // ── One binding, its rank and a second adapter ──────────────────────

    static void BindAndRank(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- the binding");
        string? first = null, second = null;
        using (var a = ctx.CreateController(gc))
        {
            first = Arrived(a);
            Probe.Check("the adapter arrives", first != null, first ?? "");
            if (first == null) return;
            var bound = a.BindWinUsb();
            Probe.Check("BindWinUsb binds WinUSB", bound == HMWinUsbBinding.Bound, $"{bound}");
            Probe.Check("the devnode runs WinUSB", Probe.WaitFor(() => Devnodes.Service(first) == "WINUSB" && Devnodes.Status(first) is { Problem: 0 }, Probe.HangMs()),
                $"{Devnodes.Service(first)}");
            Probe.Check("it carries no reinstall flag, which libusb would read as an install problem",
                ((Devnodes.ConfigFlags(first) ?? 0) & CONFIGFLAG_REINSTALL) == 0,
                $"ConfigFlags 0x{Devnodes.ConfigFlags(first) ?? 0:X}");
            Probe.Check("the ledger holds it", RegistryLedger.HeldWinUsbDevnodes().Contains(first, StringComparer.OrdinalIgnoreCase));

            var ranks = DriverRanks(first);
            // Windows 10 generates no vendor-scoped compatible ID, so there the
            // package matches no devnode, and input.inf's USB\Class_03 comes
            // third among the compatible IDs rather than sixth. Measured on
            // 2026-10-18: 0x00FF3205 on Windows 11 26200, 0x00FF3202 on
            // Windows 10 19044.
            bool vendorScoped = Devnodes.CompatibleIds(first).Exists(c => c.StartsWith(@"USB\COMPAT_VID_057E&", StringComparison.OrdinalIgnoreCase));
            uint inputRank = vendorScoped ? 0x00FF3205u : 0x00FF3202u;
            Probe.Check($"input.inf matches it and ranks 0x{inputRank:X8}, best",
                ranks.TryGetValue("input.inf", out var input) && input.Rank == inputRank && input.Status.Contains("Best Ranked"),
                Describe(ranks));
            var ours = ranks.Values.FirstOrDefault(r => r.Original.StartsWith("hmwinusb_", StringComparison.OrdinalIgnoreCase));
            if (vendorScoped)
                Probe.Check("HIDMaestro's package matches through the vendor-scoped compatible ID at rank 0x00FF3F00, outranked",
                    ours.Original == "hmwinusb_gamecube-adapter.inf" && ours.Rank == 0x00FF3F00 && ours.Status.Contains("Outranked"),
                    Describe(ranks));
            else
                Probe.Check("HIDMaestro's package matches nothing on a devnode with no vendor-scoped compatible ID, so Windows never chooses it",
                    ours.Original == null, Describe(ranks));

            Probe.Check("a second BindWinUsb finds it already bound", a.BindWinUsb() == HMWinUsbBinding.AlreadyBound);

            using (var b = ctx.CreateController(gc))
            {
                second = Arrived(b, except: first);
                Probe.Check("a second adapter arrives on a port-named instance", second != null && !second.Equals(first, StringComparison.OrdinalIgnoreCase),
                    second ?? "");
                if (second != null)
                {
                    Probe.WaitFor(() => Devnodes.Status(second) is { Problem: 10 }, Probe.HangMs(InstallBoundMs));
                    Probe.Check("it gets input.inf, as a real adapter would, while the first stays on WinUSB",
                        Devnodes.Service(second) == "HidUsb" && Devnodes.Service(first) == "WINUSB",
                        $"{Devnodes.Service(second)} / {Devnodes.Service(first)}");
                    var bound2 = b.BindWinUsb();
                    Probe.Check("binding the second takes its devnode alone", bound2 == HMWinUsbBinding.Bound
                        && Devnodes.Service(second) == "WINUSB" && Devnodes.Service(first) == "WINUSB", $"{bound2}");
                }
            }
            if (second != null)
                Probe.Check("the second's hold goes with it", Probe.WaitFor(
                    () => !RegistryLedger.HeldWinUsbDevnodes().Contains(second, StringComparer.OrdinalIgnoreCase), Probe.HangMs()));
        }
        Console.WriteLine("-- dispose");
        Probe.Check("the first's hold goes with it",
            !RegistryLedger.HeldWinUsbDevnodes().Contains(first!, StringComparer.OrdinalIgnoreCase));
        Probe.Check("its devnode leaves", Probe.WaitFor(() => !Devnodes.IsPresent(first!), Probe.HangMs()));
        // A devnode still present when the teardown restored it keeps its
        // entry, and the follow-up that restore started flags it once it is
        // gone. A sweep leaves an entry a follow-up watches to it.
        RegistryLedger.PruneDead();
        FlaggedOnceGone(first!, "and the restore gives the gone devnode the reinstall flag for its next arrival, and the entry goes");
    }

    // ── Repeated cycles ─────────────────────────────────────────────────

    static void Cycles(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- five create, bind, dispose cycles");
        var results = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            using var c = ctx.CreateController(gc);
            string? id = Arrived(c);
            if (id == null) { results.Add("absent"); continue; }
            try { results.Add(c.BindWinUsb().ToString()); }
            catch (Exception ex) { results.Add($"{ex.GetType().Name}: {ex.Message}"); }
            Probe.WaitFor(() => Devnodes.Service(id) == "WINUSB", 5000);
        }
        Probe.Check("every cycle binds on the first call", results.All(r => r == "Bound"), string.Join(", ", results));
        Probe.Check("no hold left after them", RegistryLedger.HeldWinUsbDevnodes().Count == 0);
    }

    // ── The devnode lock and the ledger's ───────────────────────────────

    /// <summary>A restore holds the machine-wide devnode lock and takes the
    /// ledger's lock under it to delete the entry
    /// (RegistryLedger.RestoreWinUsbEntries). Here a thread holds the devnode
    /// lock and runs WinUsbBinding.RestoreLocked, which takes no ledger lock.
    /// A bind on another thread waits for the devnode lock, then claims
    /// under the ledger's, in the same order.</summary>
    static void LockOrder(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- a bind while another thread restores under the devnode lock");
        using var a = ctx.CreateController(gc, "s81-lock-order");
        string? id = Arrived(a);
        Probe.Check("the adapter arrives", id != null, id ?? "");
        if (id == null) return;

        using var held = new ManualResetEventSlim();
        var restorer = new Thread(() =>
        {
            var devnodes = WinUsbBinding.AcquireDevnodeLock();
            try
            {
                held.Set();
                // Long enough for the bind to reach the devnode lock.
                Thread.Sleep(1500);
                WinUsbBinding.RestoreLocked(@"USB\VID_057E&PID_0337\S81_NOT_PRESENT", priorInf: null);
            }
            finally { devnodes.ReleaseMutex(); devnodes.Dispose(); }
        }) { IsBackground = true };
        restorer.Start();
        held.Wait(Probe.HangMs());

        HMWinUsbBinding? bound = null;
        string error = "";
        var binder = new Thread(() =>
        {
            try { bound = a.BindWinUsb(); }
            catch (Exception ex) { error = ex.Message; }
        }) { IsBackground = true };
        binder.Start();
        bool restored = restorer.Join(Probe.HangMs(180_000));
        bool finished = binder.Join(Probe.HangMs(180_000));
        Probe.Check("the restore finishes while the bind waits for the devnode lock", restored);
        Probe.Check("the bind finishes after it and binds WinUSB", finished && bound == HMWinUsbBinding.Bound,
            finished ? $"{bound} {error}" : "both threads still waiting at the hang bound");
        if (!restored || !finished)
        {
            // The two threads hold the machine-wide devnode lock between them.
            // Ending the process is the only way to give it back.
            Probe.Finish("S81");
            Environment.Exit(1);
        }
    }

    // ── The staging lock ────────────────────────────────────────────────

    /// <summary>The staging lock is taken under the deploy lock and holds no
    /// other machine-wide lock (ProtectedStaging's lock-order note). A holder
    /// of both, in that order, delays a staging and a deploy-then-staging on
    /// two other threads, and both finish once it lets go.</summary>
    static void StagingLockOrder()
    {
        Console.WriteLine("-- a staging and a deploy while another thread holds the deploy and staging locks");
        DriverBuilder.EnsureExtracted();
        using var held = new ManualResetEventSlim();
        // Set by the holder before it lets go. A staging or deploy that
        // finishes with it unset did not wait for the lock. Timing the wait
        // instead failed when other work delayed the waiting threads' start.
        int letGo = 0;
        var holder = new Thread(() =>
        {
            var deploy = DriverDeploy.Acquire();
            try
            {
                var staging = ProtectedStaging.AcquireStagingLock();
                try
                {
                    held.Set();
                    Thread.Sleep(1500);
                    Volatile.Write(ref letGo, 1);
                }
                finally { staging.ReleaseMutex(); staging.Dispose(); }
            }
            finally { deploy.ReleaseMutex(); deploy.Dispose(); }
        }) { IsBackground = true };
        holder.Start();
        held.Wait(Probe.HangMs());

        long stagedMs = -1, deployedMs = -1;
        bool stagedAfter = false, deployedAfter = false;
        string stageError = "", deployError = "";
        var stager = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                DriverBuilder.EnsureExtracted();
                stagedAfter = Volatile.Read(ref letGo) == 1;
                stagedMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex) { stageError = ex.Message; }
        }) { IsBackground = true };
        var deployer = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var deploy = DriverDeploy.Acquire();
                try { ProtectedStaging.DirFor(ProtectedStaging.OperationPackage); }
                finally { deploy.ReleaseMutex(); deploy.Dispose(); }
                deployedAfter = Volatile.Read(ref letGo) == 1;
                deployedMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex) { deployError = ex.Message; }
        }) { IsBackground = true };
        stager.Start();
        deployer.Start();
        bool done = holder.Join(Probe.HangMs()) & stager.Join(Probe.HangMs()) & deployer.Join(Probe.HangMs());
        Probe.Check("a staging waits for the staging lock's holder, then finishes", stagedMs >= 0 && stagedAfter,
            $"{stagedMs} ms, {(stagedAfter ? "after" : "before")} the holder let go {stageError}");
        Probe.Check("a deploy that stages waits for the deploy lock's holder, then finishes", deployedMs >= 0 && deployedAfter,
            $"{deployedMs} ms, {(deployedAfter ? "after" : "before")} the holder let go {deployError}");
        if (!done)
        {
            // The threads hold machine-wide locks between them. Ending the
            // process is the only way to give them back.
            Probe.Finish("S81");
            Environment.Exit(1);
        }
    }

    // ── A killed owner ──────────────────────────────────────────────────

    static void Crash(HMContext ctx, HMProfile gc, bool reclaimFirst, bool bindArrival = false)
    {
        Console.WriteLine(reclaimFirst
            ? "-- a program killed while bound, then a reclaim before the next arrival"
            : bindArrival
                ? "-- a program killed while bound, and an arrival before any reclaim that binds"
                : "-- a program killed while bound, and an arrival before any reclaim");
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "child")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string? line = null;
        var reader = new Thread(() => { try { line = child.StandardOutput.ReadLine(); } catch { } }) { IsBackground = true };
        reader.Start();
        reader.Join(Probe.HangMs(180_000));
        var m = line == null ? null : Regex.Match(line, "^bound (\\w+) (.+)$");
        Probe.Check("the child binds WinUSB", m is { Success: true } && m.Groups[1].Value == "Bound", line ?? "no answer");
        if (m is not { Success: true })
        {
            try { child.Kill(); } catch { }
            return;
        }
        string id = m.Groups[2].Value;
        child.Kill();
        child.WaitForExit(10_000);
        Probe.Check("the killed child's adapter leaves", Probe.WaitFor(() => !Devnodes.IsPresent(id), Probe.HangMs()));
        Probe.Check("the gone devnode still names WinUSB", Devnodes.Service(id, phantom: true) == "WINUSB",
            $"{Devnodes.Service(id, phantom: true)}");
        Probe.Check("the ledger still holds it for the next reclaim",
            RegistryLedger.HeldWinUsbDevnodes().Contains(id, StringComparer.OrdinalIgnoreCase));

        if (reclaimFirst)
        {
            // The next program's reclaim: the child's hold is dead, so the
            // gone devnode gets the reinstall flag.
            RegistryLedger.PruneDead();
            Probe.Check("the reclaim drops the dead hold", !RegistryLedger.HeldWinUsbDevnodes().Contains(id, StringComparer.OrdinalIgnoreCase));
            Probe.Check("and flags the gone devnode for reinstall", ((Devnodes.ConfigFlags(id) ?? 0) & CONFIGFLAG_REINSTALL) != 0,
                $"ConfigFlags 0x{Devnodes.ConfigFlags(id) ?? 0:X}");
        }

        // A stand-in for a real adapter: same serial, so the same instance,
        // and an identity of its own, so its create reclaims nothing. This
        // process swept once already, at its first create.
        using (var standIn = ctx.CreateController(gc, reclaimFirst ? "s81-after-reclaim" : bindArrival ? "s81-binds" : "s81-before-reclaim"))
        {
            string? arrived = Arrived(standIn);
            Probe.Check("the stand-in arrives on the same instance", string.Equals(arrived, id, StringComparison.OrdinalIgnoreCase), arrived ?? "");
            if (reclaimFirst)
            {
                Probe.Check("it gets input.inf: the flag reinstalled it by rank",
                    Probe.WaitFor(() => Devnodes.Service(id) == "HidUsb" && Devnodes.Status(id) is { Problem: 10 }, Probe.HangMs(InstallBoundMs)),
                    $"{Devnodes.Service(id)}");
                Probe.Check("and the flag is spent", ((Devnodes.ConfigFlags(id) ?? 0) & CONFIGFLAG_REINSTALL) == 0);
            }
            else if (bindArrival)
            {
                Probe.Check("before any reclaim it finds WinUSB", Probe.WaitFor(() => Devnodes.Service(id) == "WINUSB", Probe.HangMs(InstallBoundMs)),
                    $"{Devnodes.Service(id)}");
                var again = standIn.BindWinUsb();
                Probe.Check("BindWinUsb finds it already bound", again == HMWinUsbBinding.AlreadyBound, $"{again}");
                Probe.Check("and the ledger holds it for this controller",
                    RegistryLedger.HeldWinUsbDevnodes().Contains(id, StringComparer.OrdinalIgnoreCase));
                RegistryLedger.PruneDead();
                Thread.Sleep(SweepSettleMs);
                Probe.Check("a sweep drops the dead holder and leaves it on WinUSB",
                    Devnodes.Service(id) == "WINUSB" && Devnodes.Status(id) is { Problem: 0 }, $"{Devnodes.Service(id)}");
            }
            else
            {
                Probe.Check("before any reclaim it finds WinUSB, as an instance Zadig bound would",
                    Probe.WaitFor(() => Devnodes.Service(id) == "WINUSB", Probe.HangMs(InstallBoundMs)), $"{Devnodes.Service(id)}");
                // A restore never installs a driver on a present devnode: the
                // stand-in keeps WinUSB, and the entry stays until it leaves.
                RegistryLedger.PruneDead();
                Thread.Sleep(SweepSettleMs);
                Probe.Check("the reclaim leaves the present stand-in on WinUSB, with no reinstall flag",
                    Devnodes.Service(id) == "WINUSB" && ((Devnodes.ConfigFlags(id) ?? 0) & CONFIGFLAG_REINSTALL) == 0,
                    $"{Devnodes.Service(id)}, ConfigFlags 0x{Devnodes.ConfigFlags(id) ?? 0:X}");
                Probe.Check("and keeps the entry", HasWinUsbEntry(id));
            }
        }
        if (!reclaimFirst)
        {
            // Before any reclaim, the reclaim found the stand-in present and
            // started a follow-up, which flags the devnode once it is gone
            // and which the sweep here leaves the entry to. When the arrival
            // bound, its dispose flags it, or the follow-up the dispose
            // started does when the devnode took longer than 2 s to leave.
            Probe.WaitFor(() => !Devnodes.IsPresent(id), Probe.HangMs());
            RegistryLedger.PruneDead();
            FlaggedOnceGone(id, "once it is gone, the follow-up or a sweep flags it for reinstall, and the entry goes");
        }
        Probe.Check("no hold left", RegistryLedger.HeldWinUsbDevnodes().Count == 0, string.Join(", ", RegistryLedger.HeldWinUsbDevnodes()));
        // The child's controller record and its import: the orphan-safe
        // cleanup takes what a dead program left.
        HMContext.RemoveAllVirtualControllers(preserveInstall: true);
    }

    static int Child()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var c = ctx.CreateController(ctx.GetProfile("gamecube-adapter")!);
        string? id = Arrived(c);
        var bound = c.BindWinUsb();
        Console.WriteLine($"bound {bound} {id}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>Check that a gone devnode gets the reinstall flag and loses
    /// its entry. A restore that found it present started a follow-up,
    /// which polls every 250 ms and flags it once it has left, and a sweep
    /// skips an entry a follow-up of any program watches
    /// (RegistryLedger.RestoreWinUsbEntries). So the flag can land after the
    /// sweep returns, and the check waits for it, bounded by a hang bound: the
    /// poll, the devnode lock and a pnputil listing of the driver store. It prints
    /// what the sweep's return found and how long the flag took.</summary>
    static void FlaggedOnceGone(string id, string label)
    {
        uint atSweep = Devnodes.ConfigFlags(id) ?? 0;
        var sw = Stopwatch.StartNew();
        bool done = Probe.WaitFor(() => ((Devnodes.ConfigFlags(id) ?? 0) & CONFIGFLAG_REINSTALL) != 0 && !HasWinUsbEntry(id), Probe.HangMs());
        Probe.Check(label, done,
            $"ConfigFlags 0x{Devnodes.ConfigFlags(id) ?? 0:X} (0x{atSweep:X} as the sweep returned), entry {(HasWinUsbEntry(id) ? "kept" : "gone")}, {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Whether the ledger has a WinUSB entry for the devnode, held
    /// or not.</summary>
    static bool HasWinUsbEntry(string instanceId)
    {
        using var kind = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroRegistryLedger\WinUsb");
        if (kind == null) return false;
        foreach (var n in kind.GetSubKeyNames())
            using (var e = kind.OpenSubKey(n))
                if (string.Equals(e?.GetValue("InstanceId") as string, instanceId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The adapter devnode of a USB/IP controller, by its vhci
    /// port, once it is present.</summary>
    static string? Arrived(HMController c, string? except = null)
    {
        string? id = null;
        int port = c.UsbipHandle!.VhciPort;
        Probe.WaitFor(() => (id = Devnodes.AdapterOnVhciPort(port)) != null
                            && !string.Equals(id, except, StringComparison.OrdinalIgnoreCase), Probe.HangMs());
        return id;
    }

    readonly record struct DriverRank(string Original, uint Rank, string Status);

    /// <summary>The devnode's matching drivers as pnputil lists them.</summary>
    static Dictionary<string, DriverRank> DriverRanks(string id)
    {
        var psi = new ProcessStartInfo("pnputil.exe", $"/enum-devices /instanceid \"{id}\" /drivers")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var result = new Dictionary<string, DriverRank>(StringComparer.OrdinalIgnoreCase);
        int start = text.IndexOf("Matching Drivers:", StringComparison.Ordinal);
        if (start < 0) return result;
        foreach (var block in Regex.Split(text[start..], @"(?=^\s*Driver Name:)", RegexOptions.Multiline))
        {
            string Field(string name) => Regex.Match(block, $@"^\s*{name}:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
            string name = Field("Driver Name");
            if (name.Length == 0) continue;
            string original = Field("Original Name");
            uint.TryParse(Field("Driver Rank"), System.Globalization.NumberStyles.HexNumber, null, out uint rank);
            result[name] = new DriverRank(original.Length > 0 ? original : name, rank, Field("Driver Status"));
        }
        return result;
    }

    static string Describe(Dictionary<string, DriverRank> ranks)
        => string.Join("; ", ranks.Select(kv => $"{kv.Key} ({kv.Value.Original}) 0x{kv.Value.Rank:X8} {kv.Value.Status}"));
}
