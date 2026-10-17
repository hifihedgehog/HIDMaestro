// capture_fidelity_check: the PS Move's prepared feature answers, and both
// new descriptors against the dumps they come from.
//
//   answers    ps-move-feature-answers.json, the table prepared for lane
//              DD-2's answer tables: 0x10's three pages are the moveonpc
//              wiki's sample blocks and reassemble to one of its eight
//              dumps, and 0x04 has the address layout the wiki gives.
//   dumps      each descriptor equals the dump its notes cite: the LKML
//              message's hidraw printout for ps-move, as printed, and
//              ControllersInfo's .bin for the flight stick, read from the
//              clone beside the repository ([SKIP] without it).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    // ── answers ──────────────────────────────────────────────────────────

    /// <summary>The moveonpc wiki's sample calibration, as the page prints
    /// it (Calibration data, lines 10-25 at 033ab8e).</summary>
    const string WikiCalibrationSample = @"10 00 46 07 73 7f 9f 7f e8 8f 85 6e ca 7f f4 7e
55 7f f1 7f 1a 6e 98 90 0a 80 5b 7f 67 7f c9 90
4c 7f b7 7f e6 6e 06 7f 87 08 a5 7f 06 80 80 81
76

10 01 07 a8 7f 00 80 8b 81 00 00 00 00 00 00 00
01 00 01 92 08 e0 01 e5 94 e6 7f a1 81 e0 01 4b
7f d6 91 59 81 e0 01 a1 7f 2c 80 a6 95 89 07 36
0c

10 82 50 c3 5b c8 fb 42 68 3d b5 42 cd 13 7d 3f
93 5b 81 3f 70 ea 76 3f ff 80 4f 3f c6 ea 9c 3d
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00";

    /// <summary>The second of the wiki's eight calibration dumps, the
    /// reassembled blob, as printed (Calibration data, "Calibration data"
    /// section).</summary>
    const string WikiCalibrationDump = @"10 00 46 07 73 7f 9f 7f e8 8f 85 6e ca 7f f4 7e
