// arith_roundtrip_check: the one arithmetic of the data-driven design
// (docs/design/data-driven-devices.md, decision (a)), offline.
//
//   roundtrip   every raw value of every conversion, normalized three ways,
//               through the SDK's own code path for its site, must return
//               (RoundTrip.cs). Fails on a tree that truncates. Then nan
//   nan         a NaN axis or slot writes what an unwritten one writes, on
//               every path (Nan.cs)
//   diff        every float in [0, 1] through each catalog conversion's old
//               and new arithmetic, against the design note's table of what
//               changes, and the per-profile list (Diff.cs)
//   all         roundtrip, nan and diff
//
// Creates no device, opens no section and needs no elevation. Exit 0 PASS,
// 1 FAIL.
using System;
using System.Globalization;
using System.Threading;

internal static class Program
{
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string mode = args.Length > 0 ? args[0] : "roundtrip";
        int fail = 0;
        switch (mode)
        {
            case "roundtrip": fail += RoundTrip.Run(); fail += Nan.Run(); break;
            case "nan": fail += Nan.Run(); break;
            case "diff": fail += Diff.Run(args.Length > 1 ? args[1] : null); break;
            case "all": fail += RoundTrip.Run(); fail += Nan.Run(); fail += Diff.Run(null); break;
            default:
                Console.WriteLine("usage: ArithRoundtripCheck roundtrip | nan | diff [<conversion text>] | all");
                return 1;
        }
        Console.WriteLine($"arith_roundtrip_check {mode}: {(fail == 0 ? "PASS" : $"FAIL, {fail}")}");
        return fail == 0 ? 0 : 1;
    }
}
