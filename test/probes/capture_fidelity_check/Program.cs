// capture_fidelity_check: catalog data and driver text against the
// captures and sources they come from. No device, no elevation.
//
//   inf        Every comment line of the two driver INFs is plain ASCII.
//              Ten lines carried a dash written as UTF-8 and read back as
//              Windows-1252, three stray characters in place of each dash.
//              Needs the source tree beside the exe, and is skipped with
//              a [SKIP] line when it is not there.
//
// Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

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
}
