// capture_fidelity_check: catalog data and driver text against the
// captures and sources they come from. No device, no elevation.
//
//   inf        Every comment line of the two driver INFs is plain ASCII.
//              Ten lines carried a dash written as UTF-8 and read back as
//              Windows-1252, three stray characters in place of each dash.
//              Needs the source tree beside the exe, and is skipped with
//              a [SKIP] line when it is not there.
//   strings    The ten Xbox 360 profiles carry the manufacturer string a real
//              wired pad serves, string 1 of ControllersInfo's
//              "DescriptorDump_Controller (Xbox 360 Wired Model
//              X854237-001).txt":188 at bfc11b8, "\u00A9Microsoft
//              Corporation". Nine carried it read as Windows-1252 once or
//              twice. No catalog string carries that damage: a U+00C2 or
//              U+00C3 followed by a character from U+0080 to U+00BF, which
//              is a UTF-8 lead and continuation byte read one by one.
//   descriptors  A profile whose notes name a captured descriptor carries
//              that capture byte for byte, and the SDK reads from it the
//              reports, sizes and top-level usage the capture declares,
//              each worked out by hand from the capture's items.
//   psmove     ps-move's input report, written whole from its extendedReport,
//              puts each control where psmoveapi and the moveonpc wiki read
//              a real Move ZCM1's: the T trigger at bytes 5 and 6 with the T
//              bit, the buttons in bytes 1 to 4, 0x7F in bytes 7 to 10, bytes
//              11 and 37 to 48 zero, the battery 0xEE or 0xEF as on USB,
//              and motion in 13 to 36 (PsMoveSources.cs).
//   answers, dumps  in PsMoveSources.cs.
//
// Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int s_total, s_failures;

    internal static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== capture_fidelity_check: catalog data and driver text against their sources ===");
        InfText();
        CatalogStrings();
        CapturedDescriptors();
        PsMoveReport();
        PsMoveAnswers();
        DescriptorDumps();
        Console.WriteLine();
        Console.WriteLine($"capture_fidelity_check: {(s_failures == 0 ? "PASS" : "FAIL")}, {s_total - s_failures} of {s_total} checks pass");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The repository this probe was built from, or null when the
    /// exe runs outside it, as on a staged machine.</summary>
    static string? RepoRoot()
    {
        string? probe = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ProbeDir")?.Value;
        if (probe == null) return null;
        string root = Path.GetFullPath(Path.Combine(probe, "..", "..", ".."));
        return File.Exists(Path.Combine(root, "driver", "hidmaestro.inf")) ? root : null;
    }

    // ── inf ──────────────────────────────────────────────────────────────

    static void InfText()
    {
        Console.WriteLine();
        Console.WriteLine("--- inf: driver INF comment lines are plain ASCII ---");
        string? root = RepoRoot();
        if (root == null)
        {
            Console.WriteLine("  [SKIP] the source tree is not beside this exe, so there are no INF sources to read.");
            return;
        }
        foreach (string name in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "driver", name));
            // The UTF-8 byte order mark the INF starts with is not text.
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text = Encoding.Latin1.GetString(bytes, start, bytes.Length - start);
            string[] lines = text.Split('\n');
            var bad = new List<int>();
            int comments = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith(';')) continue;
                comments++;
                if (lines[i].Any(c => c > 0x7E || (c < 0x20 && c != '\t' && c != '\r'))) bad.Add(i + 1);
            }
            Check($"{name}: {comments} comment lines, none outside printable ASCII", bad.Count == 0,
                  bad.Count == 0 ? "" : "lines " + string.Join(", ", bad));
        }
    }

    // ── strings ──────────────────────────────────────────────────────────

    static readonly string[] Xbox360Ids =
    {
        "xbox-360-arcade-stick", "xbox-360-dance-pad", "xbox-360-flight-stick", "xbox-360-guitar-v1",
        "xbox-360-guitar-v2", "xbox-360-type2", "xbox-360-wheel-v1", "xbox-360-wheel-v2",
        "xbox-360-wired", "xbox-360-wireless",
    };

    static void CatalogStrings()
    {
        Console.WriteLine();
        Console.WriteLine("--- strings: catalog strings as the devices serve them ---");
        var db = ProfileDatabase.LoadEmbedded();
        const string Real = "\u00A9Microsoft Corporation";
        foreach (string id in Xbox360Ids)
        {
            var p = db.All.FirstOrDefault(x => x.Id == id);
            string? m = p?.ManufacturerString;
            Check($"{id}: manufacturer string is the real pad's", m == Real,
                  m == null ? "profile or string missing" : $"has {Escaped(m)}");
        }
        var damaged = new List<string>();
        foreach (var p in db.All)
            foreach (var (key, value) in new[] { ("name", p.Name), ("productString", p.ProductString),
                                                 ("manufacturerString", p.ManufacturerString) })
                if (value != null && ReadAsLatin1(value)) damaged.Add($"{p.Id} {key} {Escaped(value)}");
        Check($"no string of the {db.All.Count} catalog profiles is UTF-8 read as single bytes", damaged.Count == 0,
              string.Join("; ", damaged));
    }

    static bool ReadAsLatin1(string s)
    {
        for (int i = 0; i + 1 < s.Length; i++)
            if ((s[i] == '\u00C2' || s[i] == '\u00C3') && s[i + 1] >= '\u0080' && s[i + 1] <= '\u00BF')
                return true;
        return false;
    }

    static string Escaped(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s) sb.Append(c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}");
        return sb.Append('"').ToString();
    }

    // ── descriptors ──────────────────────────────────────────────────────

    /// <summary>A captured descriptor: the profile that carries it, the
    /// bytes, where they come from, the top-level usage, and every report
    /// as kind, ID and length with the ID byte.</summary>
    sealed record Capture(string Profile, string Hex, string Source, ushort Page, ushort Usage,
                          (HidReportKind Kind, byte Id, int Bytes)[] Reports);

    static readonly Capture[] Captures =
    {
        // ControllersInfo bfc11b8 xbox360/xusb_flightstick_hid_report_descriptor.bin,
        // 127 bytes, the descriptor the XUSB driver presents for a flight
        // stick: Joystick, 8-bit Z and Rz, 16-bit X and Y, 16-bit Rx and
        // Ry, 10 buttons, a 4-bit hat and 2 bits of padding, no report ID.
        // 1 + 1 + 4 + 4 bytes, then 16 bits: 12 bytes.
        new("xbox-360-flight-stick",
            "05010904a10105010932150026ff0095017508810205010935150026ff0095017508810205010901a10009300931150026ffff350046ffff950275108102c0a10009330934150026ffff350046ffff950275108102c005091901290a950a7501810205010939150125083500463b10660e00750495018142750295018103c0",
            "ControllersInfo bfc11b8 xbox360/xusb_flightstick_hid_report_descriptor.bin", 0x01, 0x04,
            new[] { (HidReportKind.Input, (byte)0, 12) }),
        // The descriptor a real Move (ZCM1, 054C:03D5) serves over USB, read
        // over hidraw (LKML, Pavel Machek, 2015-03-14, "Re: Fun with sony
        // motion controllers: motion controller",
        // lkml.rescloud.iu.edu/1503.1/05461.html), 178 bytes. Game Pad.
        // Input 0x01: 24 one-bit buttons, 3 vendor bytes, X Y Z Rz at 8
        // bits, 0x26 vendor bytes: 48 bytes. Output 0x02 (0x30 bytes) and
        // 0x06 (8). Features 0x03 (8), 0x10, 0xE0 and 0x11 (0x30 each),
        // then in two vendor collections 0x04 (0x0F), 0x05 (0x16), 0xA0
        // (0x22) and 0xA1 (0x16). Each length here adds the ID byte.
        new("ps-move",
            "05010905a101850105091901291815002501350045017501951881020600ff092475089503810205010930093109320935150026ff00950481020600ff09209526810285020921953091028506092795089102850309239508b102851009229530b10285e009259530b102851109269530b102c00602ff0920a10109218504150026ff007508950fb102092285059516b102c00603ff0920a101092185a0150026ff0075089522b102092285a19516b102c0",
            "LKML 2015-03-14, a real Move's descriptor over hidraw", 0x01, 0x05,
            new[]
            {
                (HidReportKind.Input, (byte)0x01, 49), (HidReportKind.Output, (byte)0x02, 49), (HidReportKind.Output, (byte)0x06, 9),
                (HidReportKind.Feature, (byte)0x03, 9), (HidReportKind.Feature, (byte)0x04, 16), (HidReportKind.Feature, (byte)0x05, 23),
                (HidReportKind.Feature, (byte)0x10, 49), (HidReportKind.Feature, (byte)0x11, 49), (HidReportKind.Feature, (byte)0xA0, 35),
                (HidReportKind.Feature, (byte)0xA1, 23), (HidReportKind.Feature, (byte)0xE0, 49),
            }),
    };

    static void CapturedDescriptors()
    {
        Console.WriteLine();
        Console.WriteLine("--- descriptors: profiles carry the descriptors their sources capture ---");
        var db = ProfileDatabase.LoadEmbedded();
        foreach (var c in Captures)
        {
            var p = db.All.FirstOrDefault(x => x.Id == c.Profile);
            byte[]? d = p?.GetDescriptorBytes();
            byte[] want = Convert.FromHexString(c.Hex);
            Check($"{c.Profile}: descriptor is the {want.Length}-byte capture ({c.Source})",
                  d != null && d.AsSpan().SequenceEqual(want),
                  d == null ? "no descriptor" : $"carries {d.Length} bytes");
            if (d == null) continue;
            var map = HidReportMap.Parse(d);
            var top = map.Collections.FirstOrDefault();
            Check($"{c.Profile}: top-level usage {c.Page:X4}:{c.Usage:X4}", top != null && top.Is(c.Page, c.Usage),
                  top == null ? "no collection" : $"reads {top.UsagePage:X4}:{top.Usage:X4}");
            string Table(IEnumerable<(HidReportKind Kind, byte Id, int Bytes)> r) =>
                string.Join(" ", r.OrderBy(x => x.Kind).ThenBy(x => x.Id).Select(x => $"{x.Kind}:{x.Id:X2}:{x.Bytes}"));
            string got = Table(map.Reports.Select(r => (r.Kind, r.ReportId, r.FrameBytes)));
            string expected = Table(c.Reports);
            Check($"{c.Profile}: reports {expected}", got == expected, got == expected ? "" : $"reads {got}");
        }
    }

    // ── psmove ───────────────────────────────────────────────────────────

    /// <summary>ps-move's input report for a state, as the SDK writes it
    /// when its extendedReport is armed: the triggers resolved as
    /// HMController resolves them, then the codec.</summary>
    static byte[]? MoveReport(ControllerProfile p, HMGamepadState s, VendorBlobCodec.EncoderState enc)
    {
        if (p.ExtendedReport is not { AlwaysArmed: true } spec) return null;
        var hp = new HMProfile(p);
        double lt = HMController.ResolveTrigger(s.Axes, hp.Triggers, 0, HMController.ResolveCanonicalAxis(p.AxisMap, "lefttrigger", HMAxis.Z));
        double rt = HMController.ResolveTrigger(s.Axes, hp.Triggers, 1, HMController.ResolveCanonicalAxis(p.AxisMap, "righttrigger", HMAxis.Rz));
        var buf = new byte[spec.Size];
        VendorBlobCodec.EncodeInput(spec, in s, 0.5f, 0.5f, 0.5f, 0.5f, (float)lt, (float)rt, buf, enc);
        return buf;
    }

    static void PsMoveReport()
    {
        Console.WriteLine();
        Console.WriteLine("--- psmove: ps-move's input report where a real Move puts each control ---");
        var p = ProfileDatabase.LoadEmbedded().All.FirstOrDefault(x => x.Id == "ps-move");
        var enc = new VendorBlobCodec.EncoderState();
        byte[]? rest = p == null ? null : MoveReport(p, default, enc);
        Check("ps-move writes its input report 0x01 whole, 49 bytes, from power-on", rest is { Length: 49 } && rest[0] == 0x01,
              rest == null ? "no always-armed extendedReport" : $"{rest.Length} bytes, ID 0x{rest[0]:X2}");
        if (rest == null || rest.Length != 49) return;
        static string Hex(byte[] r, int from, int count) => Convert.ToHexString(r, from, count);

        // At rest: no button, no trigger, 0x7F7F7F7F at 7 to 10 (moveonpc
        // "always 0x7F7F7F7F"), the timestamp's high byte 11 and bytes 37 to
        // 48 (temperature, magnetometer, the timestamp's low byte, EXT data)
        // zero, and the battery 0xEE, charging, the value a real Move on USB
        // sends with 0xEF (moveonpc Input report, Battery level).
        bool restOk = rest[1] == 0 && rest[2] == 0 && rest[3] == 0 && (rest[4] & 0xF0) == 0 && rest[5] == 0 && rest[6] == 0
                      && Hex(rest, 7, 4) == "7F7F7F7F" && rest[11] == 0 && rest[12] == 0xEE
                      && Enumerable.Range(37, 12).All(k => rest[k] == 0);
        Check("at rest: no button or trigger, 0x7F at bytes 7 to 10, battery 0xEE, bytes 11 and 37 to 48 zero", restOk, Hex(rest, 0, 49));

        var axes = new Dictionary<HMAxis, float> { [HMAxis.Z] = 1.0f };
        var pulled = MoveReport(p!, new HMGamepadState { Axes = axes }, enc)!;
        Check("the trigger at 1.0 puts 0xFF at bytes 5 and 6 and the T bit at byte 3 bit 4 and byte 4 bit 7",
              pulled[5] == 0xFF && pulled[6] == 0xFF && (pulled[3] & 0x10) != 0 && (pulled[4] & 0x80) != 0, Hex(pulled, 1, 6));

        var press = new (HMButton Button, string Label, int Byte, int Mask, string Name)[]
        {
            (HMButton.Back, "Back", 1, 0x01, "Select"), (HMButton.Start, "Start", 1, 0x08, "Start"),
            (HMButton.Y, "Y", 2, 0x10, "Triangle"), (HMButton.B, "B", 2, 0x20, "Circle"),
            (HMButton.A, "A", 2, 0x40, "Cross"), (HMButton.X, "X", 2, 0x80, "Square"),
            (HMButton.Guide, "Guide", 3, 0x01, "PS"),
        };
        foreach (var (button, label, at, mask, name) in press)
        {
            var r = MoveReport(p!, new HMGamepadState { Buttons = button }, enc)!;
            bool only = Enumerable.Range(1, 3).All(k => r[k] == (k == at ? mask : 0)) && (r[4] & 0xF0) == 0;
            Check($"HMButton.{label} is {name}: byte {at} = 0x{mask:X2}, nothing else in bytes 1 to 4", only, Hex(r, 1, 4));
        }
        var move = MoveReport(p!, new HMGamepadState { Buttons = HMButton.Misc1 }, enc)!;
        Check("Misc1 is Move: byte 3 bit 3 and byte 4 bit 6 (moveonpc 0x4008)",
              move[1] == 0 && move[2] == 0 && move[3] == 0x08 && (move[4] & 0xF0) == 0x40, Hex(move, 1, 4));

        var seq = Enumerable.Range(0, 17).Select(_ => MoveReport(p!, default, enc)![4] & 0x0F).ToList();
        bool steps = Enumerable.Range(1, seq.Count - 1).All(k => seq[k] == ((seq[k - 1] + 1) & 0x0F));
        Check("byte 4's low nibble is a sequence number that adds 1 a report and wraps at 16", steps, string.Join(",", seq));

        var levels = Enumerable.Range(0, 11).Select(l => MoveReport(p!, new HMGamepadState { BatteryLevel = (byte)l }, enc)![12]).ToArray();
        var charging = MoveReport(p!, new HMGamepadState { BatteryLevel = 5, BatteryCharging = true }, enc)![12];
        var full = MoveReport(p!, new HMGamepadState { BatteryLevel = 10, BatteryCharging = true, BatteryFull = true }, enc)![12];
        Check("battery: 0xEE, charging, at every level and when charging, 0xEF when full, as a real Move on USB sends",
              levels.All(b => b == 0xEE) && charging == 0xEE && full == 0xEF,
              $"{Convert.ToHexString(levels)} {charging:X2} {full:X2}");

        PsMoveMotion(p!, enc);
    }
}
