// The driver store listing (PnputilHelper.ListPackages). It reads SetupAPI,
// so it answers the same way on every Windows version: pnputil prints its
// locale-stable form, /format xml, on Windows 11 build 26200, and the pnputil
// of Windows 10 build 19044 answers that option with its usage text and exit
// code 1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using HIDMaestro.Internal;

internal static partial class Program
{
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupGetInfPublishedNameW(string driverStoreLocation, StringBuilder returnBuffer, int returnBufferSize, out int requiredSize);

    static void StoreListingOffline()
    {
        // A directory that cannot be read fails the listing, naming it.
        string missing = Path.Combine(Path.GetTempPath(), "hm-nolist-" + Guid.NewGuid().ToString("N"));
        Exception? thrown = null;
        try { PnputilHelper.ListPackages(_ => true, missing); }
        catch (Exception e) { thrown = e; }
        Check("a listing whose INF directory cannot be read throws, naming the directory",
              thrown is InvalidOperationException && thrown.Message.Contains(missing, StringComparison.OrdinalIgnoreCase),
              thrown == null ? "returned" : $"{thrown.GetType().Name}: {thrown.Message}");
        Check("and is no listing at TryEnumerateDrivers", PnputilHelper.TryEnumerateDrivers(_ => true, missing) == null);

        // A published INF the driver store places no package for
        // (ERROR_NOT_FOUND) is not listed, and the listing stays whole.
        string dir = Path.Combine(Path.GetTempPath(), "hm-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string inf = "[Version]\r\nSignature=\"$Windows NT$\"\r\nProvider=%P%\r\nDriverVer=8/7/2026,1.0005.0.46\r\n[Strings]\r\nP=\"HIDMaestro\"\r\n";
            File.WriteAllText(Path.Combine(dir, "oem98765.inf"), inf);
            var none = PnputilHelper.TryEnumerateDrivers(_ => true, dir);
            Check("a published INF the driver store places no package for is not listed, and the listing is whole",
                  none is { Count: 0 }, none == null ? "the listing failed" : $"{none.Count} package(s)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        string a = PnputilHelper.DriverVersionText("8/7/2026", "1.0005.0.46"),
               b = PnputilHelper.DriverVersionText("10/18/2026", "2.0.0.1312"),
               c = PnputilHelper.DriverVersionText("", "");
        Check("a DriverVer line reads as pnputil prints it: MM/dd/yyyy and the version as numbers",
              a == "08/07/2026 1.5.0.46" && b == "10/18/2026 2.0.0.1312" && c == "", $"{a} | {b} | [{c}]");

        StoreListingAgainstPnputil();
    }

    /// <summary>The listing against pnputil's own on this machine's driver
    /// store: pnputil's XML where it prints one, which names its fields the
    /// same in every language, and its plain text otherwise. The plain text
    /// is read by its English labels, the language of both bench
    /// machines.</summary>
    static void StoreListingAgainstPnputil()
    {
        // Another program can add or remove a package while pnputil reads
        // the store, and no listing taken before matches that. The listing
        // is taken again after pnputil's read, and a store that changed
        // between the two is read again, up to three times. A listing that
        // disagrees with pnputil over a store that held still fails.
        List<PnputilHelper.DriverRecord>? mine = null;
        string form = "";
        var theirs = new List<(string Published, string Original, string Provider, string Version)>();
        for (int read = 1; read <= 3; read++)
        {
            mine = PnputilHelper.TryEnumerateDrivers(_ => true);
            if (mine == null) break;
            (form, theirs) = PnputilListing();
            var after = PnputilHelper.TryEnumerateDrivers(_ => true);
            if (after != null && after.ToHashSet().SetEquals(mine)) break;
            Console.WriteLine($"  info: the driver store changed while pnputil read it, read {read} of 3");
        }
        Check("the driver store's packages can be listed on this Windows version", mine is { Count: > 0 },
              mine == null ? "the listing failed" : $"{mine.Count} package(s)");
        if (mine == null) return;

        Check($"pnputil /enum-drivers lists this driver store ({form})", theirs.Count > 0, $"{theirs.Count} record(s)");
        if (theirs.Count == 0) return;

        var theirsByKey = new Dictionary<(string, string), (string Provider, string Version)>();
        foreach (var t in theirs) theirsByKey[(t.Published.ToLowerInvariant(), t.Original.ToLowerInvariant())] = (t.Provider, t.Version);
        var mineKeys = new HashSet<(string, string)>(mine.Select(r => (r.PublishedName.ToLowerInvariant(), r.OriginalName.ToLowerInvariant())));

        var unmatched = new List<string>();
        foreach (var r in mine)
        {
            if (!theirsByKey.TryGetValue((r.PublishedName.ToLowerInvariant(), r.OriginalName.ToLowerInvariant()), out var t))
                unmatched.Add($"{r.PublishedName} ({r.OriginalName}) not in pnputil's listing");
            else if (t.Provider != r.ProviderName || t.Version != r.DriverVersion)
                unmatched.Add($"{r.PublishedName} ({r.OriginalName}): pnputil [{t.Provider}, {t.Version}], listing [{r.ProviderName}, {r.DriverVersion}]");
        }
        Check($"every package the listing names, {mine.Count}, pnputil lists with the same original name, provider and DriverVer",
              unmatched.Count == 0, string.Join(" | ", unmatched.Take(8)));

        // What pnputil lists and the listing leaves out must be a package of
        // another architecture, which its FileRepository folder, named with
        // the architecture, publishes, or a driver store entry whose INF
        // file and FileRepository folder are both gone. Such an entry must
        // still resolve by its published name, as a devnode's driver is
        // looked up (WinUsbBinding.IsOurPackage).
        var foreign = ForeignArchitecturePublished();
        string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var missed = theirsByKey.Keys.Where(k => !mineKeys.Contains(k)).ToList();
        var otherArch = missed.Where(k => foreign.Contains(k.Item1)).ToList();
        var entryOnly = new List<(string, string)>();
        var unexplained = new List<string>();
        foreach (var k in missed.Except(otherArch))
        {
            string? store = null;
            try { store = PnputilHelper.DriverStorePathOf(k.Item1); }
            catch (InvalidOperationException e) { unexplained.Add($"{k.Item1} ({k.Item2}): {e.Message}"); continue; }
            bool infGone = !File.Exists(Path.Combine(infDir, k.Item1));
            bool folderGone = store != null && !Directory.Exists(Path.GetDirectoryName(store));
            if (infGone && folderGone && string.Equals(Path.GetFileName(store), k.Item2, StringComparison.OrdinalIgnoreCase)
                && PnputilHelper.ProviderOf(k.Item1) == null)
                entryOnly.Add(k);
            else
                unexplained.Add($"{k.Item1} ({k.Item2}): INF {(infGone ? "gone" : "present")}, store path {store ?? "none"}, folder {(folderGone ? "gone" : "present")}");
        }
        Check($"every package pnputil lists, {theirsByKey.Count}, is in the listing, of another architecture ({otherArch.Count}), or an entry whose files are gone that resolves by its published name ({entryOnly.Count})",
              unexplained.Count == 0,
              unexplained.Count == 0
                  ? $"other architecture: {string.Join(", ", otherArch.Select(k => $"{k.Item1} ({k.Item2})"))}"
                  : string.Join(" | ", unexplained.Take(8)));

        // A devnode's driver is told HIDMaestro's by its published name
        // (WinUsbBinding.IsOurPackage): a package of HIDMaestro's WinUSB INF
        // name and provider, or an entry of that name whose files are gone.
        // Every other package is not.
        var wrong = new List<string>();
        int ourListed = 0, ourEntries = 0;
        foreach (var r in mine)
        {
            bool expected = r.OriginalName.StartsWith(WinUsbBinding.PackagePrefix, StringComparison.OrdinalIgnoreCase)
                            && r.ProviderName.Equals(WinUsbBinding.Provider, StringComparison.OrdinalIgnoreCase);
            if (expected) ourListed++;
            if (WinUsbBinding.IsOurPackage(r.PublishedName) is var got && got != expected)
                wrong.Add($"{r.PublishedName} ({r.OriginalName}, {r.ProviderName}): {got?.ToString() ?? "unknown"}");
        }
        foreach (var k in entryOnly)
        {
            bool expected = k.Item2.StartsWith(WinUsbBinding.PackagePrefix, StringComparison.OrdinalIgnoreCase);
            if (expected) ourEntries++;
            if (WinUsbBinding.IsOurPackage(k.Item1) is var got && got != expected)
                wrong.Add($"{k.Item1} ({k.Item2}, files gone): {got?.ToString() ?? "unknown"}");
        }
        Check($"a devnode's driver is told HIDMaestro's by its published name: {ourListed} of {mine.Count} listed packages, {ourEntries} of {entryOnly.Count} entries whose files are gone",
              wrong.Count == 0, string.Join(" | ", wrong.Take(8)));

        // HIDMaestro's own names, the ones its listings ask for.
        bool Ours(string original) =>
            original.Equals("hidmaestro.inf", StringComparison.OrdinalIgnoreCase)
            || original.Equals("hidmaestro_xusb.inf", StringComparison.OrdinalIgnoreCase)
            || original.Equals("usbip2_ude.inf", StringComparison.OrdinalIgnoreCase)
            || original.StartsWith(WinUsbBinding.PackagePrefix, StringComparison.OrdinalIgnoreCase);
        int theirsOurs = theirsByKey.Keys.Count(k => Ours(k.Item2));
        int mineOurs = mine.Count(r => Ours(r.OriginalName));
        Check($"the packages of the names HIDMaestro reads are the same in both: {theirsOurs} in pnputil's, {mineOurs} in the listing",
              theirsOurs == mineOurs && theirsOurs > 0);

        // What the listing costs for the names a deploy reads, 30 runs.
        var ms = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            var sw = Stopwatch.StartNew();
            PnputilHelper.ListPackages(n => n.Equals("hidmaestro.inf", StringComparison.OrdinalIgnoreCase)
                                         || n.Equals("hidmaestro_xusb.inf", StringComparison.OrdinalIgnoreCase));
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
        ms.Sort();
        Console.WriteLine(FormattableString.Invariant(
            $"  the listing of HIDMaestro's two INFs, 30 runs: p50 {ms[14]:F1} ms, p95 {ms[28]:F1} ms, max {ms[29]:F1} ms"));
    }

    /// <summary>pnputil's listing of the driver store and the form it was
    /// read in: /format xml where pnputil prints it, else the plain text
    /// read by its English labels.</summary>
    static (string Form, List<(string Published, string Original, string Provider, string Version)> Records) PnputilListing()
    {
        var (xrc, xml) = PnputilHelper.Run("/enum-drivers /format xml", timeoutMs: 60_000);
        if (xrc == 0)
        {
            try
            {
                var records = XDocument.Parse(xml).Root?.Elements("Driver")
                    .Select(d => ((string?)d.Attribute("DriverName") ?? "", (string?)d.Element("OriginalName") ?? "",
                                  (string?)d.Element("ProviderName") ?? "", (string?)d.Element("DriverVersion") ?? ""))
                    .Where(r => r.Item1.Length > 0)
                    .ToList();
                if (records != null) return ("/format xml", records);
            }
            catch (System.Xml.XmlException) { }
        }
        var (rc, text) = PnputilHelper.Run("/enum-drivers", timeoutMs: 60_000);
        return (rc == 0 ? $"plain text with English labels, /format xml exited {xrc}" : $"plain text exited {rc}",
                rc == 0 ? PnputilText(text) : new());
    }

    /// <summary>pnputil's plain-text listing: one record per package, read
    /// from the English labels.</summary>
    static List<(string Published, string Original, string Provider, string Version)> PnputilText(string text)
    {
        var list = new List<(string, string, string, string)>();
        string? published = null, original = null, provider = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (Labeled(line, "Published Name:") is { } p) { published = p; original = provider = null; }
            else if (Labeled(line, "Original Name:") is { } o) original = o;
            else if (Labeled(line, "Provider Name:") is { } v) provider = v;
            else if (Labeled(line, "Driver Version:") is { } d && published != null && original != null && provider != null)
                list.Add((published, original, provider, d));
        }
        return list;

        static string? Labeled(string line, string label) =>
            line.StartsWith(label, StringComparison.Ordinal) ? line[label.Length..].Trim() : null;
    }

    /// <summary>The published names of the driver store's packages of
    /// another architecture than this machine's, read from the
    /// FileRepository folders, whose names carry the architecture, with
    /// SetupGetInfPublishedName.</summary>
    static HashSet<string> ForeignArchitecturePublished()
    {
        string native = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64",
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string repo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        foreach (string dir in Directory.EnumerateDirectories(repo))
        {
            string name = Path.GetFileName(dir);
            int end = name.LastIndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
            if (end < 0) continue;
            string rest = name[(end + 5)..];
            int cut = rest.IndexOf('_');
            if (cut < 0 || rest[..cut].Equals(native, StringComparison.OrdinalIgnoreCase)) continue;
            string inf = Path.Combine(dir, name[..(end + 4)]);
            var sb = new StringBuilder(1024);
            if (File.Exists(inf) && SetupGetInfPublishedNameW(inf, sb, sb.Capacity, out _))
                names.Add(Path.GetFileName(sb.ToString()).ToLowerInvariant());
        }
        return names;
    }
}