55 7f f1 7f 1a 6e 98 90 0a 80 5b 7f 67 7f c9 90
4c 7f b7 7f e6 6e 06 7f 87 08 a5 7f 06 80 80 81
76 07 a8 7f 00 80 8b 81 00 00 00 00 00 00 00 01
00 01 92 08 e0 01 e5 94 e6 7f a1 81 e0 01 4b 7f
d6 91 59 81 e0 01 a1 7f 2c 80 a6 95 89 07 36 0c
50 c3 5b c8 fb 42 68 3d b5 42 cd 13 7d 3f 93 5b
81 3f 70 ea 76 3f ff 80 4f 3f c6 ea 9c 3d 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00";

    static byte[] Printed(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();

    sealed record Answers(List<byte[]> Pages, byte[] Address, int AddressSize, string? PatchSource, int PatchAt);

    static Answers? FeatureAnswers()
    {
        string? probe = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ProbeDir")?.Value;
        string path = Path.Combine(probe ?? AppContext.BaseDirectory, "ps-move-feature-answers.json");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "ps-move-feature-answers.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var reports = doc.RootElement.GetProperty("featureStubs").GetProperty("reports").EnumerateArray().ToList();
        var cal = reports.First(r => r.GetProperty("id").GetString() == "0x10");
        var addr = reports.First(r => r.GetProperty("id").GetString() == "0x04");
        var patch = addr.GetProperty("patch")[0];
        return new Answers(
            cal.GetProperty("pages").EnumerateArray().Select(x => Convert.FromHexString(x.GetString()!)).ToList(),
            Convert.FromHexString(addr.GetProperty("data").GetString()!), addr.GetProperty("size").GetInt32(),
            patch.GetProperty("source").GetString(), patch.GetProperty("at").GetInt32());
    }

    /// <summary>The calibration blob psmoveapi assembles from 0x10's three
    /// answers: the first whole, the second and third without their ID and
    /// index bytes, placed by index (psmove.c:974-1022).</summary>
    static byte[]? CalibrationBlob(List<byte[]> pages)
    {
        var blob = new byte[49 * 3 - 4];
        foreach (var page in pages)
        {
            if (page.Length != 49 || page[0] != 0x10) return null;
            switch (page[1])
            {
                case 0x00: page.CopyTo(blob, 0); break;
                case 0x01: page.AsSpan(2).CopyTo(blob.AsSpan(49)); break;
                case 0x82: page.AsSpan(2).CopyTo(blob.AsSpan(96)); break;
                default: return null;
            }
        }
        return blob;
    }

    static void PsMoveAnswers()
    {
        Console.WriteLine();
        Console.WriteLine("--- answers: ps-move's feature answers, prepared for the answer tables ---");
        var a = FeatureAnswers();
        Check("ps-move-feature-answers.json is beside the probe and reads", a != null);
        if (a == null) return;
        var blocks = WikiCalibrationSample.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Select(Printed).ToList();
        Check("0x10's three pages are the wiki's three sample blocks, 49 bytes each, indices 00, 01, 82",
              a.Pages.Count == 3 && blocks.Count == 3 && a.Pages.Zip(blocks).All(z => z.First.AsSpan().SequenceEqual(z.Second))
              && a.Pages.Select(x => x[1]).SequenceEqual(new byte[] { 0x00, 0x01, 0x82 }));
        var blob = CalibrationBlob(a.Pages);
        Check("the pages reassemble to the second of the wiki's eight dumps", blob != null && blob.AsSpan().SequenceEqual(Printed(WikiCalibrationDump)));

        // moveonpc HID reports, lines 9-11: "04 66 55 44 33 22 11 08 25 00
        // FF EE DD CC BB AA", the controller's address backwards at 1, the
        // host's at 10. The descriptor declares 0x04 with 0x0F data bytes.
        var d = a.Address;
        Check("0x04: 16 bytes with the ID, 08 25 00 at 7 to 9, the address 02:48:4D:00:00:<index> backwards from byte 1, the index patched into byte 1",
              a.AddressSize == 16 && d.Length <= 16 && d[0] == 0x04 && d[7] == 0x08 && d[8] == 0x25 && d[9] == 0x00
              && d[2] == 0x00 && d[3] == 0x00 && d[4] == 0x4D && d[5] == 0x48 && d[6] == 0x02
              && a.PatchSource == "index" && a.PatchAt == 1,
              Convert.ToHexString(d));
    }

    // ── dumps ────────────────────────────────────────────────────────────

    /// <summary>The LKML message's printout of a real Move's descriptor, as
    /// hid-example printed it (Pavel Machek, 2015-03-14, "Re: Fun with sony
    /// motion controllers: motion controller",
    /// lkml.rescloud.iu.edu/1503.1/05461.html).</summary>
    const string LkmlMoveDescriptor = @"5 1 9 5 a1 1 85 1 5 9 19 1 29 18 15 0 25 1 35 0 45 1 75 1 95 18 81 2 6
0 ff 9 24 75 8 95 3 81 2 5 1 9 30 9 31 9 32 9 35 15 0 26 ff 0 95 4 81
2 6 0 ff 9 20 95 26 81 2 85 2 9 21 95 30 91 2 85 6 9 27 95 8 91 2 85 3
9 23 95 8 b1 2 85 10 9 22 95 30 b1 2 85 e0 9 25 95 30 b1 2 85 11 9 26
95 30 b1 2 c0 6 2 ff 9 20 a1 1 9 21 85 4 15 0 26 ff 0 75 8 95 f b1 2 9
22 85 5 95 16 b1 2 c0 6 3 ff 9 20 a1 1 9 21 85 a0 15 0 26 ff 0 75 8 95
22 b1 2 9 22 85 a1 95 16 b1 2 c0";

    /// <summary>The folder that holds the reference clones: beside the
    /// repository, or beside the main checkout of a worktree, as
    /// device_oracle_check's case 9 finds them, or HIDMAESTRO_CORPUS_ROOT.</summary>
    static string? ReferenceRoot(string clone)
    {
        var candidates = new List<string>();
        string? env = Environment.GetEnvironmentVariable("HIDMAESTRO_CORPUS_ROOT");
        if (!string.IsNullOrEmpty(env)) candidates.Add(env);
        if (RepoRoot() is string root)
        {
            if (Path.GetDirectoryName(root) is string up) candidates.Add(up);
            // A worktree's .git file names its main checkout's git directory.
            string dotGit = Path.Combine(root, ".git");
            if (File.Exists(dotGit))
            {
                string line = File.ReadAllText(dotGit).Trim();
                const string lead = "gitdir:";
                if (line.StartsWith(lead, StringComparison.Ordinal))
                {
                    string gitDir = Path.GetFullPath(Path.Combine(root, line[lead.Length..].Trim()));
                    // <main>\.git\worktrees\<name>
                    var main = Directory.GetParent(gitDir)?.Parent?.Parent;
                    if (main?.Parent is { } mainUp) candidates.Add(mainUp.FullName);
                }
            }
        }
        return candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, clone)));
    }

    static void DescriptorDumps()
    {
        Console.WriteLine();
        Console.WriteLine("--- dumps: the descriptors against the dumps their notes cite ---");
        var db = ProfileDatabase.LoadEmbedded();
        var move = db.All.FirstOrDefault(x => x.Id == "ps-move");
        byte[] lkml = Printed(LkmlMoveDescriptor);
        Check($"ps-move's descriptor is the LKML message's printout byte for byte, {lkml.Length} bytes",
              move?.GetDescriptorBytes() is { } md && md.AsSpan().SequenceEqual(lkml));
        Check("ps-move's manufacturer and product strings are the capture's HID name, \"Sony Computer Entertainment Motion Controller\"",
              move?.ManufacturerString == "Sony Computer Entertainment" && move.ProductString == "Motion Controller",
              $"{move?.ManufacturerString} / {move?.ProductString}");
        Check("ps-move's layout claims no magnetometer, which its report leaves zero",
              move?.Layout is HMMotionWandLayout { Imu: { Magnetometer: false } },
              move?.Layout?.GetType().Name ?? "no layout");

        string? refs = ReferenceRoot("ControllersInfo");
        string bin = refs == null ? "" : Path.Combine(refs, "ControllersInfo", "xbox360", "xusb_flightstick_hid_report_descriptor.bin");
        if (refs == null || !File.Exists(bin))
        {
            Console.WriteLine("  [SKIP] the ControllersInfo clone is not beside the repository, so the flight stick's dump is not read.");
            return;
        }
        var fs = db.All.FirstOrDefault(x => x.Id == "xbox-360-flight-stick");
        byte[] dump = File.ReadAllBytes(bin);
        Check($"xbox-360-flight-stick's descriptor is {Path.GetFileName(bin)} byte for byte, {dump.Length} bytes",
              fs?.GetDescriptorBytes() is { } fd && fd.AsSpan().SequenceEqual(dump), bin);
    }
}
