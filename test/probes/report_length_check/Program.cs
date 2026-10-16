// Report lengths and IDs the SDK reads from a HID report descriptor, against
// an independent parser.
//
//   1. reference_lengths.json holds every catalog report descriptor's input,
//      output and feature lengths per report ID, as hid-tools reads them
//      (make_reference.py beside this file writes it). The file must cover
//      exactly the catalog this SDK embeds, byte for byte.
//   2. HidReportMap's lengths for every report, and HidReportBuilder's for
//      every input report, equal the reference on every descriptor.
//   3. The input report the driver sends a data-only frame as, which the
//      SDK writes into the controller's configuration: its ID is the report
//      the SDK fills, a declared input report, and its length is that
//      report's. A profile's own inputReportSize may only lengthen a report
//      up to the descriptor's largest input report.
//   4. InputReportId, the value the driver takes its report ID from: its
//      ID, its length and its descriptor, absent where a profile's
//      inputReportSize is another report's, and a profile's dataReportId.
//      Then descriptors made by hand for the items a walker can get wrong:
//      Push and Pop around Report Size, Report Count and a report ID,
//      nested pushes, a long item, signed ranges with Unit, Unit Exponent
//      and physical items, a 4-byte usage, the locals End Collection
//      clears, and a first report ID that is an output report's. Each
//      expected value is worked out from HID 1.11 section 6.2.2 as
//      hid-core.c applies it.
//   5. The other walkers on the same items: PidReportIdExtractor,
//      GameInputMapping.HasMeasuredShape and HidDescriptorBuilder.AddRaw.
//
// No device, no elevation. Exit 0 PASS / 1 FAIL.
//
// `live` (elevated, creates devices): the reports the installed driver
// sends. Three controllers are up and read in one window. dualshock-4-v2,
// whose report did not change, is the positive control: report 0x01
// carrying the submitted left stick X. genesis-nso must arrive as report
// 0x3F carrying the submitted button and never as 0x21, the report the
// driver sent before InputReportId. microsoft-designer-keyboard's
// data-only frame must arrive as its keyboard report 0x01, seen through
// Raw Input as F24. Then logitech-wingman-formula-gp must arrive as its
// 5-byte report with Y whole at byte 2, where it was 3 bytes with Y one
// bit. Exit 0 PASS, 1 FAIL, 2 not elevated.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    internal static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    static string Lengths(IDictionary<int, int> d) =>
        d.Count == 0 ? "none" : string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

    // ── The SDK's answers ────────────────────────────────────────────────

    static Dictionary<int, int> MapLengths(byte[] d, HidReportKind kind)
    {
        var o = new Dictionary<int, int>();
        foreach (var r in HidReportMap.Parse(d).Reports)
            if (r.Kind == kind) o[r.ReportId] = r.FrameBytes;
        return o;
    }

    static Dictionary<int, int> BuilderLengths(byte[] d)
    {
        var o = new Dictionary<int, int>();
        foreach (var (id, size) in HidReportBuilder.Parse(d).InputReports) o[id] = size;
        return o;
    }

    static readonly MethodInfo? s_driverInputReport = typeof(ProfileDatabase).Assembly
        .GetType("HIDMaestro.Internal.DeviceOrchestrator")!
        .GetMethod("DriverInputReport", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo? s_inputReportIdValue = typeof(ProfileDatabase).Assembly
        .GetType("HIDMaestro.Internal.DeviceOrchestrator")!
        .GetMethod("InputReportIdValue", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo? s_oldCompute = typeof(ProfileDatabase).Assembly
        .GetType("HIDMaestro.Internal.DeviceOrchestrator")!
        .GetMethod("ComputeInputReportByteLength", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>The registry value InputReportId the SDK writes for a
    /// profile, or null for none (or an SDK without it).</summary>
    static byte[]? IdValue(ControllerProfile p) =>
        s_inputReportIdValue?.Invoke(null, new object[] { p }) as byte[];

    /// <summary>The report ID and length the driver sends a data-only frame
    /// as, from what the SDK writes. The driver takes InputReportId's ID
    /// when the value's length is InputReportByteLength and its descriptor
    /// the one in use (driver.c ReadConfigFromRegistry), and otherwise the
    /// first Report ID item of the first Application Collection (driver.c
    /// DeviceAdd), the only rule before the value. An SDK without
    /// DriverInputReport wrote the length its own count gave
    /// (ComputeInputReportByteLength, through 7b6e14d).</summary>
    static (int Id, int Length) DriverReport(ControllerProfile p)
    {
        var d = p.GetDescriptorBytes()!;
        if (s_driverInputReport != null)
        {
            var t = (System.Runtime.CompilerServices.ITuple)s_driverInputReport.Invoke(null, new object[] { p })!;
            int length = (int)t[1]!;
            return (DriverTakes(IdValue(p), length, d) ?? OldDriverScan(d), length);
        }
        int len = (p.InputReportSize ?? 0) > 0 ? p.InputReportSize!.Value
            : (int)s_oldCompute!.Invoke(null, new object[] { d })!;
        return (OldDriverScan(d), len);
    }

    /// <summary>driver.c's test on InputReportId: 5 bytes and the
    /// descriptor, the length at bytes 1 to 4 equal to
    /// InputReportByteLength, the descriptor the one in use. The ID, or
    /// null when the driver ignores the value.</summary>
    static int? DriverTakes(byte[]? value, int inputReportByteLength, byte[] descriptor)
    {
        if (value == null || value.Length != 5 + descriptor.Length) return null;
        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(1, 4)) != inputReportByteLength) return null;
        if (!value.AsSpan(5).SequenceEqual(descriptor)) return null;
        return value[0];
    }

    static int OldDriverScan(byte[] d)
    {
        int depth = 0, ri = 0;
        bool inFirst = false;
        while (ri < d.Length - 1)
        {
            byte prefix = d[ri];
            int size = (prefix & 3) == 3 ? 4 : prefix & 3;
            int type = (prefix >> 2) & 3, tag = prefix >> 4;
            if (type == 0 && tag == 10 && ++depth == 1) inFirst = true;
            if (type == 0 && tag == 12 && --depth == 0 && inFirst) break;
            if (inFirst && prefix == 0x85 && (ri == 0 || d[ri - 1] != 0x09)) return d[ri + 1];
            ri += 1 + size;
        }
        return 0;
    }

    /// <summary>The report a data-only frame fills: a keyboard or mouse
    /// that gets every report delivered sends its profile's dataReportId,
    /// else the descriptor's first input report, and everything else the
    /// report HidReportBuilder lays out (HMController.SubmitRawReport and
    /// SubmitState).</summary>
    static int FilledReport(ControllerProfile p)
    {
        var kbm = p.GetOrBuildKeyboardMousePacker();
        bool deliverEvery = (kbm.HasMouse || kbm.HasKeyboard || kbm.HasConsumer)
            && (p.IsKeyboardOrMouse
                || (string.Equals(p.Type, "other", StringComparison.OrdinalIgnoreCase) && (kbm.HasMouse || kbm.HasKeyboard)));
        string? dataReportId = typeof(ControllerProfile).GetProperty("DataReportId")?.GetValue(p) as string;
        if (deliverEvery)
            return dataReportId != null ? Convert.ToByte(dataReportId, 16) : kbm.FirstInputReportId;
        return p.GetOrBuildReportBuilder().InputReportId;
    }

    /// <summary>The driver serves the Switch Pro's input through its own
    /// protocol stream, keyed on VID 0x057E and PID 0x2009 (driver.c
    /// ReadConfigFromRegistry), and returns from ProcessSharedInput before
    /// the data lane, so no data-only frame of that profile goes out.</summary>
    static bool DriverStreamsItself(ControllerProfile p) => p.VendorId == 0x057E && p.ProductId == 0x2009;

    static ControllerProfile Synthetic(string hex, string type = "gamepad", int? inputReportSize = null,
                                       string? dataReportId = null)
    {
        var p = new ControllerProfile
        {
            Id = "synthetic", Name = "synthetic", Vid = "0x1209", Pid = "0x0001", Type = type,
            Descriptor = hex.Replace(" ", ""), InputReportSize = inputReportSize,
        };
        if (dataReportId != null) SetDataReportId(p, dataReportId);
        return p;
    }

    /// <summary>The profile's dataReportId, by reflection so the probe
    /// builds against an SDK from before it. False when the SDK has
    /// none.</summary>
    static bool SetDataReportId(ControllerProfile p, string value)
    {
        var prop = typeof(ControllerProfile).GetProperty("DataReportId");
        if (prop == null) return false;
        prop.SetValue(p, value);
        return true;
    }

    // ── 1 to 3: the catalog ──────────────────────────────────────────────

    sealed record RefEntry(string Profile, string Source, string Sha256,
        Dictionary<int, int> Input, Dictionary<int, int> Output, Dictionary<int, int> Feature);

    static List<RefEntry> LoadReference(out string hidTools)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference_lengths.json")));
        hidTools = doc.RootElement.GetProperty("hidTools").GetString()!;
        static Dictionary<int, int> Kind(JsonElement e, string k)
        {
            var o = new Dictionary<int, int>();
            if (e.TryGetProperty(k, out var v))
                foreach (var p in v.EnumerateObject()) o[int.Parse(p.Name)] = p.Value.GetInt32();
            return o;
        }
        return doc.RootElement.GetProperty("descriptors").EnumerateArray()
            .Select(e => new RefEntry(e.GetProperty("profile").GetString()!, e.GetProperty("source").GetString()!,
                e.GetProperty("sha256").GetString()!, Kind(e, "input"), Kind(e, "output"), Kind(e, "feature")))
            .ToList();
    }

    static IEnumerable<(string Source, byte[] Bytes)> Sources(ControllerProfile p)
    {
        if (p.GetDescriptorBytes() is { Length: > 0 } d) yield return ("descriptor", d);
        foreach (var iface in p.UsbConfiguration?.Interfaces ?? new())
            foreach (var alt in iface.AltSettings)
                if (!string.IsNullOrEmpty(alt.ReportDescriptor))
                    yield return ($"interface{iface.InterfaceNumber}", Convert.FromHexString(alt.ReportDescriptor));
    }

    static void Catalog()
    {
        Console.WriteLine("\n--- the catalog against hid-tools ---");
        var reference = LoadReference(out string hidTools);
        var byKey = reference.ToDictionary(e => (e.Profile, e.Source));
        var catalog = ProfileDatabase.LoadEmbedded().All;
        Console.WriteLine($"  {catalog.Count} catalog profiles, {reference.Count} reference descriptors from hid-tools {hidTools}");

        var seen = new HashSet<(string, string)>();
        var missing = new List<string>();
        var stale = new List<string>();
        var mapDiff = new List<string>();
        var builderDiff = new List<string>();
        int compared = 0;
        foreach (var p in catalog)
            foreach (var (source, d) in Sources(p))
            {
                seen.Add((p.Id, source));
                if (!byKey.TryGetValue((p.Id, source), out var r)) { missing.Add($"{p.Id} {source}"); continue; }
                if (Convert.ToHexString(SHA256.HashData(d)) != r.Sha256) { stale.Add($"{p.Id} {source}"); continue; }
                compared++;
                foreach (var (kind, want) in new[] { (HidReportKind.Input, r.Input), (HidReportKind.Output, r.Output), (HidReportKind.Feature, r.Feature) })
                {
                    var got = MapLengths(d, kind);
                    if (Lengths(got) != Lengths(want))
                        mapDiff.Add($"{p.Id} {source} {kind}: {Lengths(got)}, reference {Lengths(want)}");
                }
                var bi = BuilderLengths(d);
                if (Lengths(bi) != Lengths(r.Input))
                    builderDiff.Add($"{p.Id} {source}: {Lengths(bi)}, reference {Lengths(r.Input)}");
            }
        var extra = reference.Where(e => !seen.Contains((e.Profile, e.Source))).Select(e => $"{e.Profile} {e.Source}").ToList();
        Check("every catalog report descriptor has a reference entry", missing.Count == 0, string.Join("; ", missing));
        Check("every reference entry is a catalog descriptor", extra.Count == 0, string.Join("; ", extra));
        Check("every reference entry holds the catalog's bytes", stale.Count == 0, string.Join("; ", stale));
        Check($"HidReportMap: input, output and feature lengths equal the reference on {compared} descriptors",
            compared > 0 && mapDiff.Count == 0, string.Join("; ", mapDiff));
        Check($"HidReportBuilder: input lengths equal the reference on {compared} descriptors",
            compared > 0 && builderDiff.Count == 0, string.Join("; ", builderDiff));

        // The report the driver sends, for every profile that runs on it.
        var idDiff = new List<string>();
        var lenDiff = new List<string>();
        var overrides = new List<string>();
        var unvalued = new List<string>();
        var pairDiff = new List<string>();
        int drivers = 0;
        foreach (var p in catalog)
        {
            if (p.RequiresUsbipBackend || p.GetDescriptorBytes() is not { Length: > 0 } d) continue;
            if (!byKey.TryGetValue((p.Id, "descriptor"), out var r)) continue;
            drivers++;
            var (id, len) = DriverReport(p);
            int filled = FilledReport(p);
            bool valued = IdValue(p) != null;
            if (!valued) unvalued.Add($"{p.Id} 0x{id:X2}/{len}");
            if ((id != filled && !DriverStreamsItself(p)) || !r.Input.ContainsKey(id))
                idDiff.Add($"{p.Id}: 0x{id:X2}, the SDK fills 0x{filled:X2}, inputs {Lengths(r.Input)}");
            // A value goes with the report's own length, never another's.
            if (valued && (!r.Input.TryGetValue(id, out int own) || own != len))
                pairDiff.Add($"{p.Id}: 0x{id:X2} at {len}");
            if (!r.Input.TryGetValue(id, out int want)) continue;
            if ((p.InputReportSize ?? 0) > 0)
            {
                int max = r.Input.Values.Max();
                overrides.Add($"{p.Id} {len}");
                if (len < want || len > max)
                    lenDiff.Add($"{p.Id}: inputReportSize {len}, report 0x{id:X2} is {want}, the largest input report {max}");
            }
            else if (len != want)
                lenDiff.Add($"{p.Id}: {len}, report 0x{id:X2} is {want}");
        }
        Check($"the driver's report is the one the SDK fills, and an input report, on {drivers} profiles",
            drivers > 0 && idDiff.Count == 0, string.Join("; ", idDiff));
        Check($"the driver's length is that report's on {drivers} profiles, an inputReportSize no shorter and no longer than the largest input report",
            drivers > 0 && lenDiff.Count == 0, string.Join("; ", lenDiff));
        Check($"InputReportId pairs each ID with that report's own length on {drivers} profiles",
            pairDiff.Count == 0, string.Join("; ", pairDiff));
        Console.WriteLine($"  [INFO] profiles that set inputReportSize: {string.Join(", ", overrides)}");
        Console.WriteLine($"  [INFO] no InputReportId, so the driver's own scan names the report: {string.Join(", ", unvalued)}");
    }

    // ── 4: descriptors made by hand ──────────────────────────────────────

    static void HandMade()
    {
        Console.WriteLine("\n--- descriptors made by hand ---");

        // Pop restores Report Size 8, Report Count 1 and the Generic Desktop
        // page, so Y is one 8-bit field after the three 1-bit buttons: 8 + 3
        // + 8 + 5 bits.
        const string pushPop =
            "05 01 09 04 A1 01 15 00 26 FF 00 75 08 95 01 09 30 81 02" +
            " A4 25 01 75 01 95 03 05 09 19 01 29 03 81 02 B4" +
            " 09 31 81 02 75 05 81 03 C0";
        var b = HidReportBuilder.Parse(H(pushPop));
        var y = b.InputFields.FirstOrDefault(f => f.UsagePage == 0x01 && f.Usage == 0x31);
        Check("Pop restores Report Size, Report Count, Logical Maximum and the page: 3 bytes, Y 8 bits at bit 11",
            b.InputReportByteSize == 3 && y is { BitOffset: 11, BitSize: 8, LogicalMax: 255 }
            && Lengths(MapLengths(H(pushPop), HidReportKind.Input)) == "0=3",
            $"builder {b.InputReportByteSize} bytes, Y {(y == null ? "missing" : $"bit {y.BitOffset} size {y.BitSize} max {y.LogicalMax}")}");
        var (pid, plen) = DriverReport(Synthetic(pushPop));
        Check("the driver's report for it is report 0 of 3 bytes", pid == 0 && plen == 3, $"0x{pid:X2} {plen}");

        // A report ID set after a Push is the pushed state's, and Pop puts
        // report 1 back with its Report Count of 2 (hid-core.c
        // hid_parser_global keeps report_id in the global state). Input 1:
        // 16 + 16 bits and the ID byte. Feature 2: 4 bytes and the ID byte.
        const string idInPush =
            "05 01 09 05 A1 01 85 01 15 00 26 FF 00 75 08 95 02 09 30 09 31 81 02" +
            " A4 85 02 95 04 06 00 FF 09 01 B1 02 B4" +
            " 09 32 81 02 C0";
        var d = H(idInPush);
        Check("Pop restores the report ID: input 1 is 5 bytes, feature 2 is 5 bytes, no input 2",
            Lengths(MapLengths(d, HidReportKind.Input)) == "1=5" && Lengths(MapLengths(d, HidReportKind.Feature)) == "2=5"
            && Lengths(BuilderLengths(d)) == "1=5",
            $"map input {Lengths(MapLengths(d, HidReportKind.Input))}, feature {Lengths(MapLengths(d, HidReportKind.Feature))}, builder {Lengths(BuilderLengths(d))}");
        var z = HidReportBuilder.Parse(d).InputFields.FirstOrDefault(f => f.UsagePage == 0x01 && f.Usage == 0x32);
        Check("after that Pop, Z is in report 1 at bit 16 with the Generic Desktop page", z is { BitOffset: 16, BitSize: 8 },
            z == null ? "missing" : $"bit {z.BitOffset}");
        (pid, plen) = DriverReport(Synthetic(idInPush));
        Check("the driver's report for it is input 1 of 5 bytes", pid == 1 && plen == 5, $"0x{pid:X2} {plen}");

        // Two pushes deep: 2 + 4 + 8 bits, padded to 2 bytes.
        const string nested = "05 01 09 05 A1 01 75 08 95 01 A4 75 04 A4 75 02 81 03 B4 81 03 B4 81 03 C0";
        Check("nested Push and Pop: 2 bytes", Lengths(MapLengths(H(nested), HidReportKind.Input)) == "0=2"
            && Lengths(BuilderLengths(H(nested))) == "0=2", Lengths(BuilderLengths(H(nested))));

        // A long item (HID 1.11, 6.2.2.3) is 0xFE, its data size, its tag
        // and that many bytes, all skipped. X and Y follow it.
        const string longItem = "05 01 09 05 A1 01 15 00 26 FF 00 75 08 95 02 09 30 09 31 FE 04 F0 11 22 33 44 81 02 C0";
        var lb = HidReportBuilder.Parse(H(longItem));
        Check("a long item is skipped whole: 2 bytes, X and Y", lb.InputReportByteSize == 2
            && lb.LeftStickX != null && lb.LeftStickY != null && Lengths(MapLengths(H(longItem), HidReportKind.Input)) == "0=2",
            $"{lb.InputReportByteSize} bytes");

        // Signed X, then a pushed field with its own range, Physical
        // range, Unit and Unit Exponent, then Y with X's range again.
        const string signed =
            "05 01 09 05 A1 01 15 81 25 7F 75 08 95 01 09 30 81 02" +
            " A4 15 00 26 FF 00 35 00 46 68 01 66 14 00 55 0E 09 32 81 02 B4" +
            " 09 31 81 02 C0";
        var sb = HidReportBuilder.Parse(H(signed));
        Check("signed ranges, and Pop restores them: X and Y [-127,127], Z [0,255], 3 bytes",
            sb.InputReportByteSize == 3
            && sb.InputFields.Count(f => f.UsagePage == 1 && (f.Usage == 0x30 || f.Usage == 0x31) && f.LogicalMin == -127 && f.LogicalMax == 127) == 2
            && sb.InputFields.Any(f => f.UsagePage == 1 && f.Usage == 0x32 && f.LogicalMin == 0 && f.LogicalMax == 255),
            string.Join(" ", sb.InputFields.Select(f => $"{f.UsagePage:X}:{f.Usage:X}[{f.LogicalMin},{f.LogicalMax}]")));

        // A 4-byte Report Count of 0x80000000 reads negative as an int.
        // The builder parses it without throwing, as it always has.
        const string hugeCount = "05 01 09 05 A1 01 75 08 97 00 00 00 80 81 03 C0";
        bool parsed;
        try { HidReportBuilder.Parse(H(hugeCount)); parsed = true; } catch { parsed = false; }
        Check("a Report Count past int.MaxValue parses", parsed);

        // A 4-byte usage carries its own page (HID 1.11, 6.2.2.8) and leaves
        // the global Usage Page alone: Accelerator stays on Simulation.
        const string extended =
            "05 01 09 05 A1 01 05 02 15 00 26 FF 00 75 08 95 01 0B 30 00 01 00 81 02 09 C4 81 02 C0";
        var eb = HidReportBuilder.Parse(H(extended));
        Check("a 4-byte usage is X on Generic Desktop, and the next usage stays on Simulation",
            eb.InputFields.Count == 2 && eb.InputFields[0] is { UsagePage: 0x01, Usage: 0x30 } && eb.InputFields[1] is { UsagePage: 0x02, Usage: 0xC4 },
            string.Join(" ", eb.InputFields.Select(f => $"{f.UsagePage:X}:{f.Usage:X}")));

        // End Collection is a main item, so the X declared before it is
        // gone by the next Input (HID 1.11, 6.2.2.8, hid_parser_main).
        const string endCollection = "05 01 09 05 A1 01 A1 00 09 30 C0 15 00 26 FF 00 75 08 95 01 81 02 C0";
        var cb = HidReportBuilder.Parse(H(endCollection));
        Check("End Collection clears the usage before it", cb.LeftStickX == null && cb.InputFields.Count == 1 && cb.InputFields[0].Usage == 0,
            string.Join(" ", cb.InputFields.Select(f => $"{f.UsagePage:X}:{f.Usage:X}")));

        // The first Report ID item is an output report's. The driver sends
        // input 0x21, 3 bytes and the ID byte.
        const string outputFirst =
            "05 01 09 05 A1 01 85 11 15 00 26 FF 00 75 08 95 05 09 01 91 02" +
            " 85 21 95 03 09 30 09 31 09 32 81 02 C0";
        (pid, plen) = DriverReport(Synthetic(outputFirst));
        Check("an output report first: the driver sends input 0x21 of 4 bytes", pid == 0x21 && plen == 4, $"0x{pid:X2} {plen}");

        // A keyboard sends its first input report on the driver, even when
        // HidReportBuilder lays out a later one: here the mouse report, the
        // first with Button page fields.
        var keyboard = Synthetic(Convert.ToHexString(
            new HidDescriptorBuilder().Keyboard(reportId: 1).Mouse(reportId: 2).Build()), type: "keyboard");
        (pid, plen) = DriverReport(keyboard);
        int kbLen = HidReportMap.Parse(keyboard.GetDescriptorBytes()!).Find(HidReportKind.Input, 1)!.FrameBytes;
        Check("a keyboard: the driver sends its keyboard report, not the mouse report HidReportBuilder lays out",
            pid == 1 && plen == kbLen && keyboard.GetOrBuildReportBuilder().InputReportId == 2,
            $"0x{pid:X2} {plen}, report 1 is {kbLen}, builder 0x{keyboard.GetOrBuildReportBuilder().InputReportId:X2}");
    }

    // ── 4b: the value the driver reads, and dataReportId ─────────────────

    static void DriverValue()
    {
        Console.WriteLine("\n--- InputReportId and dataReportId ---");
        var catalog = ProfileDatabase.LoadEmbedded().All;
        ControllerProfile Get(string id) => catalog.First(x => x.Id == id);

        // genesis-nso fills 0x3F, 12 bytes. The value is that ID, that
        // length and the descriptor. A v1.12.0 program writes the same
        // descriptor with InputReportByteLength 49 (its count of 0x21) and
        // no InputReportId, so a value a killed program left beside it is
        // ignored, and 0x3F never goes out at 49 bytes.
        var nso = Get("genesis-nso");
        byte[]? v = IdValue(nso);
        byte[] d = nso.GetDescriptorBytes()!;
        Check("genesis-nso's InputReportId is 0x3F, 12 and its descriptor",
            v != null && v.Length == 5 + d.Length && v[0] == 0x3F
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(1, 4)) == 12 && v.AsSpan(5).SequenceEqual(d),
            v == null ? "none" : Convert.ToHexString(v.AsSpan(0, 5)));
        Check("beside a v1.12.0 program's length of 49 the driver ignores it",
            v != null && DriverTakes(v, 12, d) == 0x3F && DriverTakes(v, 49, d) == null);
        var changed = (byte[])d.Clone();
        changed[^1] ^= 0xFF;
        Check("beside another descriptor of the same length the driver ignores it",
            v != null && DriverTakes(v, 12, changed) == null);

        // switch-pro names 0x31's 362 bytes for the 12-byte 0x3F it fills:
        // no value, so no 0x3F at 362.
        Check("switch-pro, whose inputReportSize is another report's, gets no InputReportId",
            s_inputReportIdValue != null && IdValue(Get("switch-pro")) == null);

        // microsoft-designer-keyboard names its vendor report 0x27 first,
        // and the keyboard sends its keys as 0x01 (dataReportId).
        var designer = Get("microsoft-designer-keyboard");
        var (kid, klen) = DriverReport(designer);
        bool valid;
        try { designer.ValidateDeviceModel(); valid = true; } catch (InvalidOperationException) { valid = false; }
        Check("microsoft-designer-keyboard's data frames go out as its keyboard report 0x01, 12 bytes",
            kid == 0x01 && klen == 12 && valid, $"0x{kid:X2} {klen}, the profile {(valid ? "validates" : "is refused")}");

        // A game pad whose descriptor lays out report 1 first: dataReportId
        // 0x02 moves the fill and the driver's report to report 2.
        const string twoReports =
            "05 01 09 05 A1 01 85 01 05 09 19 01 29 08 15 00 25 01 75 01 95 08 81 02" +
            " 85 02 05 01 09 30 09 31 26 FF 00 75 08 95 02 81 02 C0";
        var pad = Synthetic(twoReports, dataReportId: "0x02");
        var (pid2, plen2) = DriverReport(pad);
        Check("a dataReportId moves a game pad's fill and its driver report to that report",
            pad.GetOrBuildReportBuilder().InputReportId == 2 && pid2 == 2 && plen2 == 3,
            $"builder 0x{pad.GetOrBuildReportBuilder().InputReportId:X2}, driver 0x{pid2:X2} {plen2}");

        // Beside an always-armed extendedReport the data frames fill that
        // report, so a dataReportId would be ignored: refused, naming both.
        var armed = Synthetic(twoReports, dataReportId: "0x02");
        armed.ExtendedReport = new ExtendedReportSpec { ReportId = "0x01", Size = 2, AlwaysArmed = true };
        string why = "";
        try { armed.ValidateDeviceModel(); }
        catch (InvalidOperationException e) { why = e.Message; }
        Check("a dataReportId beside an always-armed extendedReport is refused",
            why.Contains("dataReportId") && why.Contains("extendedReport.alwaysArmed"), why);

        // HMProfileBuilder.FromProfile keeps it.
        var copy = new HMProfileBuilder().FromProfile(new HMProfile(designer)).Build().Inner;
        var (cid, clen) = DriverReport(copy);
        Check("HMProfileBuilder.FromProfile keeps dataReportId: the copy's frames go out as 0x01 at 12",
            cid == 0x01 && clen == 12, $"0x{cid:X2} {clen}");

        // One the descriptor does not declare as input is refused.
        var bad = Synthetic(twoReports, dataReportId: "0x05");
        bool refused;
        try { bad.ValidateDeviceModel(); refused = false; }
        catch (InvalidOperationException) { refused = true; }
        Check("a dataReportId that names no input report is refused", refused && SetDataReportId(bad, "0x05"));
    }

    // ── 5: the other walkers ─────────────────────────────────────────────

    static void OtherWalkers()
    {
        Console.WriteLine("\n--- the other walkers ---");

        // Pool's report ID is set inside a Push, so State, after the Pop,
        // is report 1's.
        const string pid =
            "05 01 09 04 A1 01 85 01 15 00 26 FF 00 75 08 95 01 09 30 81 02" +
            " 05 0F 09 7F A1 02 A4 85 03 09 80 B1 02 B4 C0" +
            " 09 92 A1 02 09 9F B1 02 C0 C0";
        var ids = PidReportIdExtractor.Extract(H(pid));
        Check("PidReportIdExtractor: Pool 0x03 inside a Push, State back on 0x01 after the Pop",
            ids.PoolReportId == 0x03 && ids.StateReportId == 0x01, ids.ToString());

        // A vendor-page logical collection with usage 0x21 is no Set Effect
        // Report: that usage means one only on the PID page (0x0F).
        const string vendor =
            "05 01 09 04 A1 01 85 01 15 00 26 FF 00 75 08 95 01 09 30 81 02" +
            " 06 00 FF 09 21 A1 02 85 05 09 01 91 02 C0 C0";
        ids = PidReportIdExtractor.Extract(H(vendor));
        Check("PidReportIdExtractor: a vendor collection with usage 0x21 leaves Create New Effect at 0x11",
            ids.CreateNewEffectReportId == 0x11 && !ids.AnyOverride, ids.ToString());

        // The shape the GameInput table was measured on, with a Push, a
        // Button page and a Pop between Z+Rz and Rx: Rx and Ry are still
        // Generic Desktop values.
        const string shaped =
            "05 01 09 05 A1 01 09 30 09 31 15 00 27 FF FF 00 00 95 02 75 10 81 02" +
            " 09 32 09 35 81 02 A4 05 09 B4 09 33 95 01 75 08 26 FF 00 81 02 09 34 81 02" +
            " 09 39 15 01 25 08 75 04 95 01 81 42 75 04 81 03" +
            " 05 09 19 01 29 0A 15 00 25 01 75 01 95 0A 81 02 75 06 95 01 81 03 C0";
        Check("GameInputMapping: a Push and Pop around a page change keeps the measured shape",
            GameInputMapping.HasMeasuredShape(H(shaped)));

        // AddRaw with a Push, a Usage Page and a Pop leaves the Button page
        // in force, so the stick after it must name Generic Desktop again.
        var built = new HidDescriptorBuilder().Gamepad().AddButtons(4)
            .AddRaw(new byte[] { 0xA4, 0x05, 0x01, 0xB4 }).AddStick("Left").Build();
        var stick = HidReportMap.Parse(built).Find(HidReportKind.Input, 0)!.Fields
            .FirstOrDefault(f => !f.Constant && f.Count == 2);
        Check("HidDescriptorBuilder: a stick after AddRaw's Push and Pop is Generic Desktop X and Y",
            stick != null && stick.UsageAt(0) == 0x00010030 && stick.UsageAt(1) == 0x00010031,
            Convert.ToHexString(built));
    }

    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "live") return Live.Run();
        Console.WriteLine("=== Report lengths against an independent parser ===");
        Catalog();
        HandMade();
        DriverValue();
        OtherWalkers();
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
