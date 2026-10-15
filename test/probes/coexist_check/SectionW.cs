// W. Sweeps, owner records and the exit handler against programs running
//    beside them.
//
//    W1. Three records of a killed program, the first deleted while the
//        SDK reads the records, as another program's sweep deletes a dead
//        record at any moment. The other two are still read.
//    W2. A killed program's device whose record is gone, while the probe
//        holds its transport index and identity claims the way a running
//        program on this SDK holds them between its claims and its record
//        entry. The census and the owner check read it as a running
//        program's, and a sweep leaves it until the claims go.
//    W3. A killed program's controller with an identity key. Another
//        program's sweep is held once it has its census and is about to
//        remove the killed program's device, and the killed program,
//        started again, creates the same key. The sweep goes on and ends,
//        and the new device and its index's configuration are there.
//    W4. The same without a key: the program started again takes its old
//        index and identity, or another index while the sweep holds those.
//    W5. A running v1.12.0 program with a controller, and a program on this
//        SDK that creates two beside it. The v1.12.0 program creates twice
//        more on the lowest indices free among its own controllers. None of
//        them lands on the new program's, the new program's devices stay,
//        and its exit removes its own devices and none of the v1.12.0
//        program's. A create with the key index:1, which names the identity
//        the v1.12.0 program gives its next controller, is refused.
//    W6. A v1.12.0 program with no controller at the moment leaves no sign,
//        so its next create lands on the index a program on this SDK took.
//        The new program's exit still removes only its own device.
//    W7. A lease nothing references, the way a controller whose output
//        mapping failed holds it once its program drops the context without
//        disposing it. After a garbage collection that closes a claim
//        nothing references, the lease's claims are still held.
//    W8. A program exits while a create on another thread is held before
//        its input mapping, and a handler of its own, which runs after the
//        SDK's, lets the create finish. The device it made is named by the
//        program's record, and the next sweep removes it.
//    W9. A program exits with a controller, and a handler of its own,
//        which runs after the SDK's, creates another. The SDK's handler
//        removes the first, and the record that names the second carries
//        the program's process id, so the next sweep removes it.
//    W10. A program exits with a pad and a composite, and a handler of its
//        own, which runs after the SDK's, disposes its context: the
//        composite's device is disposed twice, once by each. The program
//        ends with exit code 0 and leaves no device and no record.
//    W11. A v1.11.0 program's GameInput table, which it wrote at its
//        create. A cleanup leaves it while that program's device of the
//        identity is present, and removes it once the device is gone, while
//        the sweeping program runs a composite on USB/IP, whose input
//        section has no configuration and no devnode the census lists.
//    W12. A v1.11.0 table no device or configuration names, beside an input
//        section on index 120 that no configuration, device, record or
//        acknowledgement section explains, as a v1.11.0 create in flight
//        leaves it, and a v1.12.0 owner pipe for that index that a program
//        that is not elevated made. A cleanup keeps the table, and removes
//        it once the section is gone.
//    W13. A v1.11.0 table no device or configuration names, while that
//        program runs a composite on USB/IP: its input section has no
//        configuration, record or acknowledgement section, and its import
//        alone explains it. A cleanup removes the table.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    static void SectionW()
    {
        SectionWRecords();
        Clean();
        SectionWUnrecorded();
        Clean();
        SectionWReclaim("W3", 0xF202, "coexist-w3");
        Clean();
        SectionWReclaim("W4", 0xF203, "-");
        Clean();
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
            Check("W5: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
        else
        {
            SectionWLegacy();
            Clean();
            SectionWLegacyIdle();
        }
        Clean();
        SectionWLease();
        Clean();
        SectionWExitMidCreate();
        Clean();
        SectionWExitThenCreate();
        Clean();
        SectionWExitThenDispose();
        Clean();
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.11.0", "CoexistPeer.exe")))
            Check("W11: the v1.11.0 peer is built", false, "build CoexistCheck.csproj");
        else
        {
            SectionWLegacyTable();
            Clean();
            SectionWLegacyTablePipe();
            Clean();
            SectionWLegacyTableImport();
        }
    }

    /// <summary>W1.</summary>
    static void SectionWRecords()
    {
        using var p = PeerProcess.Start("W1");
        string r = p.Send("readallrace");
        Check("W1: the record is deleted while the SDK reads it", PeerProcess.Field(r, "deleted") == "1", r);
        Check("W1: the two records after it are still read", PeerProcess.Field(r, "listed") == "2", r);
    }

    /// <summary>W2.</summary>
    static void SectionWUnrecorded()
    {
        string inst, t;
        string? ownerId;
        using (var k = PeerProcess.Start("W2-killed"))
        {
            string c = k.Send("create pad F201 coexist-w2");
            if (!Ok(c)) { Check("W2: the program creates", false, c); return; }
            inst = PeerProcess.Field(c, "instance")!;
            t = PeerProcess.Field(c, "transport")!;
            ownerId = Native.OwnerRecords().Where(r => r.Value == k.Pid).Select(r => r.Key).FirstOrDefault();
            k.Kill();
        }
        if (ownerId == null) { Check("W2: the program has an owner record", false); return; }
        Native.DeleteRecord(ownerId);
        string token = inst.Substring(inst.LastIndexOf('\\') + 1);
        using var s = PeerProcess.Start("W2-sweeper");
        using (var index = Native.HoldPipe($"HIDMaestro.Index.{t}"))
        using (var identity = Native.HoldPipe($"HIDMaestro.Identity.{token}"))
        {
            Check("W2: the probe holds the device's index and identity claims", index != null && identity != null);
            string? entry = Y.CensusEntry(s.Send("census"), token);
            Check("W2: the census reads the device as a running program's", entry?.Split('|')[2] == "Live", entry ?? "(no entry)");
            string cl = s.Send($"classify {inst}");
            Check("W2: so does the owner check a create runs on a rival", PeerProcess.Field(cl, "owner") == "Live", cl);
            s.Send("sweep");
            Check("W2: a sweep leaves the device", Native.IsPresent(inst));
        }
        string? after = Y.CensusEntry(s.Send("census"), token);
        Check("W2: with the claims gone it reads as a v1.12.0 program's that has exited", after?.Split('|')[2] == "DeadLegacy", after ?? "(no entry)");
        s.Send("sweep");
        Check("W2: and the next sweep removes it", WaitFor(() => !Native.IsPresent(inst), 15_000));
    }

    /// <summary>W3 with an identity key, W4 without.</summary>
    static void SectionWReclaim(string label, ushort pid, string key)
    {
        using (var k1 = PeerProcess.Start($"{label}-killed"))
        {
            string c1 = k1.Send($"create pad {pid:X4} {key}");
            if (!Ok(c1)) { Check($"{label}: the program creates", false, c1); return; }
            k1.Kill();
        }
        using var s = PeerProcess.Start($"{label}-sweeper");
        string ph = s.Send("pausereclaim");
        if (!Ok(ph)) { Check($"{label}: the sweep can be held", false, ph); return; }
        s.Post("sweep");
        string paused = s.Receive("sweep, held");
        Check($"{label}: another program's sweep is held after its census, one group to remove",
              paused.StartsWith("OK paused", StringComparison.Ordinal) && PeerProcess.Field(paused, "targets") == "1", paused);

        using var k2 = PeerProcess.Start($"{label}-again");
        k2.Post($"create pad {pid:X4} {key}");
        // Past the started program's own sweep and its create, which take
        // about two seconds when nothing holds them.
        Thread.Sleep(8000);
        string swept = s.Send("go");
        Check($"{label}: the held sweep goes on and ends", swept == "OK sweep", swept);
        string c2 = k2.Receive("create", 180_000);
        Check($"{label}: the program started again creates", Ok(c2), c2);
        if (!Ok(c2)) return;
        string inst = PeerProcess.Field(c2, "instance")!, t = PeerProcess.Field(c2, "transport")!;
        Check($"{label}: its device is present and started once the sweep is done", WaitFor(() => Native.IsStarted(inst), 10_000), inst);
        Check($"{label}: its configuration Controller{t} is there", ConfigExists(t));
        string token = inst.Substring(inst.LastIndexOf('\\') + 1);
        string? entry = Y.CensusEntry(k2.Send("census"), token);
        Check($"{label}: it reads as the program's own", entry?.Split('|')[2] == "Self", entry ?? "(no entry)");
    }

    /// <summary>W5.</summary>
    static void SectionWLegacy()
    {
        using var old = PeerProcess.Start("W5-v1.12.0", "v1.12.0");
        string o1 = old.Send("create pad F204 -");
        if (!Ok(o1)) { Check("W5: the v1.12.0 program creates", false, o1); return; }
        using var nu = PeerProcess.Start("W5-new");
        string n1 = nu.Send("create pad F205 -");
        string n2 = nu.Send("create pad F206 coexist-w5");
        if (!Ok(n1) || !Ok(n2)) { Check("W5: the new program creates beside it", false, $"{n1} / {n2}"); return; }
        // index:1 is the identity the v1.12.0 program gives its next
        // controller, whatever index this one would land on.
        string nk = nu.Send("create pad F211 index:1");
        Check("W5: a create with the key index:1 is refused with HMResourceInUseException",
              nk.StartsWith("ERR create", StringComparison.Ordinal) && nk.Contains("HMResourceInUseException"), nk);
        string[] newInst = { PeerProcess.Field(n1, "instance")!, PeerProcess.Field(n2, "instance")! };
        string[] newT = { PeerProcess.Field(n1, "transport")!, PeerProcess.Field(n2, "transport")! };
        var pathA = WaitPaths(0xBEEF, 0xF205, 1, 10_000);
        var pathB = WaitPaths(0xBEEF, 0xF206, 1, 10_000);
        using var capA = pathA.Count == 1 ? HidCapture.Open(pathA[0]) : null;
        using var capB = pathB.Count == 1 ? HidCapture.Open(pathB[0]) : null;
        if (capA == null || capB == null) { Check("W5: the new program's devices open", false); return; }

        string o2 = old.Send("create pad F207 -");
        string o3 = old.Send("create pad F208 -");
        if (!Ok(o2) || !Ok(o3)) { Check("W5: the v1.12.0 program creates twice more", false, $"{o2} / {o3}"); return; }
        string[] oldT = { PeerProcess.Field(o1, "transport")!, PeerProcess.Field(o2, "transport")!, PeerProcess.Field(o3, "transport")! };
        string[] oldInst = { PeerProcess.Field(o1, "instance")!, PeerProcess.Field(o2, "instance")!, PeerProcess.Field(o3, "instance")! };
        Check("W5: the v1.12.0 program's next creates take no index of the new program's",
              !oldT.Intersect(newT).Any(), $"v1.12.0 {string.Join(",", oldT)}, new {string.Join(",", newT)}");
        Thread.Sleep(500);
        Check("W5: the new program's devices stay, and their open handles never fail",
              !capA.Broken && !capB.Broken && newInst.All(Native.IsStarted),
              $"handles {(capA.Broken ? "broken" : "open")}/{(capB.Broken ? "broken" : "open")}");

        string q = nu.Send("quit");
        Check("W5: the new program exits without disposing", q == "OK quit" && nu.WaitExit(120_000), q);
        Check("W5: its exit handler removes its devices", newInst.All(i => WaitFor(() => !Native.IsPresent(i), 15_000)));
        Check("W5: and leaves the v1.12.0 program's three", oldInst.All(Native.IsStarted),
              string.Join(", ", oldInst.Select(i => $"{i} {(Native.IsStarted(i) ? "started" : "gone")}")));
    }

    /// <summary>W6.</summary>
    static void SectionWLegacyIdle()
    {
        using var old = PeerProcess.Start("W6-v1.12.0", "v1.12.0");
        string o1 = old.Send("create pad F209 -");
        if (!Ok(o1)) { Check("W6: the v1.12.0 program creates", false, o1); return; }
        old.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, 0xF209).Count == 0, 10_000);
        using var nu = PeerProcess.Start("W6-new");
        string n1 = nu.Send("create pad F20A coexist-w6");
        string o2 = old.Send("create pad F20B -");
        if (!Ok(n1) || !Ok(o2)) { Check("W6: both programs create", false, $"{n1} / {o2}"); return; }
        Check("W6: the v1.12.0 program's create lands on the new program's index, which nothing here can see coming",
              PeerProcess.Field(n1, "transport") == PeerProcess.Field(o2, "transport"),
              $"new {PeerProcess.Field(n1, "transport")}, v1.12.0 {PeerProcess.Field(o2, "transport")}");
        string instNew = PeerProcess.Field(n1, "instance")!, instOld = PeerProcess.Field(o2, "instance")!;
        string q = nu.Send("quit");
        Check("W6: the new program exits without disposing", q == "OK quit" && nu.WaitExit(120_000), q);
        Check("W6: its exit handler removes its device", WaitFor(() => !Native.IsPresent(instNew), 15_000));
        Thread.Sleep(500);
        Check("W6: and leaves the v1.12.0 program's device on the same index", Native.IsStarted(instOld), instOld);
    }

    /// <summary>W7.</summary>
    static void SectionWLease()
    {
        using var p = PeerProcess.Start("W7");
        string r = p.Send("droplease coexist-w7");
        if (!Ok(r)) { Check("W7: the program takes a lease and drops it", false, r); return; }
        Check("W7: the collection closes a claim nothing references", PeerProcess.Field(r, "control") == "1", r);
        Check("W7: the dropped lease's identity claim is still held", Native.ClaimExists($"HIDMaestro.Identity.{PeerProcess.Field(r, "token")}"), r);
        Check("W7: and its transport index claim", Native.ClaimExists($"HIDMaestro.Index.{PeerProcess.Field(r, "transport")}"), r);
    }

    /// <summary>W8.</summary>
    static void SectionWExitMidCreate()
    {
        using var p = PeerProcess.Start("W8");
        string r = p.Send("quitmidcreate pad F20C coexist-w8");
        if (!Ok(r)) { Check("W8: the program exits with a create in flight", false, r); return; }
        Check("W8: the program ends", p.WaitExit(120_000));
        string inst = $@"ROOT\HIDClass\{PeerProcess.Field(r, "token")}";
        Check("W8: the create in flight made its device before the program ended", Native.IsPresent(inst), inst);
        var named = Native.RecordsNaming(PeerProcess.Field(r, "token")!);
        Check("W8: the program's record still names it", named.Count > 0, $"{named.Count} records");
        using var s = PeerProcess.Start("W8-sweeper");
        s.Send("sweep");
        Check("W8: and the next sweep removes it", WaitFor(() => !Native.IsPresent(inst), 15_000));
    }

    /// <summary>W9.</summary>
    static void SectionWExitThenCreate()
    {
        using var p = PeerProcess.Start("W9");
        string c = p.Send("create pad F20D -");
        if (!Ok(c)) { Check("W9: the program creates", false, c); return; }
        string first = PeerProcess.Field(c, "instance")!;
        string r = p.Send("quitthencreate pad F20E coexist-w9");
        if (!Ok(r)) { Check("W9: the program exits", false, r); return; }
        Check("W9: the program ends", p.WaitExit(120_000));
        Check("W9: the SDK's exit handler removed the controller the program left", !Native.IsPresent(first), first);
        string inst = $@"ROOT\HIDClass\{PeerProcess.Field(r, "token")}";
        Check("W9: the create in the program's own handler, after the SDK's, made its device", Native.IsPresent(inst), inst);
        var named = Native.RecordsNaming(PeerProcess.Field(r, "token")!);
        Check("W9: the record that names it carries the program's process id", named.Any(n => n.Pid == p.Pid),
              string.Join(", ", named.Select(n => $"{n.OwnerId} pid {n.Pid?.ToString() ?? "none"}")));
        using var s = PeerProcess.Start("W9-sweeper");
        s.Send("sweep");
        Check("W9: and the next sweep removes it", WaitFor(() => !Native.IsPresent(inst), 15_000));
    }

    /// <summary>W10.</summary>
    static void SectionWExitThenDispose()
    {
        var before = Native.HidPaths(0x054C, 0x0CE6);
        using var p = PeerProcess.Start("W10");
        string cp = p.Send("create pad F20F coexist-w10");
        string cc = p.Send("create composite - coexist-w10c");
        if (!Ok(cp) || !Ok(cc)) { Check("W10: the program creates a pad and a composite", false, $"{cp} / {cc}"); return; }
        string pad = PeerProcess.Field(cp, "instance")!;
        WaitPaths(0x054C, 0x0CE6, 1, 20_000, before);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string r = p.Send("quitdispose");
        bool ended = Ok(r) && p.WaitExit(120_000);
        Check("W10: the program ends", ended, $"{r}, {sw.ElapsedMilliseconds} ms");
        if (!ended) return;
        Check("W10: with exit code 0", p.ExitCode == 0, $"exit code {p.ExitCode}");
        Check("W10: its pad is gone", WaitFor(() => !Native.IsPresent(pad), 15_000), pad);
        Check("W10: its composite is gone",
              WaitFor(() => !Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Any(), 15_000));
        Check("W10: and its record", !Native.OwnerRecords().ContainsValue(p.Pid));
    }

    /// <summary>W11.</summary>
    static void SectionWLegacyTable()
    {
        const string table = @"SYSTEM\CurrentControlSet\Control\GameInput\Devices\BEEFF21000010005";
        bool TableThere() { using var k = Registry.LocalMachine.OpenSubKey(table); return k != null; }
        Registry.LocalMachine.DeleteSubKeyTree(table, throwOnMissingSubKey: false);
        using var old = PeerProcess.Start("W11-v1.11.0", "v1.11.0");
        string c = old.Send("create pad F210 -");
        if (!Ok(c)) { Check("W11: the v1.11.0 program creates", false, c); return; }
        Check("W11: the v1.11.0 program writes its GameInput table", TableThere());
        using var s = PeerProcess.Start("W11-sweeper");
        s.Send("sweep");
        Check("W11: a cleanup leaves the table while the v1.11.0 program's device of that identity is present", TableThere());
        old.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, 0xF210).Count == 0, 10_000);
        string cc = s.Send("create composite - coexist-w11c");
        Check("W11: the sweeping program runs a composite", Ok(cc), cc);
        s.Send("sweep");
        Check("W11: once that device is gone, the next cleanup removes the table, the composite's input section notwithstanding", !TableThere());
    }

    /// <summary>A table of an identity no device and no configuration names:
    /// a v1.11.0 program writes it at its create and keeps nothing of it
    /// once that controller is disposed.</summary>
    static string? LeaveLegacyTable(PeerProcess old, ushort pid, string label)
    {
        string table = $@"SYSTEM\CurrentControlSet\Control\GameInput\Devices\BEEF{pid:X4}00010005";
        Registry.LocalMachine.DeleteSubKeyTree(table, throwOnMissingSubKey: false);
        string c = old.Send($"create pad {pid:X4} -");
        if (!Ok(c)) { Check($"{label}: the v1.11.0 program creates", false, c); return null; }
        old.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, pid).Count == 0, 10_000);
        using var k = Registry.LocalMachine.OpenSubKey(table);
        if (k == null) { Check($"{label}: the v1.11.0 program writes its GameInput table", false); return null; }
        return table;
    }

    static bool KeyThere(string path)
    {
        using var k = Registry.LocalMachine.OpenSubKey(path);
        return k != null;
    }

    /// <summary>W12.</summary>
    static void SectionWLegacyTablePipe()
    {
        const int index = 120;
        using var old = PeerProcess.Start("W12-v1.11.0", "v1.11.0");
        string? table = LeaveLegacyTable(old, 0xF212, "W12");
        if (table == null) return;
        // A v1.11.0 create on index 120 between its input section and its
        // configuration, and an owner pipe for that index that a program
        // that is not elevated made.
        IntPtr section = Native.CreateSection($@"Global\HIDMaestroInput{index}");
        IntPtr user = Native.MediumToken();
        Microsoft.Win32.SafeHandles.SafeFileHandle? pipe;
        try { pipe = Y.AsUser(user, () => Native.CreatePipe($"HIDMaestroOwner{index}", out _)); }
        finally { Native.CloseHandle(user); }
        try
        {
            Check("W12: a program that is not elevated holds the v1.12.0 owner pipe of the index", pipe != null);
            using var s = PeerProcess.Start("W12-sweeper");
            s.Send("sweep");
            Check("W12: a cleanup keeps the table while the index's input section has no explanation but that pipe", KeyThere(table));
        }
        finally
        {
            pipe?.Dispose();
            Native.CloseHandle(section);
        }
        using (var s = PeerProcess.Start("W12-sweeper-after"))
        {
            s.Send("sweep");
            Check("W12: once the section is gone, the next cleanup removes the table", !KeyThere(table));
        }
    }

    /// <summary>W13.</summary>
    static void SectionWLegacyTableImport()
    {
        var before = Native.HidPaths(0x054C, 0x0CE6);
        using var old = PeerProcess.Start("W13-v1.11.0", "v1.11.0");
        string? table = LeaveLegacyTable(old, 0xF213, "W13");
        if (table == null) return;
        string cc = old.Send("create composite - -");
        bool up = Ok(cc) && WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Any(), 20_000);
        Check("W13: the v1.11.0 program runs a composite, whose input section has no configuration and no record", up, cc);
        if (!up) return;
        string t = PeerProcess.Field(cc, "transport") ?? "?";
        Check("W13: that section has no acknowledgement section beside it", !Native.SectionExists($@"Global\HIDMaestroInputAck{t}"), $"index {t}");
        using var s = PeerProcess.Start("W13-sweeper");
        s.Send("sweep");
        Check("W13: a cleanup removes the table, the composite's import explaining its section", !KeyThere(table));
    }

    /// <summary>The removal times behind the exit handler's and the sweep's
    /// way of removing several controllers: <paramref name="runs"/> times,
    /// a program creates four Xbox 360 controllers and exits without
    /// disposing them, timed from its last command to its end, and a program
    /// creates four and is killed, and another program's sweep is timed.
    /// Prints p50 and p95 of each. Run as "removaltimes &lt;runs&gt;".</summary>
    static int RemovalTimes(int runs)
    {
        var exit = new List<double>();
        var sweep = new List<double>();
        int left = 0;
        for (int i = 1; i <= runs; i++)
        {
            string[]? inst;
            using (var p = PeerProcess.Start($"T{i}-exit"))
            {
                inst = CreateFour(p);
                if (inst == null) { Check("removal times: four Xbox 360 controllers create", false); return 1; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                p.Send("quit");
                if (!p.WaitExit(600_000)) { Check("removal times: the program ends", false); return 1; }
                exit.Add(sw.Elapsed.TotalMilliseconds);
            }
            if (inst.Any(Native.IsPresent)) left++;

            using (var p = PeerProcess.Start($"T{i}-killed"))
            {
                inst = CreateFour(p);
                if (inst == null) { Check("removal times: four Xbox 360 controllers create", false); return 1; }
                p.Kill();
            }
            using (var s = PeerProcess.Start($"T{i}-sweeper"))
            {
                s.Send("census");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                s.Send("sweep", 600_000);
                sweep.Add(sw.Elapsed.TotalMilliseconds);
            }
            if (inst.Any(Native.IsPresent)) left++;
            Console.WriteLine($"    run {i}: exit {exit[^1]:F0} ms, sweep {sweep[^1]:F0} ms");
        }
        Console.WriteLine($"  exit handler, four Xbox 360: {Percentiles(exit)}");
        Console.WriteLine($"  sweep of a killed program's four Xbox 360: {Percentiles(sweep)}");
        Check("removal times: every device is gone after each removal", left == 0, $"{left} removals left a device");
        return s_failures == 0 ? 0 : 1;
    }

    static string[]? CreateFour(PeerProcess p)
    {
        var inst = new List<string>();
        for (int n = 0; n < 4; n++)
        {
            string c = p.Send("create xbox - -");
            if (!Ok(c)) return null;
            inst.Add(PeerProcess.Field(c, "instance")!);
        }
        return inst.ToArray();
    }

    /// <summary>p50 and p95 by nearest rank, with the run count.</summary>
    static string Percentiles(List<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        double Rank(double q) => s[Math.Max(0, (int)Math.Ceiling(q * s.Length) - 1)];
        return FormattableString.Invariant($"p50 {Rank(0.50):F0} ms, p95 {Rank(0.95):F0} ms, min {s[0]:F0}, max {s[^1]:F0}, n={s.Length}");
    }

    static bool ConfigExists(string transport)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestro\Controller{transport}");
        return k != null;
    }
}
