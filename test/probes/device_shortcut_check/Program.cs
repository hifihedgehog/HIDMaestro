// The static check for device shortcuts in the SDK and the drivers.
//
// Offline, no device, no elevation. Loads the profile catalog through the
// SDK (ProfileDatabase.LoadEmbedded, public in HIDMaestro.Internal), and
// fails for every catalog profile the SDK embeds and does not load, so a
// profile a strict loader refuses never drops out of the rules unseen.
//
// What it reads: every .cs under sdk\HIDMaestro.Core, nested bin and obj
// folders included, since the build compiles them. The project's own
// top-level bin is skipped, and from its top-level obj only the sources a
// build generated there (the JSON context and the program kernels when a
// build emitted them), each content once. The same for the kernel build
// tool under sdk\HIDMaestro.KernelGen when it exists. Under driver\ (the
// UMDF2 driver, the companion, hmswd and the OpenVR driver, third_party
// included) it reads every C and C++ source and header by extension (.c
// .h .cpp .hpp .cc .hh .cxx .hxx .c++ .h++ .inl .ipp .tpp .tcc .inc .ixx
// .cppm .i .ii), every file a vcxproj there names as ClCompile or
// ClInclude, every file a build script under scripts\ hands cl.exe from
// driver\, every file a scanned source #includes by a quoted path whatever
// its extension, and the .rc and INF files. The vendored OpenVR header is read
// as any other file. Its hits of rules A to C are listed as allowed, with
// its reason, while its SHA-256 is the one Rules.Vendored pins. The
// tokenizer is this probe's own (Lexer.cs), since the Roslyn packages are
// not in the offline package cache. Comments are removed, keywords are
// never names, and each string is one token.
//
// It fails on:
//   1. A file, or a declared name of any kind (types, members, functions,
//      fields, macros, typedefs, enum members, locals and parameters, as
//      Declarations.cs and Locals.cs list them), holding a device or
//      vendor word. The words are the catalog's (Support.cs, DeviceWords):
//      every vendor folder under profiles\ and every word of each profile's
//      id, name, vendor, product, manufacturer and description strings,
//      less the generic words Rules.GenericWords lists with a reason, plus
//      Rules.ExtraWords (the earlier list and the PS and NS families). A
//      name is split into words at underscores and other
//      non-alphanumerics, before an uppercase letter that follows a
//      lowercase letter or a digit, before the last uppercase letter of a
//      run that a lowercase letter follows, and between letters and
//      digits. It holds a word when one of its words is the word, when a
//      run of its words spells a word the catalog writes with a capital
//      inside, or when one of its words holds the word inside a lowercase
//      compound:
//        Switch2ProDevice     Switch 2 Pro Device      switch
//        g_SonyCalibration    g Sony Calibration       sony
//        ds3Raw               ds 3 Raw                 ds3
//        Ps5Thing             Ps 5 Thing               ps5
//        IsDualSenseEdge      Is Dual Sense Edge       dualsense
//        EightBitDoFix        Eight Bit Do Fix         eightbitdo
//        Dualsensehelper      Dualsensehelper          dualsense
//        sixAxisParams        six Axis Params          none
//        deckle               deckle                   none
//   2. A vendor or product ID. A value compared with ==, != or an is
//      pattern, switched on as a case label or arm, assigned to an
//      expression named for an ID, or passed where the called function's
//      parameter is named for one, hits whatever it is, when it is a hex
//      literal of four or more digits, an expression of literals
//      ((0x20 << 8) | 0x09), a const, static readonly or #define, any
//      nonzero value when the name is unmistakable, or a catalog ID of
//      three or more hex digits written any way (1406, 0x57E,
//      unchecked((ushort)0x1057E)). A value is a literal, an expression of
//      literals, or a name declared with one: a const, static readonly or
//      #define from any file, or a local or field initialized once in the
//      same file (int v = 1406). A named value that hits also hits where it
//      is declared. A name is for a product ID when its last word is pid or
//      productid or its last two are product and id (ProductId, hwPid,
//      idProduct), and unmistakable unless it is a bare pid, which also
//      names process IDs and property keys. The same for vendor and vid. A
//      local or field assigned from an expression named for an ID is named
//      for that ID whatever its own name (ushort maker = profile.VendorId).
//      By value alone: a catalog vendor ID as a hex literal of four or more
//      digits or an expression of literals anywhere, a catalog product ID
//      beside the vendor ID the catalog pairs with it, four byte literals
//      in a row that spell a catalog vendor and product ID little-endian,
//      two byte comparisons joined by && (hi == 0x20 && lo == 0x09) and
//      two-byte initializer lists ({ 0x09, 0x20 }) that make a catalog ID
//      either way round beside an expression named for an ID, given to a
//      name for one, compared with bytes assigned from one, or compared
//      byte by byte with them, any four hex digits after VID_, PID_, &PID_
//      or VID&02 in a string, a string that is a catalog ID's four hex
//      digits, and a hex string parsed into a catalog ID.
//   3. A string holding a device word that is compared, searched
//      (Contains, IndexOf, StartsWith, EndsWith, Equals, strstr, wcscmp
//      and the like) or given to an identifier for a name or product
//      string, directly or through a const, static readonly or local that
//      holds it, which then hits where it is declared too. A string is
//      read after folding: literals and constants joined by +, adjacent C
//      literals and #define'd strings, an interpolated string of literal
//      holes, and string.Concat of literals. A regular expression's pattern
//      hits when its letters spell a device word (dual.?sense). Every other
//      string with a device word is listed as information.
//   4. A catalog profile id as a string, folded strings included, a device
//      word or an ID in an INF model line (a %token% read as its [Strings]
//      value), and a profile property exactly one catalog profile sets
//      (Profiles.cs) unless Rules.GeneralProperties names it.
//
//   5. Rule D, no exempt region. Nothing exempts a hit from 1 to 4. A
//      frozen-compat marker anywhere in a scanned file fails, in a comment,
//      in code or in an INF, in any spelling the two words can take
//      (frozen_compat, FrozenCompat, a Unicode hyphen, split across a
//      concatenation). And each region of 9bd6279's drivers that v2.0.0
//      deletes (Rules.Regions: the 24 of decision (c) and the default
//      device's five) fails while any of its prints is in a C or C++
//      source under driver\, whatever the file. A print is a window of the
//      region's token stream across its lines, as written or with its
//      names as placeholders, that holds content of the region's own
//      (RegionPrints.cs says which, and what still evades it). The prints
//      are in removed-regions.txt, pinned by its SHA-256 in Rules.cs. The
//      output lists each region with how many of its prints remain and
//      where.
//
// Allowed, and printed with every use: the names in Rules.Names, each
// exactly as written in the file its path names, the INF model lines in
// Rules.InfLines, and the vendored file's hits, each with its reason.
//
// Fixtures with known hits run first and fail the check if the rules give
// anything else. "--expect-baseline" passes only when every hit the plan
// and its review list for 9bd6279 is present, every removed region is
// found, and the allowed INF lines are found, so it is run against that
// tree. "--names" prints every declared name read, "--words" every device
// word with its source and every generic word left out with its reason.
// "--root <dir>" checks another tree than the one this exe sits in.
// "--write-regions <tree> <file>" writes the prints from a tree whose
// driver\ is 9bd6279's and prints the SHA-256 Rules.RegionPrintsSha256
// must then hold.
//
// Exit 0 PASS, 1 FAIL, 2 when the source tree is not beside the exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class Program
{
    static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        int writeArg = Array.IndexOf(args, "--write-regions");
        if (writeArg >= 0)
        {
            if (writeArg + 2 >= args.Length) { Console.WriteLine("usage: DeviceShortcutCheck --write-regions <tree at 9bd6279> <file>"); return 1; }
            return WriteRegions(Path.GetFullPath(args[writeArg + 1]), Path.GetFullPath(args[writeArg + 2]));
        }
        Console.WriteLine("=== device_shortcut_check: device shortcuts in the SDK and driver sources ===");
        int rootArg = Array.IndexOf(args, "--root");
        string? root = rootArg >= 0 && rootArg + 1 < args.Length ? Path.GetFullPath(args[rootArg + 1]) : RepoRoot();
        if (root == null || !File.Exists(Path.Combine(root, "sdk", "HIDMaestro.Core", "HIDMaestro.Core.csproj"))
            || !Directory.Exists(Path.Combine(root, "driver")))
        {
            Console.WriteLine("  [SKIP] the source tree (sdk\\HIDMaestro.Core and driver) is not beside this exe, so there is nothing to check.");
            return 2;
        }
        var cat = Catalog.Load(root);
        Console.WriteLine($"  catalog: {cat.All.Count} profiles, {cat.Vids.Count} vendor IDs, {cat.Pids.Count} product IDs (driverPid included), "
                          + $"{cat.NotLoaded.Count} embedded profile(s) not loaded");
        Console.WriteLine($"  device words: {cat.Words.Single.Count + cat.Words.Spanning.Count} ({cat.Words.Spanning.Count} spelled across words, "
                          + $"{cat.Words.Compound.Count} matched inside a compound), {cat.Words.Removed.Count} catalog words left out as generic (--words lists them)");
        if (args.Contains("--words")) PrintWords(cat.Words);

        bool selfTestOk = Fixtures.Run(cat);

        RegionPrints prints;
        try { prints = RegionPrints.Load(Path.Combine(AppContext.BaseDirectory, RegionPrints.FileName), Rules.Regions, Rules.RegionPrintsSha256); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.WriteLine($"  [FAIL] {ex.Message}");
            Console.WriteLine("device_shortcut_check: FAIL, the removed regions' prints cannot be read");
            return 1;
        }
        Console.WriteLine($"  removed regions: {Rules.Regions.Length}, {prints.ByRegion.Values.Sum(v => v.Count)} prints ({RegionPrints.FileName}, SHA-256 as pinned), "
                          + $"{prints.External.Count} names a shape keeps");
        var scan = new Scan(cat, prints, Rules.Regions, Rules.Names, Rules.InfLines, Rules.Vendored);
        var counts = new Dictionary<SourceKind, int>();
        var generated = new List<string>();
        var sdkTokens = new List<List<Token>>();
        string sdkRoot = Path.Combine(root, "sdk", "HIDMaestro.Core");
        foreach (var (path, kind, scanRoot, isGenerated) in Sources(root))
        {
            string rel = Path.GetRelativePath(root, path);
            string text = File.ReadAllText(path);
            scan.Add(rel, Path.GetRelativePath(scanRoot, path), text, kind);
            if (kind == SourceKind.CSharp && scanRoot == sdkRoot) sdkTokens.Add(new Lexer(text, Lang.CSharp).Run().Tokens);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (isGenerated) generated.Add(rel);
        }
        Console.WriteLine($"  sources: {counts.GetValueOrDefault(SourceKind.CSharp)} C#, {counts.GetValueOrDefault(SourceKind.C)} C and C++, "
                          + $"{counts.GetValueOrDefault(SourceKind.Rc)} .rc and {counts.GetValueOrDefault(SourceKind.Inf)} INF files, tokenizer: this probe's own");
        Console.WriteLine(generated.Count == 0
            ? "  generated sources under sdk\\HIDMaestro.Core\\obj: none"
            : $"  generated sources under sdk\\HIDMaestro.Core\\obj: {string.Join(", ", generated)}");
        scan.Run();
        foreach (var (resource, id) in cat.NotLoaded)
            scan.Hits.Add(new Hit("profiles", 0, HitGroups.Catalog, "embedded and not loaded", id, resource));

        // The profile properties one catalog profile sets, with the values of
        // enumerated keys and the keys of each field type.
        var literals = Profiles.ComparedLiterals(sdkTokens);
        var members = Profiles.MemberReferences(sdkTokens);
        var census = Profiles.Census(cat.All, new Profiles.Finer(
            (key, value, type) => type.IsEnum ? members.Contains((type.Name, value))
                                : literals.TryGetValue(key[(key.IndexOf('.') + 1)..], out var set) && set.Contains(value),
                                                                 Rules.Discriminators.Select(d => d.Property).ToArray()));
        var single = census.Where(u => u.Count == 1).ToList();
        var general = new List<(PropertyUse Use, GeneralProperty Entry)>();
        foreach (var u in single)
        {
            var entry = Rules.GeneralProperties.FirstOrDefault(g => g.Matches(u.Property));
            if (entry != null) general.Add((u, entry));
            else scan.Hits.Add(new Hit("profiles", 0, HitGroups.Property, "set by one catalog profile",
                                       $"{u.Property} (\"{u.JsonName}\")", u.Profile));
        }

        if (args.Contains("--names"))
        {
            Console.WriteLine($"-- every declared name ({scan.Names.Count})");
            foreach (var (file, d) in scan.Names) Console.WriteLine($"  {file}:{d.Line}  {d.Kind}  {d.Name}");
        }

        var failing = scan.Failing;
        foreach (string group in HitGroups.All)
        {
            var g = failing.Where(h => h.Group == group).ToList();
            Console.WriteLine($"-- {group} ({g.Count})");
            foreach (var h in g)
                Console.WriteLine(h.Group == HitGroups.Property
                    ? $"  [FAIL] {h.Name}  set only by {h.Text}"
                    : $"  [FAIL] {h.File}:{h.Line}  {h.Kind}  {h.Name}  | {h.Text}");
        }

        Console.WriteLine("-- allowed names");
        foreach (var entry in Rules.Names)
        {
            var uses = scan.AllowedNames.Where(a => a.Entry == entry).ToList();
            Console.WriteLine($"  {string.Join(", ", entry.Names)} in {string.Join(", ", entry.Paths)}: {uses.Count}. {entry.Reason}");
            foreach (var u in uses) Console.WriteLine($"    {u.File}:{u.Line}  {u.Name}");
        }

        Console.WriteLine("-- allowed strings");
        foreach (var entry in Rules.Strings)
        {
            var uses = scan.AllowedStrings.Where(a => a.Entry == entry).ToList();
            Console.WriteLine($"  \"{entry.Text}\" in {entry.Path}: {uses.Count}. {entry.Reason}");
            foreach (var u in uses) Console.WriteLine($"    {u.Hit.File}:{u.Hit.Line}  {u.Hit.Kind}");
        }

        Console.WriteLine("-- allowed in vendored files (rules A to C)");
        foreach (var entry in Rules.Vendored)
        {
            var uses = scan.VendoredHits.Where(v => v.Entry == entry).Select(v => v.Hit).ToList();
            Console.WriteLine($"  {entry.Path}, SHA-256 {entry.Sha256[..16]}: {uses.Count} hits. {entry.Reason}");
            foreach (var g in uses.GroupBy(h => h.Group)) Console.WriteLine($"    {g.Count()} {g.Key}: {string.Join(", ", g.Select(h => h.Name).Distinct().Take(12))}{(g.Select(h => h.Name).Distinct().Count() > 12 ? ", ..." : "")}");
        }

        Console.WriteLine("-- allowed INF model lines");
        foreach (var entry in Rules.InfLines)
        {
            var uses = scan.AllowedInf.Where(a => a.Entry == entry).ToList();
            Console.WriteLine($"  {entry.File}: {entry.Text}: {uses.Count}. {entry.Reason}");
            foreach (var u in uses) Console.WriteLine($"    {u.File}:{u.Line}");
        }

        Console.WriteLine("-- removed regions (none is exempt, each fails while a print of it remains)");
        foreach (var r in Rules.Regions)
        {
            var found = scan.Regions.Where(f => f.Region == r).ToList();
            Console.WriteLine($"  {r.Name}  {r.File}:{r.Begin}-{r.End} at 9bd6279, {prints.Count(r.Name, 'x')} exact and {prints.Count(r.Name, 's')} shape prints, deleted by {r.Lane}: "
                              + (found.Count == 0 ? "gone" : string.Join("; ", found.Select(f => $"{f.Prints} still in {f.File}, from lines {Ranges(f.Lines)}"))));
        }

        Console.WriteLine("-- rule C also counts a key's value that code names as Type.Prop=value (an enum member the SDK writes, "
                          + $"or one of the {literals.Sum(kv => kv.Value.Count)} literals it compares {literals.Count} property names with), "
                          + "and a key per kind of its object as Type.Prop@kind, by:");
        foreach (var (prop, reason) in Rules.Discriminators) Console.WriteLine($"  {prop}. {reason}");
        Console.WriteLine($"-- allowed among the properties one profile sets ({general.Count} of {single.Count})");
        foreach (var (u, e) in general) Console.WriteLine($"  {u.Property} (\"{u.JsonName}\"), set only by {u.Profile}. {e.Reason}");

        var infoKinds = scan.Infos.GroupBy(i => KindOf(i.Kind)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal);
        Console.WriteLine($"-- information: {scan.Infos.Count} other strings hold a device token ("
                          + string.Join(", ", infoKinds.Select(g => $"{g.Count()} {g.Key}")) + ")");
        foreach (var i in scan.Infos) Console.WriteLine($"  {i.File}:{i.Line}  {i.Kind}  {i.Text}");
        Console.WriteLine($"-- information: {scan.Coincidences.Count} literals equal a catalog product ID in a comparison, switch or assignment and do not count, for example:");
        foreach (string e in scan.Coincidences.OrderBy(e => e.Contains("bare pid") ? 0 : 1).Take(10)) Console.WriteLine($"  {e}");

        int code = failing.Count == 0 && selfTestOk ? 0 : 1;
        Console.WriteLine(failing.Count == 0
            ? selfTestOk ? "device_shortcut_check: PASS" : "device_shortcut_check: FAIL, self-test"
            : $"device_shortcut_check: FAIL, {failing.Count} hits ("
              + string.Join(", ", HitGroups.All.Select(g => (g, n: failing.Count(h => h.Group == g))).Where(x => x.n > 0).Select(x => $"{x.n} {x.g}")) + ")");
        if (args.Contains("--expect-baseline")) code = ExpectBaseline(failing, scan) && selfTestOk ? 0 : 1;
        Console.WriteLine($"  ({sw.ElapsedMilliseconds} ms)");
        return code;
    }

    // 3, 4, 5, 9 as "3-5, 9".
    static string Ranges(int[] lines)
    {
        var sorted = lines.OrderBy(x => x).ToArray();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Length;)
        {
            int j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"{sorted[i]}" : $"{sorted[i]}-{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    // The prints of Rules.Regions from a tree whose driver\ is 9bd6279's,
    // over every C and C++ source the check reads there.
    static int WriteRegions(string tree, string file)
    {
        string driver = Path.Combine(tree, "driver");
        if (!Directory.Exists(driver)) { Console.WriteLine($"  no driver directory in {tree}"); return 1; }
        var sources = Sources(tree).Where(x => x.Kind == SourceKind.C && x.ScanRoot == driver)
            .Select(x => (Path.GetRelativePath(tree, x.Path), File.ReadAllText(x.Path))).ToList();
        var cat = Catalog.Load(tree);
        var prints = RegionPrints.Build(sources, Rules.Regions, Rules.Kept, name => cat.Words.Match(name).Hit, Console.Out);
        foreach (var r in Rules.Regions)
            if (prints.Count(r.Name) == 0) { Console.WriteLine($"  [FAIL] {r.Name} has no print, so the check could not see it"); return 1; }
        string sha = prints.Write(file, Rules.Regions, "a tree whose driver\\ is 9bd6279's");
        Console.WriteLine($"  wrote {file}: {prints.ByRegion.Values.Sum(v => v.Count)} prints, {prints.External.Count} names a shape keeps");
        Console.WriteLine($"  SHA-256 {sha}: Rules.RegionPrintsSha256 must hold it");
        return 0;
    }

    static void PrintWords(DeviceWords w)
    {
        Console.WriteLine($"-- device words ({w.Single.Count + w.Spanning.Count})");
        foreach (var (word, from) in w.From)
            Console.WriteLine($"  {word}{(w.Spanning.Contains(word) ? " (across words)" : "")}{(w.Compound.Contains(word) ? " (in compounds)" : "")}  from {from}");
        Console.WriteLine($"-- catalog words left out as generic ({w.Removed.Count})");
        foreach (var (word, from) in w.Removed)
            Console.WriteLine($"  {word}  from {from}. {Rules.GenericWords.FirstOrDefault(g => g.Words.Contains(word))?.Reason ?? Rules.ShortWordReason}");
        Console.WriteLine($"-- words a device word sits inside that are not one ({Rules.CompoundExceptions.Length})");
        foreach (var e in Rules.CompoundExceptions) Console.WriteLine($"  {e.Word}. {e.Reason}");
    }

    // "argument of Log" counts as "argument of a call", and so on.
    static string KindOf(string kind)
    {
        foreach (string p in new[] { "argument of", "attribute", "message of new", "initializer of" })
            if (kind.StartsWith(p, StringComparison.Ordinal)) return p switch
            {
                "argument of" => "call arguments",
                "attribute" => "attribute arguments",
                "message of new" => "exception messages",
                _ => "initializers",
            };
        return kind switch { "return value" => "return values", "concatenation" => "concatenations", _ => "others" };
    }

    // The directory holding sdk\HIDMaestro.Core, walking up from the exe.
    static string? RepoRoot()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "sdk", "HIDMaestro.Core", "HIDMaestro.Core.csproj"))) return d;
        return null;
    }

    // Extensions read as C or C++.
    static readonly HashSet<string> s_cExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".cxx", ".hxx", ".c++", ".h++", ".inl", ".ipp", ".tpp", ".tcc", ".inc",
        ".ixx", ".cppm", ".i", ".ii",
    };

    static IEnumerable<(string Path, SourceKind Kind, string ScanRoot, bool Generated)> Sources(string root)
    {
        // A project's sources: every .cs, nested bin and obj included,
        // without the project's own top-level bin and obj.
        static IEnumerable<string> Project(string dir) =>
            Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => Path.GetRelativePath(dir, f).Split(Path.DirectorySeparatorChar)[0] is var top
                            && !top.Equals("bin", StringComparison.OrdinalIgnoreCase) && !top.Equals("obj", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal);
        string sdk = Path.Combine(root, "sdk", "HIDMaestro.Core");
        foreach (string f in Project(sdk)) yield return (f, SourceKind.CSharp, sdk, false);
        // A build's generated sources: one copy of each content, since every
        // configuration and platform writes its own.
        string obj = Path.Combine(sdk, "obj");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(obj))
            foreach (string f in Directory.EnumerateFiles(obj, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                if (seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))))
                    yield return (f, SourceKind.CSharp, sdk, true);
        string kernelGen = Path.Combine(root, "sdk", "HIDMaestro.KernelGen");
        if (Directory.Exists(kernelGen))
            foreach (string f in Project(kernelGen)) yield return (f, SourceKind.CSharp, kernelGen, false);

        string driver = Path.Combine(root, "driver");
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var c = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(driver, "*", SearchOption.AllDirectories))
        {
            files.Add(f);
            if (s_cExtensions.Contains(Path.GetExtension(f))) c.Add(f);
        }
        bool Under(string f) => Path.GetFullPath(f).StartsWith(driver + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(f);
        // The files a vcxproj compiles or lists as headers.
        foreach (string proj in files.Where(f => Path.GetExtension(f).Equals(".vcxproj", StringComparison.OrdinalIgnoreCase)).ToList())
            foreach (Match mt in Regex.Matches(File.ReadAllText(proj), @"<(?:ClCompile|ClInclude)\s+Include=""([^""]+)"""))
            {
                string f = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(proj)!, mt.Groups[1].Value));
                if (Under(f)) c.Add(f);
            }
        // The files a build script hands cl.exe from driver\: a variable set
        // to a folder under driver, then %VAR%\path on a cl.exe command and
        // the lines its ^ continues.
        string scripts = Path.Combine(root, "scripts");
        if (Directory.Exists(scripts))
            foreach (string cmd in Directory.EnumerateFiles(scripts, "*.cmd"))
            {
                string[] lines = File.ReadAllLines(cmd);
                string text = string.Join("\n", lines);
                var compile = new System.Text.StringBuilder();
                for (int n = 0; n < lines.Length; n++)
                {
                    if (!Regex.IsMatch(lines[n], @"^\s*cl(\.exe)?\s", RegexOptions.IgnoreCase)) continue;
                    for (; n < lines.Length; n++)
                    {
                        compile.Append(lines[n]).Append('\n');
                        if (!lines[n].TrimEnd().EndsWith('^')) break;
                    }
                }
                foreach (Match set in Regex.Matches(text, @"set\s+""?(\w+)=%~dp0\.\.\\(driver[^""\r\n]*)""?", RegexOptions.IgnoreCase))
                {
                    string dir = Path.Combine(root, set.Groups[2].Value.Trim());
                    foreach (Match use in Regex.Matches(compile.ToString(), "%" + set.Groups[1].Value + @"%\\([^""\s\^]+)", RegexOptions.IgnoreCase))
                    {
                        string f = Path.GetFullPath(Path.Combine(dir, use.Groups[1].Value));
                        if (Under(f)) c.Add(f);
                    }
                }
            }
        // The files a scanned source #includes by a quoted path, until no new
        // one appears.
        var queue = new Queue<string>(c);
        while (queue.Count > 0)
        {
            string f = queue.Dequeue();
            foreach (Match mt in Regex.Matches(File.ReadAllText(f), @"^\s*#\s*include\s*""([^""]+)""", RegexOptions.Multiline))
            {
                string inc = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, mt.Groups[1].Value));
                if (Under(inc) && c.Add(inc)) queue.Enqueue(inc);
            }
        }
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (c.Contains(f)) yield return (f, SourceKind.C, driver, false);
            else if (ext == ".rc") yield return (f, SourceKind.Rc, driver, false);
            else if (ext == ".inf") yield return (f, SourceKind.Inf, driver, false);
        }
    }

    // What the plan and its review list at 9bd6279. Each entry is a file
    // name ("" for any), a line (0 for any), the groups that count and a
    // name (null for any).
    static bool ExpectBaseline(List<Hit> hits, Scan scan)
    {
        Console.WriteLine("-- expect-baseline: the hits the plan and its review list at 9bd6279");
        var expected = new List<(string File, int Line, string[] Groups, string? Name, string What)>();
        foreach (string f in new[] { "SwitchProPacker.cs", "Switch2ProPacker.cs", "Switch2ProDevice.cs", "SteamHardwareUpdater.cs" })
            expected.Add((f, 1, new[] { HitGroups.File }, f, "file"));
        var ids = new[] { HitGroups.Vendor, HitGroups.Product };
        foreach (var (f, l) in new[]
                 {
                     ("driver.c", 480), ("driver.c", 481), ("driver.c", 3126), ("driver.c", 3141), ("driver.c", 3244),
                     ("ControllerProfile.cs", 423), ("DeviceNodeCreator.cs", 109), ("DeviceNodeCreator.cs", 268),
                     ("DeviceNodeCreator.cs", 400), ("Switch2ProPacker.cs", 32), ("SwitchProPacker.cs", 30),
                     ("UsbipEmulatedDevice.cs", 1367), ("UsbipEmulatedDevice.cs", 1377), ("UsbipEmulatedDevice.cs", 1401),
                     ("companion.c", 962),
                 })
            expected.Add((f, l, ids, null, "comparison"));
        foreach (var (f, l) in new[] { ("driver.c", 2528), ("driver.c", 2529), ("companion.c", 458), ("companion.c", 459) })
            expected.Add((f, l, ids, null, "Xbox 360 default"));
        foreach (string n in new[] { "IsXboxBranded", "SteamFirmwareStamp" })
            expected.Add(("", 0, new[] { HitGroups.Name }, n, "member"));
        foreach (string n in new[]
                 {
                     "SwitchProtocol", "SwitchProtocolSeen", "SwitchProtocolHold", "SwitchInputMode", "SwitchImuEnabled",
                     "SwitchTimer", "SwitchMac", "SwitchReplies", "SwitchReplyCount", "SwitchReplyRead",
                     "SwitchStreamThread", "SwitchStreamStop", "Ds3Sixaxis", "Ds3Raw", "Ds3OutBody",
                 })
            expected.Add(("driver.h", 0, new[] { HitGroups.Name }, n, "driver field"));
        // The review's examples (FB6, FB15, N5).
        expected.Add(("HMController.cs", 272, new[] { HitGroups.Name }, "_switchProtocol", "field"));
        expected.Add(("UsbipEmulatedDevice.cs", 210, new[] { HitGroups.Name }, "_switch2", "field"));
        expected.Add(("driver.c", 1829, new[] { HitGroups.Name }, "nintendoLayout", "local"));
        expected.Add(("driver.c", 1964, new[] { HitGroups.Name }, "ds3Raw", "local"));
        expected.Add(("UsbipEmulatedDevice.cs", 1377, new[] { HitGroups.Name }, "ds4", "local"));
        expected.Add(("driver.c", 3141, new[] { HitGroups.Name }, "ds4", "local"));
        expected.Add(("SteamHardwareUpdater.cs", 59, new[] { HitGroups.Name }, "steam", "local"));
        expected.Add(("FeatureStubTable.cs", 43, new[] { HitGroups.Name }, "_steamStamp", "field"));
        foreach (int l in new[] { 409, 410, 411 })
            expected.Add(("ControllerProfile.cs", l, new[] { HitGroups.DeviceString }, null, "the \"xbox\" heuristic"));
        expected.Add(("DeviceOrchestrator.cs", 2168, new[] { HitGroups.Vendor }, "VID_045E", "vendor ID in a string"));
        expected.Add(("Switch2ProDevice.cs", 409, new[] { HitGroups.Vendor }, null, "split bytes"));
        expected.Add(("driver.c", 2537, new[] { HitGroups.DeviceString }, null, "product string"));
        expected.Add(("profiles", 0, new[] { HitGroups.Property }, "FeatureStubSpec.MessageByte", "single-profile property"));
        // SixaxisCompatible leaves the allowlist with the frozen paths.
        expected.Add(("ControllerProfile.cs", 0, new[] { HitGroups.Name }, "SixaxisCompatible", "member"));
        // Rule D: every region v2.0.0 deletes, still present at 9bd6279.
        foreach (var r in Rules.Regions)
            expected.Add((Path.GetFileName(r.File), 0, new[] { HitGroups.Removed }, r.Name, "removed region"));

        int missing = 0;
        foreach (var e in expected)
        {
            var match = hits.FirstOrDefault(h =>
                (e.File.Length == 0 || Path.GetFileName(h.File).Equals(e.File, StringComparison.OrdinalIgnoreCase))
                && (e.Line == 0 || h.Line == e.Line)
                && e.Groups.Contains(h.Group)
                && (e.Name == null || h.Name == e.Name || (h.Group == HitGroups.Property && h.Name.StartsWith(e.Name + " ", StringComparison.Ordinal))));
            if (match == null) missing++;
            string where = e.File.Length == 0 ? e.Name! : e.Line == 0 ? $"{e.File} {e.Name}" : $"{e.File}:{e.Line}";
            Console.WriteLine($"  [{(match != null ? "PASS" : "FAIL")}] {e.What} {where}{(match != null ? "  " + match.Kind + " " + match.Name : "")}");
        }
        // The XUSB INF's model lines, allowed by Rules.InfLines (N5).
        foreach (int l in new[] { 47, 48, 49 })
        {
            bool found = scan.AllowedInf.Any(a => Path.GetFileName(a.File).Equals("hidmaestro_xusb.inf", StringComparison.OrdinalIgnoreCase) && a.Line == l);
            if (!found) missing++;
            Console.WriteLine($"  [{(found ? "PASS" : "FAIL")}] allowed INF model line hidmaestro_xusb.inf:{l}");
        }
        int listed = expected.Count + 3;
        Console.WriteLine(missing == 0
            ? $"device_shortcut_check --expect-baseline: PASS, {listed} listed hits and allowances present"
            : $"device_shortcut_check --expect-baseline: FAIL, {missing} of {listed} listed hits and allowances missing");
        return missing == 0;
    }
}
