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
    /// <summary>A fresh copy of a catalog profile, through its own JSON, so
    /// a field set here reaches no other check.</summary>
    static ControllerProfile Copy(ControllerProfile p) =>
        System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(p, HMJsonContext.Default.ControllerProfile),
            HMJsonContext.Default.ControllerProfile)!;

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
            Expect("amazon-luna-usb", a, 0x7F, "a stick rests at 0.5, which axisCenters puts at 0x7F, where SDL's Luna driver reads zero");
        foreach (var a in new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rz })
            Expect("amazon-luna-ble", a, 0x7FFF, "a 16-bit stick rests at its axisCenters value, high byte 0x7F");
        foreach (var a in new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rz })
            Expect("dualshock-4-v2", a, 128, "a 0..255 stick its layout names rests at 0.5");
        Expect("dualshock-4-v2", HMAxis.Rx, 0, "an unsigned trigger rests at its minimum");
        Expect("xbox-one-s", HMAxis.X, 32768, "a 0..65535 stick rests at 0.5");
        Expect("sidewinder-force-feedback-2", HMAxis.Rz, 0, "a signed twist rests at its center, as before");
        Expect("steelseries-srws1", HMAxis.X, 0, "a signed wheel rests at its center, as before");
        // A center the profile cannot have is refused when its report
        // builder is made, which a create does first.
        foreach (var (centers, why) in new (Dictionary<string, int>, string)[]
        {
            (new() { ["Q"] = 127 }, "an axis name the SDK does not know"),
            (new() { ["48"] = 127 }, "a number in place of a name"),
            (new() { ["X"] = 256 }, "a center outside 0..255"),
            (new() { ["Slider"] = 127 }, "an axis the descriptor lacks"),
        })
        {
            var p = Copy(db["amazon-luna-usb"]);
            p.AxisCenters = centers;
            string? refused = null;
            try { p.GetOrBuildReportBuilder(); }
            catch (InvalidOperationException e) { refused = e.Message; }
            bool ok = refused != null && refused.Contains("amazon-luna-usb", StringComparison.Ordinal) && refused.Contains("axisCenters.", StringComparison.Ordinal);
            if (!ok) fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {why} is refused: {refused ?? "accepted"}");
        }
        Console.WriteLine(fail == 0 ? "rest: PASS" : $"rest: FAIL, {fail} field(s)");
        return fail;
    }
}
