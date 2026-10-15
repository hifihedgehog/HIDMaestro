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

    static bool ConfigExists(string transport)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestro\Controller{transport}");
        return k != null;
    }
}
