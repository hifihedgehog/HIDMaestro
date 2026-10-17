// rest: what a descriptor-path field carries when the consumer writes no
// value for it, on catalog profiles, through the profile's own report
// builder. A stick rests at its center, as a real stick at rest reads near
// its center (design note, defects table). Every other axis rests as
// before: a signed one at its center, an unsigned one at its minimum.
using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Rest
{
    public static int Run()
    {
        var db = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        int fail = 0;
        void Expect(string id, HMAxis axis, int want, string why)
        {
            var b = db[id].GetOrBuildReportBuilder();
            var f = b.AxisFields[axis];
            var report = new byte[b.InputReportByteSize];
            b.BuildReportInto(report, new Dictionary<HMAxis, float>());
            int off = f.BitOffset + (b.InputReportId != 0 ? 8 : 0);
            long raw = 0;
            for (int i = 0; i < f.BitSize; i++)
                if (((report[(off + i) >> 3] >> ((off + i) & 7)) & 1) != 0) raw |= 1L << i;
            if (f.LogicalMin < 0 && (raw & (1L << (f.BitSize - 1))) != 0) raw -= 1L << f.BitSize;
            bool ok = raw == want;
            if (!ok) fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {id} {axis} ({f.LogicalMin}..{f.LogicalMax}) unwritten rests at {raw}, expected {want}: {why}");
        }
        Console.WriteLine("rest: descriptor-path fields with no value written");
        foreach (var a in new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rz })
            Expect("amazon-luna-usb", a, 128, "a 0..255 stick its layout names rests at 0.5");
        foreach (var a in new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rz })
            Expect("dualshock-4-v2", a, 128, "a 0..255 stick its layout names rests at 0.5");
        Expect("dualshock-4-v2", HMAxis.Rx, 0, "an unsigned trigger rests at its minimum");
        Expect("xbox-one-s", HMAxis.X, 32768, "a 0..65535 stick rests at 0.5");
        Expect("sidewinder-force-feedback-2", HMAxis.Rz, 0, "a signed twist rests at its center, as before");
        Expect("steelseries-srws1", HMAxis.X, 0, "a signed wheel rests at its center, as before");
        Console.WriteLine(fail == 0 ? "rest: PASS" : $"rest: FAIL, {fail} field(s)");
        return fail;
    }
}
