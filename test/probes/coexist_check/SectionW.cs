// W. Sweeps, owner records and the exit handler against programs running
//    beside them.
//
//    W1. Three records of a killed program, the first deleted while the
//        SDK reads the records, as another program's sweep deletes a dead
//        record at any moment. The other two are still read.
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
    }

    /// <summary>W1.</summary>
    static void SectionWRecords()
    {
        using var p = PeerProcess.Start("W1");
        string r = p.Send("readallrace");
        Check("W1: the record is deleted while the SDK reads it", PeerProcess.Field(r, "deleted") == "1", r);
        Check("W1: the two records after it are still read", PeerProcess.Field(r, "listed") == "2", r);
    }
}
