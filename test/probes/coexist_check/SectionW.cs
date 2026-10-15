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
using System;
using System.Collections.Generic;
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

    static bool ConfigExists(string transport)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestro\Controller{transport}");
        return k != null;
    }
}
