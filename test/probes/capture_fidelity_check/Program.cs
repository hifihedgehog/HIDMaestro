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
//
// Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
}
