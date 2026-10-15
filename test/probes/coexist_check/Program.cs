// Two or more programs that embed HIDMaestro on one machine.
//
//   A. Two programs create a controller with no identity key. Each gets
//      its own transport index and identity, and in one capture window
//      each device carries only its own program's input.
//   B. A second program asks for an identity key a live program holds.
//      Its create is refused with HMResourceInUseException naming the
//      owner, and the first program's devices never change.
//   C. A second program runs InstallDriver and a create while the first
//      streams. The first program's devnodes, XInput slot, WGI entry and
//      Raw Input entry stay as they were, and its frame gaps stay within
//      its own one-program 99th percentile.
//   D. A program killed with TerminateProcess. The next sweep of a live
//      program, or the start of a new one, removes the killed program's
//      devices and releases its registry ledger holds, and leaves the live
//      program's controller running.
//   E. Mixed SDK versions. A v1.12.0 program and a v1.13.0 program side by
//      side keep each other's controllers and the installed driver. Then
//      each install policy branch: upgrade when nobody uses the older
//      driver, a v1.12.0 program on a machine with a newer driver, a
//      v1.11.0 program whose driver the new SDK cannot run on, and a
//      driver newer than the SDK, which is never replaced.
//   F. Two programs call InstallDriver at the same moment on a machine
//      without the driver. One deploys, the other finds it current, and
//      both succeed.
//   G. Two composite controllers on USB/IP. One program's sweep leaves
//      the other's import attached. Killing one lets the other remove it.
//   H. A program killed while a child process it started still runs. Its
//      ownership claim goes with it, and another program can take the
//      same identity at once.
//   I. Staging. The driver payload and hmswd.exe stage under
//      %ProgramData%\HIDMaestro\Staging, every directory on the path is
//      Administrators/SYSTEM-write-only, and nothing is left in %TEMP%. A
//      staging root a non-administrator could write is repaired before use.
//      A staged tool overwritten with different bytes of the same length
//      is restored from the embedded copy on the next use. A deploy prunes
//      the staging of versions no running program uses, a staging
//      directory replaced by a link is moved aside rather than followed,
//      and a temporary file a process left when it ended mid-write is
//      removed. A running program's staged tool library or manifest,
//      removed or changed, is restored before its next deploy. A deploy's
//      prune never removes the staging another build's program reaches
//      while the prune runs. A %ProgramData%\HIDMaestro an upgrade finds,
//      owned by Administrators with an OpenVR file held open, is reset
//      where it is, one a program that is not elevated made is moved aside,
//      or refused when it cannot move, and the OpenVR driver's files come
//      back at their registered path. While hmswd.exe is held for its run,
//      no process can write or rename it, or rename its directory, the
//      build's above it or the root.
//      SectionIDeploy.cs has the detail. Battery S72 runs this section.
//   J. A record a killed program left names a transport index and an
//      identity that a running program holds now: a v1.12.0 program, and a
//      program on this SDK whose own record names them too. A sweep leaves
//      the running program's controller alone.
//   K. A keyless program killed and started again, creating without
//      InstallDriver: it gets the killed program's transport index and
//      identity back.
//   L. A program that only creates composites, killed: the next one's first
//      create removes its record.
//   M. A v1.11.0 program's keyed Xbox 360 controller, left when the program
//      was killed. A create of the same key on another transport index takes
//      its XUSB companion over, restarts it on the new index, and XInput
//      reads the new program's input.
//   N. A running program's controller whose devices are not present yet
//      holds an older driver: InstallDriver keeps it, and upgrades it once
//      that program is gone.
//   O. Another running program, with no controller, keeps the driver
//      packages through RemoveAllVirtualControllers(). Once it is gone they
//      go. The public DriverBuilder.RemoveOldDriverPackages() follows the
//      same rule.
//   P. A program's create after another program removed the driver
//      deploys it again and succeeds.
//   Q. A create waits for a deploy that holds the deploy lock, and so does
//      the public DriverBuilder.SignDrivers(). GenerateCatalogs() catalogs
//      what SignDrivers() signed, and InstallDrivers() returns false when it
//      installed nothing.
//   R. A program that has only staged names its staging in its record, and
//      a deploy keeps the staging a running program's record names.
//   S. Two deploys in one program, with another program's uninstall
//      between them: both succeed, and the driver staging still holds the
//      embedded binaries after a deploy, since each deploy signs its own copy.
//   T. A set a deploy left incomplete, this build's XUSB package gone and
//      its main package installed: InstallDriver adds the missing package,
//      and an Xbox 360 controller, which needs it, creates. With the record
//      of the install gone, InstallDriver finds this build's packages at
//      their own stamps. An older driver missing its XUSB package is
//      upgraded by a create that never called InstallDriver. A running
//      v1.12.0 program's XUSB companion stays on its package when this
//      build's is added. The tool runner returns a tool's output to its
//      last line, and gives up on output a child of the tool keeps open.
//   U. A create that fails after it claimed an index and an identity
//      gives both back. SectionU.cs has the detail.
//   V. USB/IP re-attach attempts and dropped imports of composites: a
//      killed program's port taken over, sweeps beside live programs, and
//      a running program's own composite losing its connection.
//      SectionV.cs has the detail.
//   W. Sweeps, owner records and the exit handler against programs
//      running beside them: a record deleted while another program reads
//      the records, a device no record names whose claims a running
//      program holds, a sweep whose create of another program lands
//      between its census and its removal, a v1.12.0 program's later
//      creates, a lease nothing references, and an exit while a create is
//      in flight. SectionW.cs has the detail.
//   X. Machine-wide names a program that is not elevated creates first:
//      the deploy lock, the registry ledger's lock, the claim pipes, and a
//      transport index's events and sections. Each is created by a thread
//      of this probe that impersonates such a program, and HIDMaestro's
//      deploy, sweep and create neither hang nor use it. SectionX.cs.
//      "--x-parts=<digits>" after the section letters runs only those
//      parts. Part 8, how long v1.12.0 holds its ledger mutex, runs only
//      when named there, never in S71, and only inside lane T's Windows
//      Sandbox, which sets HIDMAESTRO_X8_SANDBOX=1: it churns devices with
//      v1.12.0 code. Named anywhere else, the run stops before its first
//      step.
//   Y. The driver's StopEvent and OutputEvent admit no program that is
//      not elevated, and an owner pipe such a program makes for a killed
//      program's index keeps none of its devices. SectionY.cs has the
//      detail.
//   Z. pnputil from System32 and a runner that returns at its timeout,
//      transport work under a lock of its own with its progress raised
//      outside it, a timed-out repair that keeps the lock, the legacy check
//      without the lock, the wait for a running usbip-win2 installer, the
//      registry ledger's lock free during a WinUSB restore, the WinUSB
//      package under the deploy lock, a binding that follows a re-import, a
//      claim during a sweep, the test certificate under the deploy lock, a
//      bind whose devnode leaves, and a dispose during a bind.
//      SectionZ.cs has the detail.
//   S71 runs A to H and J to Z.
//
// Each program is a peer process (this binary with --peer, or the same
// source built against an older release under legacy\). Requires
// elevation, except "offline", which runs the checks in Offline.cs. Exit 0 PASS / 1 FAIL / 2 when another program that embeds
// HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Windows.Gaming.Input;

