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
//
// Each program is a peer process (this binary with --peer, or the same
// source built against an older release under legacy\). Requires
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
            ('A', "two programs with no identity key", SectionA),
            ('B', "one identity key in two programs", SectionB),
            ('C', "a second program starts while the first streams", SectionC),
            ('D', "a killed program's devices", SectionD),
            ('E', "mixed SDK versions", SectionE),
            ('F', "InstallDriver at the same moment in two programs", SectionF),
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
    /// SDK's version, catalogued and signed the way the SDK does it, then
    /// installed. Returns the version Windows reports for it.</summary>
    static string InstallNewerDriver()
    {
        string staging = HIDMaestro.Internal.DriverBuilder.EnsureExtracted();
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
}
