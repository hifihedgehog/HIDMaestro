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
}