internal static partial class Program
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
        if (IsPlanted()) return Planted(args);
        if (args.Length > 0 && args[0] == "offline") return Offline();
        if (args.Length > 0 && args[0] == "--peer")
        {
            Peer.Extra = PeerZ.Run;
            return Peer.Run();
        }
        // Every process the probe starts has no window, and a loader failure
        // in one returns an exit code, never a dialog. Set before any mode
        // that starts one: phantomgate starts children.
        Native.SetErrorMode(Native.SEM_FAILCRITICALERRORS | Native.SEM_NOGPFAULTERRORBOX | Native.SEM_NOOPENFILEERRORBOX);
        if (args.Length > 0 && args[0] == "phantomclaims") return PhantomClaims();
        if (args.Length > 0 && args[0] == "phantomgate") return PhantomGate();
        if (args.Length > 2 && args[0] == "phantomgate-child") return PhantomGateChild(args[1], args[2]);
        if (args.Length > 1 && args[0] == "firstcall" && !args.Contains("phantoms"))
            return FirstCallTimes(RunCount(args), makePhantoms: false, args.Contains("inexit"));
        if (args.Length > 1 && args[0] == "firstcall-child") return FirstCallChild(args[1], args.Contains("inexit"));
        // Its SDK loads only where the child asks for it.
        if (args.Length > 1 && args[0] == "exitcut-child") return ExitCutChild(args[1] == "early");

        Console.WriteLine("=== Programs that embed HIDMaestro, side by side ===");
        string only = args.Length > 0 ? args[0].ToUpperInvariant() : "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        // X8 churns devices with v1.12.0 code and runs only inside lane T's
        // sandbox. Named anywhere else, the whole run stops here, before
        // any cleanup, deploy or section touches the machine.
        if (X8Named(only) && !X8InSandbox(out string markers))
        {
            Check("X8: runs only inside lane T's Windows Sandbox", false, $"{markers}. Nothing was run.");
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
            return 1;
        }
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }

        if (args.Length > 1 && args[0] == "removaltimes")
        {
            Clean();
            int rc = RemovalTimes(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture),
                args.Length > 2 ? args[2] : "xbox",
                args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 4);
            Clean();
            return rc;
        }
        if (args.Length > 2 && args[0] == "firstcall" && args.Contains("phantoms"))
        {
            Clean();
            int rc = FirstCallTimes(RunCount(args), makePhantoms: true, args.Contains("inexit"));
            Clean();
            return rc;
        }
        if (args.Length > 0 && args[0] == "exitcut")
        {
            Clean();
            int rc = ExitCut();
            Clean();
            return rc;
        }
        if (args.Length > 1 && args[0] == "phantomtimes")
        {
            Clean();
            int rc = PhantomTimes(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
            Clean();
            return rc;
        }
        Clean();
        var sections = new (char Id, string Title, Action Body)[]
        {
            ('A', "two programs with no identity key", SectionA),
            ('B', "one identity key in two programs", SectionB),
            ('C', "a second program starts while the first streams", SectionC),
            ('D', "a killed program's devices", SectionD),
            ('E', "mixed SDK versions", SectionE),
            ('F', "InstallDriver at the same moment in two programs", SectionF),
            ('G', "two composite controllers", SectionG),
            ('H', "a program killed while its child runs", SectionH),
            ('I', "staging under a protected directory", SectionI),
            ('J', "a killed program's record and a running program's controller", SectionJ),
            ('K', "a keyless program started again after a crash", SectionK),
            ('L', "a killed program that only created composites", SectionL),
            ('M', "a companion taken over on another transport index", SectionM),
            ('N', "an older driver and a create in flight", SectionN),
            ('O', "removing the packages while another program runs", SectionO),
            ('P', "a create after another program removed the driver", SectionP),
            ('Q', "a create during another program's deploy", SectionQ),
            ('R', "staging a running program uses", SectionR),
            ('S', "two deploys in one program", SectionS),
            ('T', "a driver set a deploy left incomplete", SectionT),
            ('U', "a create that fails after its claims", SectionU),
            ('V', "re-attach attempts a killed composite program leaves", SectionV),
            ('W', "sweeps, owner records and the exit handler", SectionW),
            ('X', "machine-wide names a program that is not elevated creates first", SectionX),
            ('Y', "the driver's events and a faked v1.12.0 owner pipe", SectionY),
            ('Z', "the transport's install and repair, the machine-wide locks and the WinUSB binding", SectionZ),
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

    // ── A ──────────────────────────────────────────────────────────────

    static void SectionA()
    {
        using var p1 = PeerProcess.Start("A1");
        using var p2 = PeerProcess.Start("A2");
        string i1 = p1.Send("install");
        string c1 = p1.Send("create pad F0D0 -");
        p1.Send("stream 0 0.25 125 0");
        string i2 = p2.Send("install");
        string c2 = p2.Send("create pad F0D1 -");
        p2.Send("stream 1 0.75 125 0");
        Check("A: both programs install and create", Ok(i1) && Ok(c1) && Ok(i2) && Ok(c2));
        Check("A: the two controllers have different transport indices",
              Ok(c1) && Ok(c2) && PeerProcess.Field(c1, "transport") != PeerProcess.Field(c2, "transport"),
              $"{PeerProcess.Field(c1, "transport")} and {PeerProcess.Field(c2, "transport")}");
        Check("A: the two controllers have different identities and devnodes",
              Ok(c1) && Ok(c2) && PeerProcess.Field(c1, "key") != PeerProcess.Field(c2, "key")
              && PeerProcess.Field(c1, "instance") != PeerProcess.Field(c2, "instance"),
              $"{PeerProcess.Field(c1, "key")} {PeerProcess.Field(c1, "instance")} and {PeerProcess.Field(c2, "key")} {PeerProcess.Field(c2, "instance")}");

        var path1 = WaitPaths(0xBEEF, 0xF0D0, 1, 10_000);
        var path2 = WaitPaths(0xBEEF, 0xF0D1, 1, 10_000);
        Check("A: both devices are present", path1.Count == 1 && path2.Count == 1, $"{path1.Count} and {path2.Count} interfaces");
        if (path1.Count != 1 || path2.Count != 1) return;
        using var cap1 = HidCapture.Open(path1[0]);
        using var cap2 = HidCapture.Open(path2[0]);
        if (cap1 == null || cap2 == null) { Check("A: both devices open", false); return; }
        Thread.Sleep(300);
        long from = cap1.Mark();
        Thread.Sleep(1000);
        long to = cap1.Mark();
        var pat1 = Patterns(cap1, from, to);
        var pat2 = Patterns(cap2, from, to);
        Check("A: device 1 carries one pattern in the window", pat1.Count == 1, Show(pat1));
        Check("A: device 2 carries one pattern in the window", pat2.Count == 1, Show(pat2));
        if (pat1.Count == 1 && pat2.Count == 1)
            Check("A: each device carries its own program's input",
                  pat1[0].Buttons != pat2[0].Buttons && pat1[0].Buttons != "none" && pat2[0].Buttons != "none" && pat1[0].X < pat2[0].X,
                  $"device 1 {Show(pat1)}, device 2 {Show(pat2)}");
    }

    // ── B ──────────────────────────────────────────────────────────────

    static void SectionB()
    {
        using var p1 = PeerProcess.Start("B1");
        string c1 = p1.Send("install") is var i1 && Ok(i1) ? p1.Send("create pad F0D2 coexist-same") : i1;
        if (!Ok(c1)) { Check("B: the first program creates", false, c1); return; }
        p1.Send("stream 2 0.5 125 0");
        string inst1 = PeerProcess.Field(c1, "instance")!;
        var path = WaitPaths(0xBEEF, 0xF0D2, 1, 10_000);
        if (path.Count != 1) { Check("B: the first program's device is present", false, $"{path.Count} interfaces"); return; }
        var tree = Native.Subtree(inst1);
        using var cap = HidCapture.Open(path[0]);
        if (cap == null) { Check("B: the first program's device opens", false); return; }

        using var p2 = PeerProcess.Start("B2");
        string i2 = p2.Send("install");
        string c2 = p2.Send("create pad F0D2 coexist-same");
        Check("B: the second program installs", Ok(i2), i2);
        Check("B: the second program's create is refused with HMResourceInUseException, an InvalidOperationException",
              c2.StartsWith("ERR create", StringComparison.Ordinal) && c2.Contains("HMResourceInUseException") && c2.Contains("InvalidOperationException"), c2);
        Check("B: the refusal names the owning process", c2.Contains($"{p1.Pid}"), c2);
        Thread.Sleep(500);
        var tree2 = Native.Subtree(inst1);
        Check("B: the first program's devnodes are unchanged and started",
              tree2.SequenceEqual(tree, StringComparer.OrdinalIgnoreCase) && tree.All(Native.IsStarted),
              $"before {tree.Count} nodes, after {tree2.Count}");
        Check("B: the first program's open handle never failed", !cap.Broken, cap.Broken ? $"read error {cap.ReadError}" : "");
        Check("B: the first program's input still arrives", Flows(cap));
    }

    // ── C ──────────────────────────────────────────────────────────────

    static void SectionC()
    {
        var wgiBefore = RawGameController.RawGameControllers.Select(r => r.NonRoamableId).ToHashSet();
        using var p1 = PeerProcess.Start("C1");
        p1.Send("install");
        string cx = p1.Send("create xbox - coexist-c-xbox");
        string cp = p1.Send("create pad F0D3 coexist-c-pad");
        if (!Ok(cx) || !Ok(cp)) { Check("C: the first program creates an Xbox 360 pad and a HID pad", false, $"{cx} / {cp}"); return; }
        p1.Send("stream 3 0.95 250 1");
        string instX = PeerProcess.Field(cx, "instance")!, instP = PeerProcess.Field(cp, "instance")!;
        var padPath = WaitPaths(0xBEEF, 0xF0D3, 1, 10_000);
        int slot = -1;
        WaitFor(() =>
        {
            for (uint s = 0; s < 4; s++)
                if (Native.XInputGetState(s, out var st) == 0 && st.Gamepad.sThumbLX > 25000) { slot = (int)s; return true; }
            return false;
        }, 10_000);
        string? wgiId = null;
        WaitFor(() =>
        {
            wgiId = RawGameController.RawGameControllers
                .Where(r => r.HardwareVendorId == 0x045E && r.HardwareProductId == 0x028E && !wgiBefore.Contains(r.NonRoamableId))
                .Select(r => r.NonRoamableId).FirstOrDefault();
            return wgiId != null;
        }, 10_000);
        if (padPath.Count != 1 || slot < 0 || wgiId == null)
        {
            Check("C: the first program's pads appear on HID, XInput and WGI", false, $"hid {padPath.Count}, xinput slot {slot}, wgi {wgiId ?? "none"}");
            return;
        }
        var raw = Native.RawInputDevices().Where(d => string.Equals(d.Name, padPath[0], StringComparison.OrdinalIgnoreCase)).ToList();
        var treeX = Native.Subtree(instX);
        var treeP = Native.Subtree(instP);
        using var cap = HidCapture.Open(padPath[0]);
        if (cap == null) { Check("C: the HID pad opens", false); return; }

        // Watch the XInput slot and WGI for the whole run.
        int xinputMisses = 0;
        var wgiRemoved = new List<string>();
        void OnRemoved(object? s, RawGameController r) { lock (wgiRemoved) wgiRemoved.Add(r.NonRoamableId); }
        RawGameController.RawGameControllerRemoved += OnRemoved;
        bool watching = true;
        var watch = new Thread(() =>
        {
            while (watching)
            {
                if (Native.XInputGetState((uint)slot, out var st) != 0 || st.Gamepad.sThumbLX < 25000) Interlocked.Increment(ref xinputMisses);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        watch.Start();
        try
        {
            Thread.Sleep(500);
            long a1 = cap.Mark();
            Thread.Sleep(5000);
            long a1End = cap.Mark();

            using var p2 = PeerProcess.Start("C2");
            long during = cap.Mark();
            string i2 = p2.Send("install");
            string c2 = p2.Send("create pad F0D4 coexist-c-second");
            Thread.Sleep(500);
            long duringEnd = cap.Mark();
            Check("C: the second program installs and creates", Ok(i2) && Ok(c2), $"{i2} / {c2}");

            long a2 = cap.Mark();
            Thread.Sleep(5000);
            long a2End = cap.Mark();

            double p99a1 = HidCapture.Percentile(HidCapture.Gaps(cap.Since(a1, a1End)), 0.99);
            double p99d = HidCapture.Percentile(HidCapture.Gaps(cap.Since(during, duringEnd)), 0.99);
            double p99a2 = HidCapture.Percentile(HidCapture.Gaps(cap.Since(a2, a2End)), 0.99);
            double noise = Math.Max(0.5, Math.Abs(p99a1 - p99a2));
            Check("C: the first program's frame gaps stay within its own one-program p99 while the second starts",
                  p99d <= Math.Max(p99a1, p99a2) + noise,
                  FormattableString.Invariant($"p99 alone {p99a1:F2} ms, during {p99d:F2} ms, alone after {p99a2:F2} ms, allowance {noise:F2} ms"));

            var treeX2 = Native.Subtree(instX);
            var treeP2 = Native.Subtree(instP);
            Check("C: the first program's devnodes are unchanged and started",
                  treeX2.SequenceEqual(treeX, StringComparer.OrdinalIgnoreCase) && treeP2.SequenceEqual(treeP, StringComparer.OrdinalIgnoreCase)
                  && treeX.Concat(treeP).All(Native.IsStarted));
            Check("C: the first program's HID handle never failed", !cap.Broken, cap.Broken ? $"read error {cap.ReadError}" : "");
            Check("C: the first program's XInput slot never dropped", Volatile.Read(ref xinputMisses) == 0, $"{xinputMisses} misses at slot {slot}");
            bool wgiStill = RawGameController.RawGameControllers.Any(r => r.NonRoamableId == wgiId);
            lock (wgiRemoved)
                Check("C: the first program's WGI entry stayed", wgiStill && !wgiRemoved.Contains(wgiId!), wgiRemoved.Count > 0 ? $"removed: {string.Join(", ", wgiRemoved)}" : "");
            var raw2 = Native.RawInputDevices().Where(d => string.Equals(d.Name, padPath[0], StringComparison.OrdinalIgnoreCase)).ToList();
            Check("C: the first program's Raw Input entry kept its handle",
                  raw.Count == 1 && raw2.Count == 1 && raw[0].Handle == raw2[0].Handle,
                  $"before {raw.Count}, after {raw2.Count}");
        }
        finally
        {
            watching = false;
            watch.Join(500);
            RawGameController.RawGameControllerRemoved -= OnRemoved;
        }
    }

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

    // ── E ──────────────────────────────────────────────────────────────

    static void SectionE()
    {
        foreach (var v in new[] { "v1.12.0", "v1.11.0" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", v, "CoexistPeer.exe")))
            {
                Check($"E: the {v} peer is built", false, "build CoexistCheck.csproj, which fetches the release and builds legacy\\" + v);
                return;
            }

        // E1. v1.12.0 first, then the new SDK beside it.
        Uninstall();
        string verOld, instOld;
        using (var old = PeerProcess.Start("E1-v1.12.0", "v1.12.0"))
        {
            old.Send("install");
            string co = old.Send("create pad F0E0 coexist-e-old");
            if (!Ok(co)) { Check("E1: the v1.12.0 program creates", false, co); return; }
            old.Send("stream 5 0.2 125 0");
            instOld = PeerProcess.Field(co, "instance")!;
            verOld = Native.DriverVersionOf(instOld) ?? "?";
            var pathOld = WaitPaths(0xBEEF, 0xF0E0, 1, 10_000);
            using var capOld = pathOld.Count == 1 ? HidCapture.Open(pathOld[0]) : null;
            if (capOld == null) { Check("E1: the v1.12.0 program's device opens", false); return; }
            var treeOld = Native.Subtree(instOld);

            using var nu = PeerProcess.Start("E1-new");
            string ri = nu.Send("install");
            Check("E1: beside a live v1.12.0 program the new SDK keeps the installed driver",
                  PeerProcess.Field(ri, "outcome") == "KeptInUse", ri);
            string cn = nu.Send("create pad F0E1 coexist-e-new");
            Check("E1: the new program creates", Ok(cn), cn);
            if (Ok(cn))
            {
                string instNew = PeerProcess.Field(cn, "instance")!;
                Check("E1: the new program's controller runs on the installed v1.12.0 driver",
                      Native.DriverVersionOf(instNew) == verOld, $"{Native.DriverVersionOf(instNew)} vs {verOld}");
                string dv = nu.Send("driverversion");
                Check("E1: the v1.12.0 driver reports no version through the handshake",
                      dv == "OK driverversion none", dv);
                nu.Send("stream 6 0.8 125 0");
                var pathNew = WaitPaths(0xBEEF, 0xF0E1, 1, 10_000);
                using var capNew = pathNew.Count == 1 ? HidCapture.Open(pathNew[0]) : null;
                Check("E1: the new program's input arrives", capNew != null && Flows(capNew));
            }
            nu.Send("sweep");
            Check("E1: the v1.12.0 program's controller is untouched, driver and all",
                  !capOld.Broken && Native.Subtree(instOld).SequenceEqual(treeOld, StringComparer.OrdinalIgnoreCase)
                  && Native.DriverVersionOf(instOld) == verOld && Flows(capOld),
                  capOld.Broken ? $"read error {capOld.ReadError}" : $"driver {Native.DriverVersionOf(instOld)}");
        }

        // E2. The older driver, nobody on it: the new SDK upgrades.
        using (var nu = PeerProcess.Start("E2-new"))
        {
            string ri = nu.Send("install");
            Check("E2: with nobody on the v1.12.0 driver the new SDK upgrades it", PeerProcess.Field(ri, "outcome") == "Upgraded", ri);
            string cn = nu.Send("create pad F0E2 coexist-e-upgraded");
            string? ver = Ok(cn) ? Native.DriverVersionOf(PeerProcess.Field(cn, "instance")!) : null;
            Check("E2: the new controller runs on the new driver", ver != null && ver != verOld, $"{ver} (was {verOld})");
            if (Ok(cn) && ver != null)
            {
                // The driver's binary and its INF come from one build, so
                // the handshake's version is the INF's without the build
                // number.
                string release = string.Join('.', ver.Split('.').Take(3));
                string dv = nu.Send("driverversion");
                Check("E2: the new driver reports its version through the handshake",
                      dv == $"OK driverversion {release}", $"{dv}, the devnode says {ver}");
                var vs = Native.VersionSection(int.Parse(PeerProcess.Field(cn, "transport")!));
                Check("E2: the handshake section carries layout 1, the SDK's protocols 2 to 3, its version and the driver's protocol 3",
                      vs != null && vs.Value.Layout == 1 && vs.Value.SdkMin == 2 && vs.Value.SdkMax == 3
                      && vs.Value.DriverProtocol == 3 && vs.Value.Driver == release
                      && nu.SdkVersion.StartsWith(vs.Value.Sdk + ".", StringComparison.Ordinal),
                      vs == null ? "no section" : $"{vs}, the peer's SDK is {nu.SdkVersion}");
            }
        }

        // E3. A v1.12.0 program on a machine with the newer driver.
        using (var old = PeerProcess.Start("E3-v1.12.0", "v1.12.0"))
        {
            string ri = old.Send("install");
            string co = old.Send("create pad F0E3 coexist-e-old-2");
            old.Send("stream 5 0.2 125 0");
            var path = WaitPaths(0xBEEF, 0xF0E3, 1, 10_000);
            using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
            Check("E3: a v1.12.0 program runs on a machine where a newer driver was installed",
                  Ok(ri) && Ok(co) && cap != null && Flows(cap), $"{ri} / {co}");
        }

        // E4. A live v1.11.0 program on its own driver, which predates the
        // driver protocol this SDK needs.
        Uninstall();
        using (var v11 = PeerProcess.Start("E4-v1.11.0", "v1.11.0"))
        {
            v11.Send("install");
            string cv = v11.Send("create pad F0E4 coexist-e-v11");
            if (!Ok(cv)) { Check("E4: the v1.11.0 program creates", false, cv); return; }
            v11.Send("stream 5 0.2 125 0");
            string instV = PeerProcess.Field(cv, "instance")!;
            var path = WaitPaths(0xBEEF, 0xF0E4, 1, 10_000);
            using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
            if (cap == null) { Check("E4: the v1.11.0 program's device opens", false); return; }

            using var nu = PeerProcess.Start("E4-new");
            string ri = nu.Send("install");
            Check("E4: the new SDK refuses with HMDriverVersionException, an InvalidOperationException",
                  ri.StartsWith("ERR install", StringComparison.Ordinal) && ri.Contains("HMDriverVersionException") && ri.Contains("InvalidOperationException"), ri);
            Check("E4: the refusal names the device holding the old driver", ri.Contains(instV, StringComparison.OrdinalIgnoreCase), ri);
            Check("E4: the v1.11.0 program's controller is untouched", !cap.Broken && Native.IsStarted(instV) && Flows(cap));
        }
        using (var nu = PeerProcess.Start("E4-new-after"))
        {
            string ri = nu.Send("install");
            Check("E4: once the v1.11.0 program is gone the new SDK upgrades", PeerProcess.Field(ri, "outcome") == "Upgraded", ri);
        }

        // E5. A driver newer than the SDK is never replaced.
        Uninstall();
        string newer = InstallNewerDriver();
        using (var nu = PeerProcess.Start("E5-new"))
        {
            string ri = nu.Send("install");
            Check("E5: a newer installed driver is kept", PeerProcess.Field(ri, "outcome") == "KeptNewer", ri);
            string cn = nu.Send("create pad F0E5 coexist-e-newer");
            string? ver = Ok(cn) ? Native.DriverVersionOf(PeerProcess.Field(cn, "instance")!) : null;
            Check("E5: the controller runs on the newer driver", ver == newer, $"{ver} vs {newer}");
            nu.Send("stream 6 0.8 125 0");
            var path = WaitPaths(0xBEEF, 0xF0E5, 1, 10_000);
            using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
            Check("E5: its input arrives", cap != null && Flows(cap));
        }
        Uninstall();
    }

    static void Uninstall()
    {
        using var u = PeerProcess.Start("uninstall");
        u.Send("uninstall");
    }

    /// <summary>The SDK's own driver package with a DriverVer above this
    /// SDK's version, cataloged and signed the way the SDK does it, then
    /// installed. Returns the version Windows reports for it.</summary>
    static string InstallNewerDriver()
    {
        // A peer stages the payload, so this probe never becomes a program
        // that embeds HIDMaestro: later sections count the programs running.
        string staging;
        using (var stager = PeerProcess.Start("stage"))
            staging = StagingPath(stager.Send("stagingdir")) ?? throw new InvalidOperationException("the peer staged nothing");
        string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro_coexist_newer");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(staging)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        foreach (var f in Directory.GetFiles(dir, "*.cat")) File.Delete(f);
        const string version = "1.99.0.0";
        foreach (var inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
        {
            string p = Path.Combine(dir, inf);
            string text = File.ReadAllText(p);
            text = Regex.Replace(text, @"(?m)^(DriverVer\s*=\s*)\d{2}/\d{2}/\d{4}\s*,\s*[\d.]+", "${1}" + DateTime.Now.ToString("MM/dd/yyyy") + "," + version);
            File.WriteAllText(p, text);
        }
        Run(Path.Combine(dir, "signtool.exe"), $"sign /a /sm /s My /n HIDMaestroTestCert /fd SHA256 \"{Path.Combine(dir, "HIDMaestro.dll")}\" \"{Path.Combine(dir, "HMXInput.dll")}\"", dir);
        string os = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "10_RS3_ARM64" : "10_X64";
        Run(Path.Combine(dir, "Inf2Cat.exe"), $"/driver:\"{dir}\" /os:{os}", dir);
        foreach (var cat in Directory.GetFiles(dir, "*.cat"))
            Run(Path.Combine(dir, "signtool.exe"), $"sign /a /sm /s My /n HIDMaestroTestCert /fd SHA256 \"{cat}\"", dir);
        foreach (var inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
            Run("pnputil.exe", $"/add-driver \"{Path.Combine(dir, inf)}\" /install", dir);
        return version;
    }

    static void Run(string exe, string args, string dir)
    {
        var psi = new ProcessStartInfo(exe, args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = dir };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !exe.Equals("pnputil.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}: {o.Trim()}");
    }

    // ── F ──────────────────────────────────────────────────────────────

    static void SectionF()
    {
        Uninstall();
        using var p1 = PeerProcess.Start("F1");
        using var p2 = PeerProcess.Start("F2");
        p1.Post("install");
        p2.Post("install");
        string r1 = p1.Receive("install");
        string r2 = p2.Receive("install");
        Check("F: both programs' InstallDriver succeed", Ok(r1) && Ok(r2), $"{r1} / {r2}");
        var outcomes = new[] { PeerProcess.Field(r1, "outcome"), PeerProcess.Field(r2, "outcome") }.OrderBy(o => o).ToArray();
        Check("F: one deploys and the other finds it current",
              outcomes.SequenceEqual(new[] { "AlreadyCurrent", "Installed" }), string.Join(" and ", outcomes));
        string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        int main = Directory.GetDirectories(repo, "hidmaestro.inf_*").Length;
        int xusb = Directory.GetDirectories(repo, "hidmaestro_xusb.inf_*").Length;
        Check("F: the driver store holds one package of each INF", main == 1 && xusb == 1, $"{main} and {xusb}");
        string c1 = p1.Send("create pad F0D9 coexist-f");
        Check("F: a controller creates on the deployed driver", Ok(c1), c1);
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
            string transport;
            using (var p1 = PeerProcess.Start("H1"))
            {
                p1.Send("install");
                string c1 = p1.Send("create pad F0D8 coexist-h");
                if (!Ok(c1)) { Check("H: the parent program creates", false, c1); return; }
                transport = PeerProcess.Field(c1, "transport")!;
                string sc = p1.Send("spawnchild");
                if (int.TryParse(PeerProcess.Field(sc, "pid"), out int childPid)) child = Process.GetProcessById(childPid);
                string? ownerId = Native.OwnerRecords().Where(r => r.Value == p1.Pid).Select(r => r.Key).FirstOrDefault();
                Check("H: the program has an owner record", ownerId != null);
                if (ownerId != null)
                    Check("H: its ownership claim is held while it runs", Native.ClaimExists($"HIDMaestro.Owner.{ownerId}"));
                p1.Kill();
                Check("H: the child still runs", child != null && !child.HasExited);
                if (ownerId != null)
                    Check("H: the claim is gone with the parent though the child runs", !Native.ClaimExists($"HIDMaestro.Owner.{ownerId}"));
                Check("H: the v1.12.0 owner pipe is gone too", !Native.PipeExists($"HIDMaestroOwner{transport}"));
            }
            using var p2 = PeerProcess.Start("H2");
            p2.Send("install");
            string c2 = p2.Send("create pad F0D8 coexist-h");
            Check("H: another program takes the same identity at once", Ok(c2), c2);
            if (Ok(c2))
                Check("H: and the transport index the killed program had",
                      PeerProcess.Field(c2, "transport") == transport,
                      $"{PeerProcess.Field(c2, "transport")}, was {transport}");
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

    // ── J ──────────────────────────────────────────────────────────────

    /// <summary>A record whose program has exited names an index and an
    /// identity. A running program holds both now, so the device is the
    /// running program's.</summary>
    static void SectionJ()
    {
        const string deadId = "ffffffffffffffffffffffffffffffff";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("J: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        try
        {
            // J1. A v1.12.0 program on index 0 with the default identity.
            using (var old = PeerProcess.Start("J1-v1.12.0", "v1.12.0"))
            {
                string co = old.Send("create pad F0F1 -");
                if (!Ok(co) || PeerProcess.Field(co, "index") != "0") { Check("J1: the v1.12.0 program creates on index 0", false, co); return; }
                old.Send("stream 5 0.2 125 0");
                var path = WaitPaths(0xBEEF, 0xF0F1, 1, 10_000);
                using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
                if (cap == null) { Check("J1: its device opens", false); return; }
                Native.WriteDeadRecord(deadId, "HM_0000", 0);
                using (var nu = PeerProcess.Start("J1-new"))
                {
                    string s = nu.Send("sweep");
                    Thread.Sleep(1000);
                    Check("J1: a sweep leaves the v1.12.0 program's controller, though a killed program's record names its index and identity",
                          Ok(s) && !cap.Broken && Flows(cap) && WaitPaths(0xBEEF, 0xF0F1, 1, 1000).Count == 1,
                          cap.Broken ? $"read error {cap.ReadError}" : s);
                    Check("J1: and drops the killed program's record", !Native.OwnerRecords().ContainsKey(deadId));
                }
            }
            Native.DeleteRecord(deadId);

            // J2. A program on this SDK, whose record names the identity too.
            using (var a = PeerProcess.Start("J2-a"))
            {
                string ca = a.Send("create pad F0F2 -");
                if (!Ok(ca) || PeerProcess.Field(ca, "transport") != "0") { Check("J2: the program creates on index 0", false, ca); return; }
                a.Send("stream 6 0.8 125 0");
                var path = WaitPaths(0xBEEF, 0xF0F2, 1, 10_000);
                using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
                if (cap == null) { Check("J2: its device opens", false); return; }
                // Listed after every other record, where a record read last
                // decided the owner.
                Native.WriteDeadRecord(deadId, "HM_0000", 0);
                using (var b = PeerProcess.Start("J2-b"))
                {
                    string s = b.Send("sweep");
                    Thread.Sleep(1000);
                    Check("J2: a sweep leaves the running program's controller, though a killed program's record names it too",
                          Ok(s) && !cap.Broken && Flows(cap) && WaitPaths(0xBEEF, 0xF0F2, 1, 1000).Count == 1,
                          cap.Broken ? $"read error {cap.ReadError}" : s);
                }
            }
        }
        finally { Native.DeleteRecord(deadId); }
    }

    // ── K ──────────────────────────────────────────────────────────────

    /// <summary>A keyless program that is killed and started again, and
    /// creates without calling InstallDriver, as a program that installs
    /// once at setup does.</summary>
    static void SectionK()
    {
        string transport;
        using (var k1 = PeerProcess.Start("K1"))
        {
            string c1 = k1.Send("create pad F0F3 -");
            if (!Ok(c1)) { Check("K: the program creates", false, c1); return; }
            transport = PeerProcess.Field(c1, "transport")!;
            k1.Kill();
        }
        Check("K: the killed program's device is still there", WaitPaths(0xBEEF, 0xF0F3, 1, 2000).Count == 1);
        using var k2 = PeerProcess.Start("K2");
        string c2 = k2.Send("create pad F0F3 -");
        Check("K: started again, it creates", Ok(c2), c2);
        Check("K: on the killed program's transport index and identity",
              PeerProcess.Field(c2, "transport") == transport && PeerProcess.Field(c2, "key") == "index:" + transport,
              $"transport {PeerProcess.Field(c2, "transport")}, key {PeerProcess.Field(c2, "key")}, was {transport}");
        Check("K: and one device of that identity is present", WaitPaths(0xBEEF, 0xF0F3, 1, 5000).Count == 1);
    }

    // ── L ──────────────────────────────────────────────────────────────

    static void SectionL()
    {
        var before = Native.HidPaths(0x054C, 0x0CE6);
        string? ownerId;
        using (var l1 = PeerProcess.Start("L1"))
        {
            string c1 = l1.Send("create composite - coexist-l");
            var path = WaitPaths(0x054C, 0x0CE6, 1, 20_000, before);
            if (!Ok(c1) || path.Count != 1) { Check("L: the first program's composite appears", false, c1); return; }
            ownerId = Native.OwnerRecords().Where(r => r.Value == l1.Pid).Select(r => r.Key).FirstOrDefault();
            Check("L: it has an owner record", ownerId != null);
            l1.Kill();
        }
        using var l2 = PeerProcess.Start("L2");
        string c2 = l2.Send("create composite - coexist-l");
        Check("L: a second program that only creates composites creates the same identity", Ok(c2), c2);
        if (ownerId != null)
            Check("L: its first create removes the killed program's record", !Native.OwnerRecords().ContainsKey(ownerId));
        Check("L: one composite of that identity is present",
              WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 1, 15_000),
              $"{Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count()} present");
    }

    // ── M ──────────────────────────────────────────────────────────────

    /// <summary>A program before v1.12.0 leaves no sign of whether it still
    /// runs, so no sweep removes what a killed one left. A create that asks
    /// for the same identity key takes it over. With index 0 held, as a
    /// running program's claim holds it, the create lands on index 1, and
    /// the companion it takes over was started on index 0.</summary>
    static void SectionM()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.11.0", "CoexistPeer.exe")))
        {
            Check("M: the v1.11.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        using (var old = PeerProcess.Start("M1-v1.11.0", "v1.11.0"))
        {
            string co = old.Send("create xbox - coexist-m");
            if (!Ok(co)) { Check("M: the v1.11.0 program creates its Xbox 360 controller", false, co); return; }
            old.Kill();
        }
        string? companion = Native.InterfaceList(XusbGuid, null).Select(Native.InstanceOfInterface)
            .FirstOrDefault(id => id != null && id.StartsWith(@"SWD\HIDMAESTRO\", StringComparison.OrdinalIgnoreCase));
        Check("M: the killed program's companion is still started", companion != null && Native.IsStarted(companion), companion ?? "none");

        using (var claim = Native.HoldPipe("HIDMaestro.Index.0"))
        {
            Check("M: the probe holds index 0's claim", claim != null);
            using var nu = PeerProcess.Start("M2-new");
            string cn = nu.Send("create xbox - coexist-m");
            Check("M: a create of the same key lands on index 1", Ok(cn) && PeerProcess.Field(cn, "transport") == "1", cn);
            if (!Ok(cn)) return;
            nu.Send("stream 0 0.95 125 0");
            int slot = -1;
            bool reads = WaitFor(() =>
            {
                for (uint s = 0; s < 4; s++)
                    if (Native.XInputGetState(s, out var st) == 0 && st.Gamepad.sThumbLX > 25000 && (st.Gamepad.wButtons & 0x1000) != 0)
                    { slot = (int)s; return true; }
                return false;
            }, 10_000);
            Check("M: XInput reads the new program's input through the companion it took over", reads,
                  reads ? $"slot {slot}" : "no XInput slot carries it");
        }
    }

    static readonly Guid XusbGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    // ── N to R ─────────────────────────────────────────────────────────

    static string FileRepository =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");

    static int MainPackages() => Directory.GetDirectories(FileRepository, "hidmaestro.inf_*").Length;

    /// <summary>A running program's ownership claim and record, held by this
    /// probe: the claim pipe while the returned handle lives, and a record
    /// naming <paramref name="controllers"/>, each a token on an index.</summary>
    static Microsoft.Win32.SafeHandles.SafeFileHandle? LiveRecord(string ownerId, string? stagingTag,
        params (string Token, int Index)[] controllers)
    {
        var claim = Native.HoldPipe($"HIDMaestro.Owner.{ownerId}");
        Native.WriteRecord(ownerId, stagingTag, controllers);
        return claim;
    }

    static void SectionN()
    {
        const string ownerId = "0000000000000000000000000000000a";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("N: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        Uninstall();
        using (var old = PeerProcess.Start("N-v1.12.0", "v1.12.0"))
            Check("N: a v1.12.0 program installs its driver", Ok(old.Send("install")));
        try
        {
            using (LiveRecord(ownerId, null, ("HM_0042", 42)))
            {
                using var nu = PeerProcess.Start("N1-new");
                string ri = nu.Send("install");
                Check("N1: a running program's controller with no device yet keeps the older driver",
                      PeerProcess.Field(ri, "outcome") == "KeptInUse", ri);
            }
            using (var nu = PeerProcess.Start("N2-new"))
            {
                string ri = nu.Send("install");
                Check("N2: once that program is gone the driver is upgraded", PeerProcess.Field(ri, "outcome") == "Upgraded", ri);
            }
        }
        finally { Native.DeleteRecord(ownerId); }
    }

    static void SectionO()
    {
        const string ownerId = "0000000000000000000000000000000b";
        try
        {
            using (LiveRecord(ownerId, null))
            {
                using var p = PeerProcess.Start("O1");
                string r = p.Send("removeall");
                Check("O1: with another program running, RemoveAllVirtualControllers() keeps the driver packages",
                      Ok(r) && MainPackages() > 0 && Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro") != null,
                      $"{r}, {MainPackages()} package(s)");
            }
            using (var p = PeerProcess.Start("O2"))
            {
                string r = p.Send("removeall");
                Check("O2: with no other program, they go", Ok(r) && MainPackages() == 0, $"{r}, {MainPackages()} package(s)");
            }
            Clean();
            using (LiveRecord(ownerId, null))
            {
                using var p = PeerProcess.Start("O3");
                string r = p.Send("builderstep RemoveOldDriverPackages");
                Check("O3: with another program running, the public DriverBuilder.RemoveOldDriverPackages() keeps them and says why",
                      !Ok(r) && r.Contains("other running program", StringComparison.Ordinal) && MainPackages() > 0,
                      $"{r}, {MainPackages()} package(s)");
            }
        }
        finally { Native.DeleteRecord(ownerId); }
    }

    static void SectionP()
    {
        using var p1 = PeerProcess.Start("P1");
        Check("P: the first program installs", Ok(p1.Send("install")));
        using (var p2 = PeerProcess.Start("P2"))
            p2.Send("uninstall");
        Check("P: a second program's uninstall removed the packages", MainPackages() == 0, $"{MainPackages()} package(s)");
        string c = p1.Send("create pad F0F4 -");
        Check("P: the first program's next create deploys the driver again and succeeds", Ok(c) && MainPackages() > 0, c);
        if (Ok(c))
        {
            p1.Send("stream 3 0.7 125 0");
            var path = WaitPaths(0xBEEF, 0xF0F4, 1, 10_000);
            using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
            Check("P: its input arrives", cap != null && Flows(cap));
        }
    }

    static void SectionQ()
    {
        using var p = PeerProcess.Start("Q");
        p.Send("install");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = Native.AdminMutex("Deploy");
            m.WaitOne();
            held.Set();
            release.Wait(15_000);
            m.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        held.Wait(10_000);
        p.Post("create pad F0F5 -");
        Thread.Sleep(3000);
        release.Set();
        string c = p.Receive("create");
        holder.Join(5000);
        // The create's own time, as the program measured it: the lock was
        // held for 3 s from just before the create was sent.
        int ms = int.TryParse(PeerProcess.Field(c, "ms"), out int v) ? v : -1;
        Check("Q: a create waits for the deploy lock another program holds, then succeeds",
              Ok(c) && ms >= 2500, $"CreateController took {ms} ms: {c}");

        // Q2. The public DriverBuilder.SignDrivers() signs in the package
        // directory a deploy uses, so it waits for the lock too.
        using var held2 = new ManualResetEventSlim();
        using var release2 = new ManualResetEventSlim();
        var holder2 = new Thread(() =>
        {
            using var m = Native.AdminMutex("Deploy");
            m.WaitOne();
            held2.Set();
            release2.Wait(15_000);
            m.ReleaseMutex();
        }) { IsBackground = true };
        holder2.Start();
        held2.Wait(10_000);
        p.Post("builderstep SignDrivers");
        Thread.Sleep(3000);
        release2.Set();
        string s = p.Receive("SignDrivers");
        holder2.Join(5000);
        int sms = int.TryParse(PeerProcess.Field(s, "ms"), out int sv) ? sv : -1;
        Check("Q2: the public DriverBuilder.SignDrivers() waits for the deploy lock, then signs",
              Ok(s) && sms >= 2500, $"SignDrivers took {sms} ms: {s}");

        // Q3. GenerateCatalogs() catalogs the binaries SignDrivers() signed,
        // rather than staging and signing them again, and InstallDrivers()
        // reports that it installed nothing on a machine already running
        // this build's driver.
        string? driverDir = StagingPath(p.Send("stagingdir"));
        string signedDll = driverDir == null ? "" : Path.Combine(Path.GetDirectoryName(driverDir)!, "package", "HIDMaestro.dll");
        (DateTime When, byte[] Bytes)? before = File.Exists(signedDll) ? (File.GetLastWriteTimeUtc(signedDll), File.ReadAllBytes(signedDll)) : null;
        string g = p.Send("builderstep GenerateCatalogs");
        bool same = before != null && File.GetLastWriteTimeUtc(signedDll) == before.Value.When
                    && File.ReadAllBytes(signedDll).AsSpan().SequenceEqual(before.Value.Bytes);
        int cats = driverDir == null ? 0 : Directory.GetFiles(Path.Combine(Path.GetDirectoryName(driverDir)!, "package"), "*.cat").Length;
        Check("Q3: GenerateCatalogs() catalogs the binaries SignDrivers() signed, unchanged",
              Ok(g) && same && cats == 2, $"{g}; {(same ? "same file" : "rewritten")}; {cats} catalog(s)");
        string i = p.Send("builderstep InstallDrivers");
        Check("Q3: InstallDrivers() returns false when this build's driver is already installed",
              Ok(i) && PeerProcess.Field(i, "result") == "False", i);
    }

    static void SectionR()
    {
        const string ownerId = "0000000000000000000000000000000c";
        const string tag = "9.9.9-0123456789abcdef";
        string fake = Path.Combine(StagingRoot, tag);
        try
        {
            using (var p = PeerProcess.Start("R1"))
            {
                string sd = p.Send("stagingdir");
                string? dir = StagingPath(sd);
                string? own = dir == null ? null : Path.GetFileName(Path.GetDirectoryName(dir));
                string? recTag = Native.RecordValue(p.Pid, "StagingTag");
                Check("R1: a program that has only staged has a record naming its staging",
                      own != null && string.Equals(recTag, own, StringComparison.OrdinalIgnoreCase), $"record {recTag ?? "none"}, staging {own}");
            }

            Directory.CreateDirectory(Path.Combine(fake, "driver"));
            using (LiveRecord(ownerId, tag))
            {
                using var p = PeerProcess.Start("R2");
                Native.DropInstalledManifest();
                string ri = p.Send("install");
                Check("R2: a deploy keeps the staging a running program's record names", Ok(ri) && Directory.Exists(fake), ri);
            }
            using (var p = PeerProcess.Start("R3"))
            {
                Native.DropInstalledManifest();
                string ri = p.Send("install");
                Check("R3: once that program is gone the next deploy removes it", Ok(ri) && !Directory.Exists(fake), ri);
            }
        }
        finally
        {
            Native.DeleteRecord(ownerId);
            try { if (Directory.Exists(fake)) Directory.Delete(fake, true); } catch { }
        }
    }

    // ── I ──────────────────────────────────────────────────────────────

    static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro");
    static string StagingRoot => Path.Combine(ProgramDataRoot, "Staging");

    /// <summary>Reply to a peer "stagingdir" command, whose path runs to the
    /// end of the line so a directory with spaces survives.</summary>
    static string? StagingPath(string reply)
    {
        const string tag = "path=";
        int i = reply.IndexOf(tag, StringComparison.Ordinal);
        return i < 0 ? null : reply[(i + tag.Length)..].Trim();
    }

    static void SectionI()
    {
        try
        {
            SectionIChecks();
            Clean();
            SectionIDeploy();
        }
        finally
        {
            // I3 makes the staging root writable by others. An SDK that does
            // not repair it would leave it that way, so the probe puts the
            // protection back itself and never leaves the machine weaker
            // than the SDK under test found it.
            try { if (Directory.Exists(StagingRoot) && Native.WhyWritableByOthers(StagingRoot) != null) Native.Protect(StagingRoot); }
            catch (Exception ex) { Check("I: the staging root is protected again", false, ex.Message); }
        }
    }

    static void SectionIChecks()
    {
        // I1. The driver payload stages under the protected directory, and
        // every directory on the path denies a non-administrator write.
        string? driverDir;
        using (var p = PeerProcess.Start("I1"))
        {
            p.Send("install");
            string r = p.Send("stagingdir");
            driverDir = Ok(r) ? StagingPath(r) : null;
            if (driverDir == null) { Check("I1: the driver payload stages somewhere", false, r); return; }
            Check("I1: the driver payload stages under %ProgramData%\\HIDMaestro\\Staging",
                  driverDir.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase), driverDir);
            string? why = Native.WhyWritableByOthers(driverDir);
            Check("I1: its directory is Administrators/SYSTEM-write-only", why == null, why ?? "");
            // Every ancestor down from %ProgramData%\HIDMaestro.
            for (string d = driverDir; d.Length >= ProgramDataRoot.Length && d.StartsWith(ProgramDataRoot, StringComparison.OrdinalIgnoreCase);
                 d = Path.GetDirectoryName(d)!)
            {
                string? w = Native.WhyWritableByOthers(d);
                Check($"I1: {Path.GetFileName(d)} on the path denies others write", w == null, $"{d}: {w}");
                if (string.Equals(d, ProgramDataRoot, StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        // I2. hmswd.exe stages under the protected directory too, and the
        // SDK no longer writes it to the user-writable %TEMP%\HIDMaestro it
        // used before. The stale copy a pre-v1.13.0 run leaves there is
        // removed first, so the check reflects only what this SDK does.
        string legacyTemp = Path.Combine(Path.GetTempPath(), "HIDMaestro", "hmswd.exe");
        try { if (File.Exists(legacyTemp)) File.Delete(legacyTemp); } catch { }
        using (var p = PeerProcess.Start("I2"))
        {
            string r = p.Send("swdhelper");
            string? swd = Ok(r) ? StagingPath(r) : null;
            if (swd == null) { Check("I2: the SWD helper stages", false, r); return; }
            string swdDir = Path.GetDirectoryName(swd)!;
            Check("I2: hmswd.exe stages under the version's swd directory",
                  swd.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(Path.GetFileName(swdDir), "swd", StringComparison.OrdinalIgnoreCase) &&
                  File.Exists(swd), swd);
            Check("I2: that directory is Administrators/SYSTEM-write-only",
                  Native.WhyWritableByOthers(swdDir) == null, Native.WhyWritableByOthers(swdDir) ?? "");
            Check("I2: the SDK did not write hmswd.exe to the user-writable %TEMP% location",
                  !File.Exists(legacyTemp), legacyTemp);
        }

        // I3. A staging tree a non-administrator could write before the SDK
        // ran is repaired, not used as found.
        Native.MakeUserWritable(StagingRoot);
        Check("I3: the planted staging root starts out writable by others",
              Native.WhyWritableByOthers(StagingRoot) != null);
        using (var p = PeerProcess.Start("I3"))
        {
            p.Send("install");
            string r = p.Send("stagingdir");
            string? dir = Ok(r) ? StagingPath(r) : null;
            Check("I3: the planted staging root is repaired to deny others write",
                  Native.WhyWritableByOthers(StagingRoot) == null, Native.WhyWritableByOthers(StagingRoot) ?? "");
            Check("I3: and the driver still stages into a protected directory",
                  dir != null && Native.WhyWritableByOthers(dir) == null, dir ?? r);
        }

        // I4. A staged file changed to different bytes of the same length is
        // caught by its hash on the next use, not trusted on its size.
        byte[]? good = ReadEmbedded("HIDMaestro.Resources.Inf2Cat.exe");
        if (good == null) { Check("I4: the embedded Inf2Cat.exe is readable", false); return; }
        string? dirA;
        using (var p = PeerProcess.Start("I4-a"))
        {
            p.Send("install");
            dirA = StagingPath(p.Send("stagingdir"));
        }
        string tool = dirA == null ? "" : Path.Combine(dirA, "Inf2Cat.exe");
        if (dirA == null || !File.Exists(tool)) { Check("I4: Inf2Cat.exe is staged", false, tool); return; }
        byte[] tampered = (byte[])good.Clone();
        for (int i = 0; i < tampered.Length; i++) tampered[i] ^= 0xFF;
        try
        {
            File.WriteAllBytes(tool, tampered);
            Check("I4: the staged tool starts out tampered, same length",
                  new FileInfo(tool).Length == good.Length && !File.ReadAllBytes(tool).SequenceEqual(good));
            // Staging only: a fresh process re-stages, and nothing in this
            // step runs the tool.
            using (var p = PeerProcess.Start("I4-b"))
                p.Send("stagingdir");
            Check("I4: the next use restores the staged tool from the embedded bytes",
                  File.ReadAllBytes(tool).SequenceEqual(good));
        }
        finally
        {
            // An SDK that trusts the size leaves the scrambled bytes in
            // place, and Windows answers an attempt to run them with a
            // modal "Unsupported 16-Bit Application" dialog. Put the
            // original back before anything later can run it.
            try { if (!File.ReadAllBytes(tool).SequenceEqual(good)) File.WriteAllBytes(tool, good); } catch { }
        }

        // I5 to I7 plant files in the version's staging directory. An SDK
        // that stages somewhere else, such as a user's %TEMP%, gets no files
        // planted there, and these checks fail on that alone.
        string versionDir = Path.GetDirectoryName(dirA)!;
        if (!versionDir.StartsWith(StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string name in new[]
                     {
                         "I5: a deploy prunes the staging of versions no running program uses",
                         "I6: a staging directory replaced by a link is moved aside, not followed",
                         "I7: a temporary file a process left when it ended mid-write is removed",
                     })
                Check(name, false, $"the SDK stages in {versionDir}");
            return;
        }

        // I5. A deploy, which holds the deploy lock, removes the staging
        // directory of a version no running program uses, and anything a
        // repair moved aside, and keeps its own version's.
        string stale = Path.Combine(StagingRoot, "0.0.1-0000000000000000");
        string movedAside = Path.Combine(versionDir, "swd.untrusted-00000000000000000000000000000000");
        Directory.CreateDirectory(Path.Combine(stale, "driver"));
        File.WriteAllText(Path.Combine(stale, "driver", "stale.txt"), "stale");
        Directory.CreateDirectory(movedAside);
        Uninstall();
        using (var p = PeerProcess.Start("I5"))
        {
            string ri = p.Send("install");
            Check("I5: a deploy runs", PeerProcess.Field(ri, "outcome") == "Installed", ri);
        }
        Check("I5: the stale version's staging directory is removed", !Directory.Exists(stale), stale);
        Check("I5: a moved-aside directory is removed", !Directory.Exists(movedAside), movedAside);
        Check("I5: this version's staging directory stays", Directory.Exists(versionDir), versionDir);

        // I6. A staging directory replaced by a link is moved aside, not
        // followed: the helper lands in a real, protected directory and the
        // link's target receives nothing.
        string swdOp = Path.Combine(versionDir, "swd");
        string linkTarget = Path.Combine(Path.GetTempPath(), "HIDMaestro_coexist_link_target");
        if (Directory.Exists(linkTarget)) Directory.Delete(linkTarget, true);
        Directory.CreateDirectory(linkTarget);
        if (Directory.Exists(swdOp)) Directory.Delete(swdOp, true);
        Directory.CreateSymbolicLink(swdOp, linkTarget);
        Check("I6: the swd directory starts out as a link",
              (File.GetAttributes(swdOp) & FileAttributes.ReparsePoint) != 0);
        using (var p = PeerProcess.Start("I6"))
        {
            string r = p.Send("swdhelper");
            string? swd = Ok(r) ? StagingPath(r) : null;
            Check("I6: the helper stages", swd != null && File.Exists(swd), r);
        }
        Check("I6: the swd directory is now a real directory",
              Directory.Exists(swdOp) && (File.GetAttributes(swdOp) & FileAttributes.ReparsePoint) == 0);
        Check("I6: and Administrators/SYSTEM-write-only",
              Native.WhyWritableByOthers(swdOp) == null, Native.WhyWritableByOthers(swdOp) ?? "");
        Check("I6: the link's target received nothing",
              Directory.GetFileSystemEntries(linkTarget).Length == 0,
              string.Join(", ", Directory.GetFileSystemEntries(linkTarget).Select(Path.GetFileName)));
        try { Directory.Delete(linkTarget, true); } catch { }

        // I7. A temporary file a process left when it ended between writing
        // a staged file and renaming it into place is removed on the next
        // use. One whose writer still runs is left alone.
        int deadPid;
        using (var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            gone.WaitForExit();
            deadPid = gone.Id;
        }
        string orphan = Path.Combine(swdOp, $"hmswd.exe.new-{deadPid}-00000000000000000000000000000000");
        string inFlight = Path.Combine(swdOp, $"hmswd.exe.new-{Environment.ProcessId}-00000000000000000000000000000000");
        File.WriteAllText(orphan, "orphan");
        File.WriteAllText(inFlight, "in flight");
        using (var p = PeerProcess.Start("I7"))
        {
            string r = p.Send("swdhelper");
            Check("I7: the helper stages", Ok(r), r);
        }
        Check("I7: the temporary file of a process that ended is removed", !File.Exists(orphan), orphan);
        Check("I7: the temporary file of a running process stays", File.Exists(inFlight), inFlight);
        try { File.Delete(inFlight); } catch { }
    }

    static void SectionS()
    {
        Uninstall();
        using var p = PeerProcess.Start("S");
        string r1 = p.Send("install");
        Check("S: the first deploy installs", PeerProcess.Field(r1, "outcome") == "Installed", r1);
        string dir = StagingPath(p.Send("stagingdir")) ?? "";
        string prefix = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "HIDMaestro.Native.arm64." : "HIDMaestro.Native.x64.";
        foreach (string dll in new[] { "HIDMaestro.dll", "HMXInput.dll" })
        {
            byte[]? embedded = ReadEmbedded(prefix + dll);
            string staged = Path.Combine(dir, dll);
            Check($"S: after a deploy the driver staging's {dll} is still the embedded, unsigned binary",
                  embedded != null && File.Exists(staged) && File.ReadAllBytes(staged).AsSpan().SequenceEqual(embedded),
                  staged);
        }
        using (var other = PeerProcess.Start("S-other"))
            other.Send("uninstall");
        Check("S: another program's uninstall removed the packages", MainPackages() == 0, $"{MainPackages()} package(s)");
        string r2 = p.Send("install");
        Check("S: the same program's second deploy installs again", PeerProcess.Field(r2, "outcome") == "Installed", r2);
        string c = p.Send("create pad F0F6 -");
        Check("S: and a controller creates on it", Ok(c), c);
    }

    static int XusbPackages() => Directory.GetDirectories(FileRepository, "hidmaestro_xusb.inf_*").Length;

    /// <summary>The published name (oemNN.inf) of each package of an INF, as
    /// pnputil lists them.</summary>
    static List<string> PublishedNames(string originalName)
    {
        var psi = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var proc = Process.Start(psi)!;
        string text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        var names = new List<string>();
        string? published = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("Published Name:", StringComparison.OrdinalIgnoreCase))
                published = line["Published Name:".Length..].Trim();
            else if (line.StartsWith("Original Name:", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(line["Original Name:".Length..].Trim(), originalName, StringComparison.OrdinalIgnoreCase)
                     && published != null)
                names.Add(published);
        }
        return names;
    }

    static void SectionT()
    {
        SectionT1();
        Clean();
        SectionT2();
        Clean();
        SectionT3();
        Clean();
        SectionT4();
        SectionT5();
    }

    /// <summary>The runner the deploy starts signtool, inf2cat and pnputil
    /// with, on a tool whose last line arrives after its own process has
    /// exited, from a child that shares its output: the line is in what the
    /// runner returns, as a tool's closing error has to be. A child that
    /// keeps the output open longer holds the runner for a bounded time.</summary>
    static void SectionT5()
    {
        using var p = PeerProcess.Start("T5");
        string r = p.Send("runtool");
        Check("T5: the tool runner returns a tool's output to its last line", PeerProcess.Field(r, "last") == "yes", r);
        // T6. The same child holding the output 5 s past the runner's drain
        // bound, both as the peer scales them (HIDMAESTRO_TIMEOUT_SCALE): the
        // runner gives up on the output at the bound, says so, and keeps the
        // tool's exit code, so the deploy lock is not held until the child
        // ends.
        string h = p.Send("runtool beyond", 180_000);
        int ms = int.TryParse(PeerProcess.Field(h, "ms"), out int v) ? v : -1;
        int drain = int.TryParse(PeerProcess.Field(h, "drain"), out int d) ? d : -1;
        Check("T6: a child that keeps the tool's output open holds the runner for the drain bound, not until it ends",
              drain > 0 && PeerProcess.Field(h, "open") == "yes" && PeerProcess.Field(h, "exit") == "0"
              && ms >= drain - 1_000 && ms < drain + 4_000, h);
    }

    static void SectionT1()
    {
        var xusb = PublishedNames("hidmaestro_xusb.inf");
        Check("T: this build's XUSB package is installed", xusb.Count == 1, string.Join(", ", xusb));
        if (xusb.Count != 1) return;
        Run("pnputil.exe", $"/delete-driver {xusb[0]}", FileRepository);
        Check("T: deleting it leaves the main package", XusbPackages() == 0 && MainPackages() == 1,
              $"{MainPackages()} main, {XusbPackages()} XUSB");
        using var p = PeerProcess.Start("T");
        string r = p.Send("install");
        Check("T: InstallDriver adds the missing package", Ok(r) && PeerProcess.Field(r, "outcome") == "Installed"
              && XusbPackages() == 1 && MainPackages() == 1, $"{r}, {MainPackages()} main, {XusbPackages()} XUSB");
        string c = p.Send("create xbox - coexist-t");
        Check("T: an Xbox 360 controller, which needs it, creates", Ok(c), c);
    }

    /// <summary>This build's packages installed and the record of them
    /// gone: the deploy finds both INFs at their own stamps and reinstalls
    /// nothing. A build whose two INFs carry different stamps, as one before
    /// v1.13.0 could, failed here and in T: each INF was compared with the
    /// driver INF's stamp.</summary>
    static void SectionT2()
    {
        Native.DropInstalledManifest();
        using var p = PeerProcess.Start("T2");
        string r = p.Send("install");
        Check("T2: with the record of the install gone, InstallDriver finds this build's packages current",
              PeerProcess.Field(r, "outcome") == "AlreadyCurrent" && MainPackages() == 1 && XusbPackages() == 1,
              $"{r}, {MainPackages()} main, {XusbPackages()} XUSB");
    }

    /// <summary>An older driver whose XUSB package is gone, and a program
    /// that creates without calling InstallDriver: the create's deploy gate
    /// upgrades the driver. Its own record entry, written before the gate,
    /// holds nothing.</summary>
    static void SectionT3()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("T3: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        Uninstall();
        using (var old = PeerProcess.Start("T3-v1.12.0", "v1.12.0"))
            Check("T3: a v1.12.0 program installs its driver", Ok(old.Send("install")));
        var xusb = PublishedNames("hidmaestro_xusb.inf");
        if (xusb.Count == 1) Run("pnputil.exe", $"/delete-driver {xusb[0]}", FileRepository);
        Check("T3: its XUSB package is deleted and its main package stays",
              xusb.Count == 1 && XusbPackages() == 0 && MainPackages() == 1, $"{MainPackages()} main, {XusbPackages()} XUSB");
        using var p = PeerProcess.Start("T3");
        string c = p.Send("create pad F0F7 -");
        string v = Ok(c) ? p.Send("driverversion") : "";
        Check("T3: a create without InstallDriver upgrades the driver and succeeds",
              Ok(c) && v.Contains(p.SdkVersion.Split('.')[0] + "." + p.SdkVersion.Split('.')[1] + ".", StringComparison.Ordinal),
              $"{c}; {v}; SDK {p.SdkVersion}");
    }

    /// <summary>A running v1.12.0 program's Xbox 360 controller, whose XUSB
    /// companion runs on the v1.12.0 XUSB package, and the deploy policy's
    /// add of this build's XUSB package: the companion stays on its own
    /// package.</summary>
    static void SectionT4()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("T4: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        Uninstall();
        using var old = PeerProcess.Start("T4-v1.12.0", "v1.12.0");
        old.Send("install");
        string c = old.Send("create xbox - coexist-t4");
        Check("T4: the v1.12.0 program creates an Xbox 360 controller", Ok(c), c);
        if (!Ok(c)) return;
        List<(string Id, string? Version)> Companions() =>
            Native.InterfaceList(XusbGuid, null).Select(Native.InstanceOfInterface).Where(id => id != null)
                .Select(id => (id!, Native.DriverVersionOf(id!))).ToList();
        List<(string Id, string? Version)> before = new();
        WaitFor(() => (before = Companions()).Count == 1, 10_000);
        Check("T4: its XUSB companion runs on the v1.12.0 package",
              before.Count == 1 && before[0].Version?.StartsWith("1.12.0.", StringComparison.Ordinal) == true,
              string.Join(", ", before.Select(b => $"{b.Id} {b.Version}")));
        if (before.Count != 1) return;
        using (var p = PeerProcess.Start("T4"))
        {
            string a = p.Send("addpackages hidmaestro_xusb.inf");
            Check("T4: this build's XUSB package is added beside it", Ok(a) && XusbPackages() == 2, $"{a}, {XusbPackages()} XUSB");
        }
        Thread.Sleep(2000);
        string? after = Native.DriverVersionOf(before[0].Id);
        Check("T4: the running program's companion stays on its own package", after == before[0].Version,
              $"{before[0].Id}: {before[0].Version} before, {after ?? "absent"} after");
    }

    static byte[]? ReadEmbedded(string logicalName)
    {
        try
        {
            using var s = typeof(HIDMaestro.HMContext).Assembly.GetManifestResourceStream(logicalName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
